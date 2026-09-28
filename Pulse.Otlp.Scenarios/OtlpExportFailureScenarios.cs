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

    private readonly FakeCollector collector;

    public OtlpExportFailureScenarios()
    {
        // xUnit builds the test class before Atlas boots the host, so the collector is already
        // listening by the time the exporter's first export goes out.
        collector = new FakeCollector(CollectorPort, HttpStatusCode.Unauthorized);
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

        Assert.Contains(FailureMarker, log);
        Assert.Contains("failed:", log);

        // The whole reason this reads the SDK's own EventSource instead of the request: the
        // configured header never reaches the log, however the backend answers.
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
