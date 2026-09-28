using System.Diagnostics;
using System.Net;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>A collector that rejects every export with 401: the failure an admin is most likely to
/// hit, a wrong or expired token, proven end to end from a real exporter thread through to the
/// server's own log rather than at the unit level.</summary>
/// <remarks>Its own fixture, its own port and its own seeded config, for the reason the other OTLP
/// export scenarios give: the config file is seeded from disk before the server boots and so
/// cannot carry a port chosen at runtime.</remarks>
[AtlasDataFiles("data/otlpfailure", TargetPath = "ModConfig")]
public class OtlpExportFailureScenarios : AtlasScenarioBase, IDisposable
{
    private const int CollectorPort = 39479;
    private const string FailureMarker = "Pulse OTLP export to";
    private const string ConfiguredSecret = "scenario-secret-token";

    // Braces in the backend's own body: the same shape that would throw if PulseOtlpModSystem
    // ever regressed to passing the queued line as the game logger's format string instead of as
    // an argument to a fixed "{0}" template.
    private const string RejectionBody = "{\"error\":\"invalid credentials\",\"detail\":\"token {expired}\"}";

    private readonly FakeCollector collector;

    public OtlpExportFailureScenarios()
    {
        // xUnit builds the test class before Atlas boots the host, so the collector is already
        // listening by the time the exporter's first export goes out.
        collector = new FakeCollector(CollectorPort, HttpStatusCode.Unauthorized, RejectionBody);
    }

    public void Dispose()
    {
        collector.Dispose();
        GC.SuppressFinalize(this);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Exporter_LogsTheRejectedPush_InTheServerLog()
    {
        string log = await WaitForFailureLine();

        // The failure line itself, not just the log as a whole: at Warning (never Error, so
        // DieAboveErrorCount cannot count it), naming the status and carrying the backend's body
        // intact, braces and all, which only holds if the line reached the logger as an argument
        // rather than as the format string.
        string line = Assert.Single(
            log.Split('\n'), l => l.Contains(FailureMarker, StringComparison.Ordinal));
        Assert.Contains("[Warning]", line);
        Assert.Contains("401", line);
        Assert.Contains(RejectionBody, line);

        // The configured header never reaches the log, however the backend answers: reading the
        // SDK's own EventSource instead of the request keeps it out of every payload to begin
        // with, and ExportFailureLog redacts the configured value out of the backend's own body
        // too, in case a collector ever echoes back what it was sent.
        Assert.DoesNotContain(ConfiguredSecret, log);
    }

    /// <summary>Pumps the world until the failure line appears or a minute of wall clock time
    /// passes: real time, not a tick count, since both the exporter's periodic timer and the mod's
    /// own drain listener run on real timers the world owes nothing to.</summary>
    private async Task<string> WaitForFailureLine()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        Stopwatch clock = Stopwatch.StartNew();
        string text = ReadShared(path);
        while (!text.Contains(FailureMarker, StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            await World.Ticks(10);
            text = ReadShared(path);
        }

        return text;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
