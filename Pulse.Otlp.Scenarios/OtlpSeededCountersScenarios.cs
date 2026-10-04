using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>Regression coverage for mod start order: on an idle, already generated world (nobody
/// online, nothing left to explore, no autosave, no engine warning), several of the base mod's
/// counters only ever receive one measurement in their whole life, the zero <c>SeedCounters</c>
/// records at startup. That call happens once, synchronously, inside
/// <c>PulseModSystem.StartServerSide</c>. A measurement only reaches the listeners attached at the
/// moment it is recorded: <c>MeterListener.Start()</c> does see an instrument that was already
/// published (it walks every published instrument when it starts), so an OTLP provider built after
/// the base mod still picks up the "Pulse.Server" meter and everything recorded on it from then on.
/// What it never sees is whatever was already recorded before that start, the seeds included. A
/// restarted boot is what makes "idle" concrete here: the world's spawn chunks already exist on
/// disk from the boot that created them, so nothing calls OnMapChunkGenerated a second time (or, on
/// a slower runner, calls it only a little), and pulse_worldgen_columns_generated_total has close to
/// no source of truth beyond its own seed, the same as it would on a veteran dedicated server that
/// has not generated new terrain in months.</summary>
/// <remarks>Assertions check presence per seeded series, not value, except for the three families an
/// idle restarted server actually guarantees at zero (deaths, suspends, suspend seconds). A slow
/// first tick under load can log a real "Over 400ms tick" warning (counted under
/// pulse_log_entries_total's "warning" level; see PulseModSystem's PrimeFrameProfiler) or, rarely, a
/// real overload (pulse_engine_warnings_total's "overload" kind), and a restart whose previous boot
/// had not quite finished generating the spawn area can still generate a few more columns. None of
/// that is what this fix is about: the regression is a series being entirely absent, and presence is
/// what proves it is not.</remarks>
[AtlasDataFiles("data/otlpseed", TargetPath = "ModConfig")]
public class OtlpSeededCountersScenarios : AtlasScenarioBase
{
    private const int CollectorPort = 29481;

    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(2);

    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(OtlpSeededCountersScenarios).Assembly.Location)!;

    /// <summary>Unlabelled families a genuinely idle restart guarantees at exactly 0: nobody died,
    /// nothing suspended, so nothing on this restart can move them off their seed.</summary>
    private static readonly string[] ZeroAtIdle =
    [
        "pulse_player_deaths_total",
        "pulse_server_suspends_total",
        "pulse_server_suspend_seconds_total",
    ];

    /// <summary>Left at presence only, unlike <see cref="ZeroAtIdle"/>: a restart whose previous
    /// boot had not quite finished generating the spawn area keeps generating on this one, which
    /// would move its value without saying anything about this fix.</summary>
    private const string WorldgenColumnsFamily = "pulse_worldgen_columns_generated_total";

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
            () => collector.First, () => collector.Count,
            () => World.Ticks(10), ExportInterval * 15, CollectorPort);

        Assert.Equal("application/x-protobuf", export.ContentType);

        foreach (string kind in EngineWarningKinds)
        {
            AssertSeriesPresent(export.Body, "pulse_engine_warnings_total", "kind", kind);
        }

        foreach (string level in LogLevels)
        {
            AssertSeriesPresent(export.Body, "pulse_log_entries_total", "level", level);
        }

        AssertSeriesPresent(export.Body, WorldgenColumnsFamily);

        foreach (string family in ZeroAtIdle)
        {
            List<OtlpMetricsPayload.Point> points = OtlpMetricsPayload.PointsFor(export.Body, family);
            Assert.True(points.Count > 0, $"{family} never reached the collector");
            foreach (OtlpMetricsPayload.Point point in points)
            {
                Assert.Equal(0, point.Value);
            }
        }
    }

    private static void AssertSeriesPresent(byte[] body, string metricName)
    {
        List<OtlpMetricsPayload.Point> points = OtlpMetricsPayload.PointsFor(body, metricName);
        Assert.True(points.Count > 0, $"{metricName} never reached the collector");
    }

    private static void AssertSeriesPresent(byte[] body, string metricName, string labelKey, string labelValue)
    {
        List<OtlpMetricsPayload.Point> points = OtlpMetricsPayload.PointsFor(body, metricName);
        bool present = points.Any(point =>
            point.Attributes.TryGetValue(labelKey, out string? value) && value == labelValue);
        Assert.True(present, $"{metricName}{{{labelKey}=\"{labelValue}\"}} never reached the collector");
    }

    // Reflection against the staged mod dll, not a copy of the two lists typed out again here: this
    // project deliberately carries no compile-time reference to Pulse.dll (see its csproj), so this
    // is the one place that reaches across anyway, at arm's length, to keep this scenario from
    // silently drifting the day someone adds or renames a kind or a level in LogClassifier and
    // forgets this file exists.
    private static readonly IReadOnlyList<string> EngineWarningKinds = ReadClassifierList("Kinds");
    private static readonly IReadOnlyList<string> LogLevels = ReadClassifierList("Levels");

    private static IReadOnlyList<string> ReadClassifierList(string fieldName)
    {
        string modDll = Path.Combine(TestAssemblyDirectory, "mod", "Pulse.dll");
        Assembly assembly = Assembly.LoadFrom(modDll);
        Type classifier = assembly.GetType("Pulse.LogClassifier")
            ?? throw new InvalidOperationException("Pulse.LogClassifier not found in " + modDll);
        object value = classifier.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Pulse.LogClassifier." + fieldName + " not found");
        return (IReadOnlyList<string>)value;
    }
}
