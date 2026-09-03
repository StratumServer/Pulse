using System.Globalization;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>Per-mod attribution against a real engine, which is the only place it can be proven.
/// Everything it reads is an engine internal with no compatibility promise: the profiler flag, the
/// mark tree, the prefixes the engine writes into mark keys, and the run phase that primes the
/// profiler before the tick loop exists. A unit test can only check the arithmetic. This checks
/// that the engine still produces what the arithmetic is for.
/// <para>The fixture runs a burst of five ticks a second apart, so a burst lands inside a
/// scenario rather than half a minute later.</para></summary>
[AtlasDataFiles("data/attribution/pulse.json", TargetPath = "ModConfig")]
public class AttributionScenarios : AtlasScenarioBase
{
    private const int Port = 39465;

    private static readonly string[] Families =
    [
        "pulse_mod_tick_share",
        "pulse_mod_tick_seconds_total",
        "pulse_attribution_ticks_total",
        "pulse_attribution_dropped_samples_total",
    ];

    /// <summary>Ticks until a burst has completed, or gives up and fails with the body it last
    /// saw. A burst needs its interval, then a discarded sample, then five profiled ticks.</summary>
    private static async Task<string> Burst(IWorldSession world)
    {
        string body = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await world.Ticks(30);
            body = await Scrape.Metrics(Port);
            if (Scrape.Value(body, "pulse_attribution_ticks_total") > 0)
            {
                return body;
            }
        }

        Assert.Fail("no burst ever completed:\n" + body);
        return body;
    }

    /// <summary>One mod's share line, of which there is exactly one per mod.</summary>
    private static double Share(string exposition, string modid)
        => Scrape.Value(exposition, $"pulse_mod_tick_share{{modid=\"{modid}\"}}");

    [AtlasScenario]
    public async Task Attribution_Serves_ItsFamilies_FromBoot()
    {
        await World.Ticks(5);

        string body = await Scrape.Metrics(Port);

        // Seeded at zero, so the families are on the wire before the first burst rather than
        // appearing minutes into a dashboard's life.
        foreach (string family in Families)
        {
            Assert.Contains("# TYPE " + family + " ", body);
        }

        Assert.Contains("pulse_mod_tick_share{modid=\"engine\"} ", body);
        Assert.Contains("pulse_mod_tick_share{modid=\"unattributed\"} ", body);
    }

    /// <summary>The whole feature end to end: the profiler was primed without killing the server,
    /// a burst ran, the marks parsed, and Pulse found itself in its own numbers. Pulse registers
    /// three game tick listeners off one ModSystem, so the engine marks them all with the type name
    /// this mod's assembly declares, and the mod loader maps that name back to modid "pulse".</summary>
    [AtlasScenario]
    public async Task Attribution_Attributes_TickTime_ToPulseItself()
    {
        string body = await Burst(World);

        double share = Share(body, "pulse");

        // A share, not a duration: whatever the host machine is doing, Pulse's listeners are some
        // fraction of a tick and never the whole of one.
        Assert.InRange(share, double.Epsilon, 1.0);
    }

    [AtlasScenario]
    public async Task Attribution_Splits_TheWholeBusyTick_BetweenItsBuckets()
    {
        string body = await Burst(World);

        double total = body.Split('\n')
            .Where(line => line.StartsWith("pulse_mod_tick_share{", StringComparison.Ordinal))
            .Sum(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture));

        // The engine's own time, the mods' and the remainder nobody marked add up to the tick, so
        // a share can be read straight off a dashboard as a proportion of the whole.
        Assert.Equal(1.0, total, 6);
    }

    [AtlasScenario]
    public async Task Attribution_Counts_TheSecondsItSampled()
    {
        string body = await Burst(World);

        double ticks = Scrape.Value(body, "pulse_attribution_ticks_total");
        double seconds = body.Split('\n')
            .Where(line => line.StartsWith("pulse_mod_tick_seconds_total{", StringComparison.Ordinal))
            .Sum(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture));

        // Sampled seconds, and the tick count is what makes them mean anything: five profiled
        // ticks cannot add up to more busy time than five ticks of the budget.
        Assert.True(ticks >= 5, $"the burst profiled {ticks} ticks");
        Assert.InRange(seconds, double.Epsilon, ticks);
    }

    /// <summary>The duty cycle is the reason any of this is affordable, so it has to actually
    /// idle between bursts rather than leave the profiler running.</summary>
    [AtlasScenario]
    public async Task Attribution_Profiles_OnlyASliceOfTheTicks()
    {
        string before = await Burst(World);
        await World.Ticks(300);
        string after = await Scrape.Metrics(Port);

        double profiled = Scrape.Value(after, "pulse_attribution_ticks_total")
            - Scrape.Value(before, "pulse_attribution_ticks_total");
        double ticked = Scrape.Value(after, "pulse_server_ticks_total")
            - Scrape.Value(before, "pulse_server_ticks_total");

        // Five profiled ticks per second-long interval is about one tick in seven at the default
        // tick rate. Asserted loosely, because the ratio moves with how fast the host ticks.
        Assert.True(ticked > 0, "the server did not tick");
        Assert.InRange(profiled / ticked, 0, 0.5);
    }
}
