using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>Regression coverage for mod start order: on an idle, already generated world (nobody
/// online, nothing left to explore, no autosave, no engine warning), several of the base mod's
/// counters only ever receive one measurement in their whole life, the zero <c>SeedCounters</c>
/// records at startup. That call happens once, synchronously, inside
/// <c>PulseModSystem.StartServerSide</c>; if the OTLP exporter is not already listening for the
/// "Pulse.Server" meter when it runs, the measurement is gone for good, because
/// <c>System.Diagnostics.Metrics.Counter.Add</c> is a no-op when nothing is subscribed at the
/// moment it is called. A restarted boot is what makes "idle" concrete here: the world's spawn
/// chunks already exist on disk from the boot that created them, so nothing calls
/// OnMapChunkGenerated a second time, and pulse_worldgen_columns_generated_total has no source of
/// truth except its own seed, the same as it would on a veteran dedicated server that has not
/// generated new terrain in months.</summary>
[AtlasDataFiles("data/otlpseed", TargetPath = "ModConfig")]
public class OtlpSeededCountersScenarios : AtlasScenarioBase
{
    private const int CollectorPort = 39479;

    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(2);

    /// <summary>Counters an idle server only ever seeds: nobody died, nothing suspended, no engine
    /// warning fired, and the restarted boot generates no new terrain.</summary>
    private static readonly string[] SeededAtZero =
    [
        "pulse_engine_warnings_total",
        "pulse_player_deaths_total",
        "pulse_server_suspends_total",
        "pulse_server_suspend_seconds_total",
        "pulse_worldgen_columns_generated_total",
    ];

    [AtlasScenario(RestartWorld = true, TimeoutMs = 180_000)]
    public async Task SeededCounters_Reach_TheCollector_OnAnIdleRestartedServer()
    {
        // Constructed here, not in the constructor: RestartWorld's reboot runs before this method
        // body, and the boot it replaces shuts down gracefully, which force-flushes an export of
        // its own (see PulseOtlpModSystem.Dispose). Listening only from here means the export this
        // scenario inspects is the restarted, idle boot's first one, never a stray one left over
        // from the boot that generated the world in the first place.
        using FakeCollector collector = new(CollectorPort);

        FakeCollector.Export export = await Exports.WaitFor(
            () => collector.First, () => World.Ticks(10), ExportInterval * 15, CollectorPort);

        Assert.Equal("application/x-protobuf", export.ContentType);

        foreach (string family in SeededAtZero)
        {
            List<OtlpMetricsPayload.Point> points = OtlpMetricsPayload.PointsFor(export.Body, family);
            Assert.True(points.Count > 0, $"{family} never reached the collector");
            foreach (OtlpMetricsPayload.Point point in points)
            {
                Assert.Equal(0, point.Value);
            }
        }

        // Labelled per severity rather than a single series, seeded at zero the same way as the
        // families above; but a genuine "Over 400ms tick" warning is a real, environment-dependent
        // possibility on any boot (attribution arms the engine's frame profiler regardless of
        // whether attribution itself is switched on, see PulseModSystem's PrimeFrameProfiler, and a
        // slow first tick under load logs exactly that warning, counted here as level="warning").
        // That is a fact about tick timing, not about this fix, so only presence is asserted here:
        // the regression this scenario exists for is the family being entirely absent, which
        // reaching the collector at all already disproves.
        List<OtlpMetricsPayload.Point> logEntries = OtlpMetricsPayload.PointsFor(export.Body, "pulse_log_entries_total");
        Assert.True(logEntries.Count > 0, "pulse_log_entries_total never reached the collector");
    }
}
