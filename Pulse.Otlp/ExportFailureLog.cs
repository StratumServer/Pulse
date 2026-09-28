using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulse.Otlp;

/// <summary>Puts the OTLP exporter's own failure reports in the server log.</summary>
/// <remarks>The exporter says why an export failed (a 401, a refused connection, a timeout) only
/// through its EventSource, on its own export thread, and hands the reader nothing else. This
/// listens to that source and queues one line per distinct failure, repeated at most every ten
/// minutes; the mod drains the queue from a main thread tick listener, so the export thread never
/// waits on the game's logger. Warnings, not errors: the engine counts every Error entry toward
/// DieAboveErrorCount, and a backend being down is no reason to move a server toward shutdown. A
/// healthy server logs exactly one "succeeded" line, at Notification, from its very first
/// delivery; that is expected, not a sign anything was ever failing.
///
/// The EventSource is process-wide: every OTLP exporter any mod builds writes to the same one, so
/// every event carries an endpoint (or, for a handful of shapes, none at all) that this checks
/// against Pulse's own before doing anything else with it.</remarks>
internal sealed partial class ExportFailureLog(IReadOnlyCollection<string> secrets, Uri endpoint) : EventListener
{
    private const string ExporterSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";
    private const int ExportSucceeded = 21;
    private const int ResponseTooLarge = 40;
    private const long RepeatMs = 10 * 60_000;
    private const int MaxKinds = 32;
    private const int ClipLength = 200;
    private const string Redacted = "***";
    private const string GrpcExportPath = "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export";

    // Field initialisers, not constructor assignments: the base EventListener constructor can
    // call OnEventSourceCreated, and through it EnableEvents, before any constructor body of ours
    // runs, and another thread can already be writing events by the time it returns. The SDK's
    // own SelfDiagnosticsEventListener carries the same warning for the same reason. secrets and
    // ownEndpoint below are initialised the same way, from this class's primary constructor
    // parameters, for the same reason: a value from a caller is exactly as unsafe to defer as one
    // built with `new()`.
    private readonly ConcurrentDictionary<string, long> lastLogged = new();
    private readonly ConcurrentQueue<(bool Success, string Line)> pending = new();
    private volatile bool reportSuccess = true;

    /// <summary>The configured header values, redacted out of anything a collector's response
    /// puts back in front of us. Never the header names: a name is not a secret, and logging it
    /// is how an admin sees which one to check.</summary>
    private readonly IReadOnlyCollection<string> secrets = secrets;

    /// <summary>Pulse's own resolved endpoint, in the same scheme+host+path shape the exporter's
    /// EventSource redacts every endpoint payload to, so it can be compared against one directly.
    /// </summary>
    private readonly string ownEndpoint = endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    /// <summary>The exact address a grpc export of Pulse's own reaches: the exporter appends
    /// <see cref="GrpcExportPath"/> to the configured endpoint unconditionally, so equality
    /// against this, not a prefix check against the base endpoint, is what tells Pulse's own grpc
    /// export apart from another mod's on a base endpoint that merely starts with the same
    /// characters (Pulse's "/pre" is a string prefix of some other mod's "/prefix" too). Recomputed
    /// from the constructor's own <c>endpoint</c> parameter rather than read from <see
    /// cref="ownEndpoint"/>: a field initialiser cannot reference another instance field.</summary>
    private readonly string ownGrpcExportPath =
        endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/') + GrpcExportPath;

