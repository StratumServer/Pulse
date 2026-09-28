using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Pulse.Otlp;

/// <summary>Puts the OTLP exporter's own failure reports in the server log.</summary>
/// <remarks>The exporter says why an export failed (a 401, a refused connection, a timeout) only
/// through its EventSource, on its own export thread, and hands the reader nothing else. This
/// listens to that source and queues one line per distinct failure, repeated at most every ten
/// minutes; the mod drains the queue from a main thread tick listener, so the export thread never
/// waits on the game's logger. Warnings, not errors: the engine counts every Error entry toward
/// DieAboveErrorCount, and a backend being down is no reason to move a server toward
/// shutdown.</remarks>
internal sealed class ExportFailureLog : EventListener
{
    private const string ExporterSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";
    private const int ExportSucceeded = 21;
    private const long RepeatMs = 10 * 60_000;
    private const int MaxKinds = 32;
    private const int ClipLength = 200;

    // Field initialisers, not constructor assignments: the base EventListener constructor can
    // call OnEventSourceCreated, and through it EnableEvents, before any constructor body of ours
    // runs, and another thread can already be writing events by the time it returns. The SDK's
    // own SelfDiagnosticsEventListener carries the same warning for the same reason.
    private readonly ConcurrentDictionary<string, long> lastLogged = new();
    private readonly ConcurrentQueue<string> pending = new();
    private volatile bool reportSuccess = true;

    /// <summary>Hands every queued line to <paramref name="log"/>, oldest first. Call this from
    /// the main thread only; queuing, from the export thread, and draining are the only two
    /// operations that touch <see cref="pending"/>, and a ConcurrentQueue allows exactly that
    /// split.</summary>
    public void Drain(Action<string> log)
    {
        while (pending.TryDequeue(out string? line))
        {
            log(line);
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == ExporterSource)
        {
            EnableEvents(source, EventLevel.Informational);
        }
    }

    /// <summary>Runs on the exporter's own export thread: classify, queue, return. Never logs from
    /// here, and never blocks, so a slow game logger can never hold the export thread back.</summary>
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventId == ExportSucceeded)
        {
            // Only the first delivery, and the first one after a logged failure, are worth a line.
            if (reportSuccess)
            {
                reportSuccess = false;
                pending.Enqueue($"Pulse OTLP export to {Clip(Payload(e, "endpoint") ?? "the collector")} succeeded.");
            }

            return;
        }

        if (e.Level > EventLevel.Warning)
        {
            return;
        }

        string cause = Cause(e);
        string key = e.EventId + " " + cause;
        long now = Environment.TickCount64;

        // ponytail: a cause whose text changes on every export (a request id inside a gRPC status
        // detail, say) would otherwise earn a line per export forever. MaxKinds bounds memory at
        // the cost of a failure kind past the cap never being logged at all; raise it if a real
        // deployment ever needs more than 32 distinct kinds tracked at once.
        if (lastLogged.TryGetValue(key, out long last) ? now - last < RepeatMs : lastLogged.Count >= MaxKinds)
        {
            return;
        }

        lastLogged[key] = now;
        reportSuccess = true;
        string endpoint = Clip(Payload(e, "endpoint") ?? Payload(e, "rawCollectorUri") ?? "the collector");
        string response = Payload(e, "response") is { Length: > 0 } body
            ? $" The backend answered: {Clip(body)}"
            : string.Empty;
        pending.Enqueue(
            $"Pulse OTLP export to {endpoint} failed: {cause}.{response} Metrics are not reaching the "
            + "backend; check Endpoint and Headers in pulse-otlp.json. This is logged again at most "
            + "every 10 minutes.");
    }

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

    private static string? Payload(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i >= 0 ? e.Payload![i] as string : null;
    }

    /// <summary>First line only, and no longer than <see cref="ClipLength"/>: a backend's answer
    /// can be an arbitrary, unbounded body, and an endpoint is meant to be short already, but
    /// nothing here should be able to grow a log line without limit.</summary>
    private static string Clip(string text)
    {
        string line = text.Split('\n')[0].Trim();
        return line.Length <= ClipLength ? line : line[..ClipLength] + "...";
    }
}
