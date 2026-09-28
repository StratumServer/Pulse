using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
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
    private const long RepeatMs = 10 * 60_000;
    private const int MaxKinds = 32;
    private const int ClipLength = 200;
    private const string Redacted = "***";

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
            // Only the first delivery, and the first one after a logged failure, are worth a line.
            if (reportSuccess)
            {
                reportSuccess = false;
                pending.Enqueue((true, $"Pulse OTLP export to {Clip(eventEndpoint)} succeeded."));
            }

            return;
        }

        if (eventData.Level > EventLevel.Warning)
        {
            return;
        }

        string cause = Cause(eventData);

        // gRPC's Detail text can change on every export (a request id, say), and a response's own
        // declared size (event 40) varies with the response itself; keying on the text before it
        // is what keeps either from earning a line, and a tracked kind, per export forever.
        string key = eventData.EventId + " " + TrimDetail(cause);
        long now = Environment.TickCount64;

        if (lastLogged.TryGetValue(key, out long last))
        {
            if (now - last < RepeatMs)
            {
                return;
            }
        }
        else if (lastLogged.Count >= MaxKinds)
        {
            // Stale entries no longer rate-limit anything, so they are the first thing to give up
            // before a new kind is dropped: without this, a server old enough to have once seen
            // 32 different kinds, all long since resolved, would refuse to log a brand new one
            // ever again.
            foreach (KeyValuePair<string, long> entry in lastLogged)
            {
                if (now - entry.Value >= RepeatMs)
                {
                    lastLogged.TryRemove(entry.Key, out _);
                }
            }

            // ponytail: only if every one of the 32 tracked kinds is still inside its own repeat
            // window is a new kind ever dropped outright; raise MaxKinds if a real deployment ever
            // needs more than that many distinct kinds live at once.
            if (lastLogged.Count >= MaxKinds)
            {
                return;
            }
        }

        lastLogged[key] = now;
        reportSuccess = true;
        string endpointText = Clip(eventEndpoint);
        string safeCause = Redact(cause);
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
    /// Pulse's own export. Exact match for http/protobuf, where the configured endpoint is the
    /// whole address; for grpc the exporter appends its own service path, so the base endpoint can
    /// only ever be a prefix.</summary>
    private bool IsOwnExport(string candidate) =>
        candidate == ownEndpoint
        || (candidate.StartsWith(ownEndpoint, StringComparison.Ordinal)
            && candidate.EndsWith("MetricsService/Export", StringComparison.Ordinal));

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

    /// <summary>Blanks every configured header value out of collector-controlled text, plus
    /// anything shaped like a bearer or basic credential regardless of whether it matches one of
    /// those values verbatim. The response body and the gRPC Detail field are the backend's own
    /// words, not the SDK's, so a backend that echoes what it was sent, by accident or not, must
    /// not be able to put a header value in the server log.</summary>
    private string Redact(string text)
    {
        string result = text;
        foreach (string secret in secrets)
        {
            if (secret.Length > 0)
            {
                result = result.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }

        try
        {
            result = CredentialScheme().Replace(result, $"$1 {Redacted}");
        }
        catch (RegexMatchTimeoutException)
        {
            // The exact-value pass above already removed every secret this mod itself configured;
            // a scheme this pass cannot finish checking in time is not one more chance to leak.
        }

        return result;
    }

    /// <summary>"Bearer " or "Basic " followed by a token, matched as plain non-whitespace: cheap,
    /// and every real bearer or basic credential is exactly that shape. csharpsquid:S6444 requires
    /// an explicit timeout on every regex match.</summary>
    [GeneratedRegex("(Bearer|Basic) \\S+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
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