    /// <summary>Hands every queued line to the matching callback, oldest first: <paramref
    /// name="onFailure"/> for a failure line, <paramref name="onSuccess"/> for the "succeeded"
    /// one. Call this from the main thread only; queuing, from the export thread, and draining are
    /// the only two operations that touch <see cref="pending"/>, and a ConcurrentQueue allows
    /// exactly that split.</summary>
    public void Drain(Action<string> onFailure, Action<string> onSuccess)
    {
        while (pending.TryDequeue(out (bool Success, string Line) entry))
        {
            if (entry.Success)
            {
                onSuccess(entry.Line);
            }
            else
            {
                onFailure(entry.Line);
            }
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == ExporterSource)
        {
            // Informational is also what flips the SDK's own IsEnabled(Error) check to true for
            // every exporter sharing this process-wide source, Pulse's own included: each one now
            // reads a failing response's body, up to 4 MiB, before this method ever sees it, work
            // that never happens at all while nothing is listening.
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    /// <summary>Runs on the exporter's own export thread: classify, queue, return. Never logs from
    /// here, and never blocks, so a slow game logger can never hold the export thread back.</summary>
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        string? eventEndpoint = Payload(eventData, "endpoint") ?? Payload(eventData, "rawCollectorUri");

        // No endpoint at all (an invalid OTEL_* setting, a batch dropped before it had one) could
        // still be Pulse's own export, but nothing here can tell it apart from another mod's, or
        // another signal's, sharing the same process-wide source; dropping it is safer than
        // attributing it to Pulse on a guess. A mismatched endpoint is always someone else's.
        if (eventEndpoint is null || !IsOwnExport(eventEndpoint))
        {
            return;
        }

        if (eventData.EventId == ExportSucceeded)
        {
            HandleSuccess(eventEndpoint);
            return;
        }

        if (eventData.Level > EventLevel.Warning)
        {
            return;
        }

        string cause = Cause(eventData);

        // Two shapes whose text would otherwise change on every single export: gRPC's Detail
        // field (a request id, say) is trimmed off the key by TrimDetail, and event 40's declared
        // response size, which is not part of Cause's text at all, is replaced outright rather
        // than trimmed. Either one earning a line, and a tracked kind, per export would defeat the
        // rate limit and the cap alike.
        string key = eventData.EventId + " " + KeyCause(eventData, cause);
        long now = Environment.TickCount64;
        if (ShouldSkip(key, now))
        {
            return;
        }

        lastLogged[key] = now;
        reportSuccess = true;
        EnqueueFailure(eventEndpoint, eventData, cause);
    }

    /// <summary>Only the first delivery, and the first one after a logged failure, are worth a
    /// line.</summary>
    private void HandleSuccess(string endpoint)
    {
        if (reportSuccess)
        {
            reportSuccess = false;
            pending.Enqueue((true, $"Pulse OTLP export to {Clip(endpoint)} succeeded."));
        }
    }

    /// <summary>Whether <paramref name="key"/> is still rate-limited: already logged inside its
    /// repeat window, or new but the cap has no room even after evicting whatever has aged out of
    /// its own window.</summary>
    private bool ShouldSkip(string key, long now)
    {
        if (lastLogged.TryGetValue(key, out long last))
        {
            return now - last < RepeatMs;
        }

        if (lastLogged.Count < MaxKinds)
        {
            return false;
        }

        // Stale entries no longer rate-limit anything, so they are the first thing to give up
        // before a new kind is dropped: without this, a server old enough to have once seen 32
        // different kinds, all long since resolved, would refuse to log a brand new one ever
        // again.
        EvictStaleKinds(now);

        // ponytail: only if every one of the 32 tracked kinds is still inside its own repeat
        // window is a new kind ever dropped outright; raise MaxKinds if a real deployment ever
        // needs more than that many distinct kinds live at once.
        return lastLogged.Count >= MaxKinds;
    }

    private void EvictStaleKinds(long now)
    {
        foreach (KeyValuePair<string, long> entry in lastLogged)
        {
            if (now - entry.Value >= RepeatMs)
            {
                lastLogged.TryRemove(entry.Key, out _);
            }
        }
    }

    private void EnqueueFailure(string endpoint, EventWrittenEventArgs eventData, string cause)
    {
        // Redact first, clip second, for both: clipping first could cut a secret in half and
        // leave the surviving half unredacted. The cause needs this too, not only the response
        // body, since a gRPC Detail field or an HTTP reason phrase is exactly as backend-controlled
        // and exactly as unbounded.
        string endpointText = Clip(endpoint);
        string safeCause = Clip(Redact(cause));
        string response = Payload(eventData, "response") is { Length: > 0 } body
            ? $" The backend answered: {Clip(Redact(body))}"
            : string.Empty;
        pending.Enqueue((
            false,
            $"Pulse OTLP export to {endpointText} failed: {safeCause}.{response} Metrics are not reaching the "
            + "backend; check Endpoint and Headers in pulse-otlp.json. This is logged again at most "
            + "every 10 minutes."));
    }

    /// <summary>Whether <paramref name="candidate"/>, an endpoint or rawCollectorUri payload, is
    /// Pulse's own export: an exact match against either the http/protobuf address or the one
    /// exact address a grpc export reaches. Two exporters sending to the very same collector URL
    /// are inherently indistinguishable from here, a known and accepted limit; everything short of
    /// that exact collision is exact matching on purpose, not a prefix check, so a base endpoint
    /// that is merely a string prefix of another mod's never passes.</summary>
    private bool IsOwnExport(string candidate) => candidate == ownEndpoint || candidate == ownGrpcExportPath;

    /// <summary>The first line of whichever payload says why: the exception text or the gRPC
    /// status, with the exception type stripped off the front of it.</summary>
    private static string Cause(EventWrittenEventArgs e)
    {
        for (int i = (e.Payload?.Count ?? 0) - 1; i >= 0; i--)
        {
            string name = e.PayloadNames![i];
            if (e.Payload![i] is string text
                && (name.StartsWith("ex", StringComparison.Ordinal) || name == "statusString"))
            {
                string line = text.Split('\n')[0].Trim().TrimEnd('.');
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                return colon > 0 && line[..colon].EndsWith("Exception", StringComparison.Ordinal)
                    ? line[(colon + 2)..]
                    : line;
            }
        }

        // No exception or status in this event (an invalid OTEL_* setting, a response discarded
        // for its size): its own message template already says what happened.
        string message = e.Message is null
            ? e.EventName ?? "event " + e.EventId
            : string.Format(CultureInfo.InvariantCulture, e.Message, e.Payload?.ToArray() ?? []);
        return message.Split('\n')[0].Trim().TrimEnd('.');
    }

    /// <summary>The text a failure's rate-limit and cap key is built from: stable across repeats
    /// of the same kind, even where the displayed cause is not.</summary>
    private static string KeyCause(EventWrittenEventArgs eventData, string cause) =>
        eventData.EventId == ResponseTooLarge ? "response discarded for its size" : TrimDetail(cause);

    private static string TrimDetail(string cause)
    {
        int detail = cause.IndexOf(", Detail=", StringComparison.Ordinal);
        return detail < 0 ? cause : cause[..detail];
    }

    private static string? Payload(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i >= 0 ? e.Payload![i] as string : null;
    }

    /// <summary>Blanks every configured header value, and the credential half of it where the
    /// value has a scheme, out of collector-controlled text, plus anything shaped like a bearer or
    /// basic credential regardless of whether it matches one of those values verbatim. The
    /// response body and the gRPC Detail field are the backend's own words, not the SDK's, so a
    /// backend that echoes what it was sent, in whole, in part, or JSON-escaped, by accident or
    /// not, must not be able to put a header value in the server log. This is not exhaustive: a
    /// backend transforming a secret some other way (hashing it, say, or splitting it across two
    /// fields) could still get it into the log, which is why the log itself stays worth treating as
    /// sensitive before sharing it.</summary>
    private string Redact(string text)
    {
        string result = text;
        foreach (string secret in secrets)
        {
            foreach (string target in RedactionTargets(secret))
            {
                result = result.Replace(target, Redacted, StringComparison.Ordinal);
            }
        }

        try
        {
            result = CredentialScheme().Replace(result, $"$1 {Redacted}");
        }
        catch (RegexMatchTimeoutException)
        {
            // Best effort only: keep whatever the exact-value pass above already redacted.
        }

        return result;
    }

    /// <summary>Every string worth searching for in collector-controlled text on account of one
    /// configured header value: the value itself, trimmed the way the exporter trims it before
    /// sending (the SDK trims a header value; this reads the raw configured one), the credential
    /// alone when the value has an "scheme credential" shape (an echo can drop the scheme), and the
    /// JSON-escaped form of each, since a backend's own JSON error body is exactly where an echo
    /// shows up.</summary>
    private static IEnumerable<string> RedactionTargets(string configuredValue)
    {
        string trimmed = configuredValue.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        foreach (string variant in EscapedForms(trimmed))
        {
            yield return variant;
        }

        int space = trimmed.IndexOf(' ');
        if (space > 0 && space < trimmed.Length - 1)
        {
            foreach (string variant in EscapedForms(trimmed[(space + 1)..]))
            {
                yield return variant;
            }
        }
    }

    /// <summary>A value as configured, plus the same value the way it can come back JSON-encoded:
    /// System.Text.Json's own escaping, and a literal "/" written as "\/", which System.Text.Json
    /// does not produce but other JSON encoders (PHP's, notably) do by default.</summary>
    private static IEnumerable<string> EscapedForms(string value)
    {
        yield return value;

        string jsonEscaped = JsonEncodedText.Encode(value).ToString();
        if (jsonEscaped != value)
        {
            yield return jsonEscaped;
        }

        string slashEscaped = value.Replace("/", "\\/", StringComparison.Ordinal);
        if (slashEscaped != value)
        {
            yield return slashEscaped;
        }
    }

    /// <summary>"Bearer" or "Basic", either case, followed by a token68 credential (RFC 7235):
    /// letters, digits, "-._~+/", optionally padded with "=". Restricted to that shape, rather than
    /// to plain non-whitespace, so a credential embedded in a compact JSON body (immediately
    /// followed by '"' or '}', neither of which is token68) is what gets redacted, not everything
    /// up to the next space. csharpsquid:S6444 requires an explicit timeout on every regex match.
    /// </summary>
    [GeneratedRegex("(Bearer|Basic) [A-Za-z0-9\\-._~+/]+=*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialScheme();

    /// <summary>First line only, and no longer than <see cref="ClipLength"/>: a backend's answer
    /// can be an arbitrary, unbounded body, and an endpoint is meant to be short already, but
    /// nothing here should be able to grow a log line without limit. Cuts one character earlier
    /// than the limit when that would otherwise split a surrogate pair in two, which would leave
    /// an unpaired high surrogate in the line.</summary>
    private static string Clip(string text)
    {
        string line = text.Split('\n')[0].Trim();
        if (line.Length <= ClipLength)
        {
            return line;
        }

        int cut = ClipLength;
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut] + "...";
    }
}
