using System.Globalization;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Server;
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
    private const int Port = 29465;

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

    /// <summary>The seconds one mod has been credited so far, or zero before its first burst: a
    /// mod's series only appears once something has been measured for it.</summary>
    private static double Seconds(string exposition, string modid)
    {
        string series = $"pulse_mod_tick_seconds_total{{modid=\"{modid}\"}}";
        return exposition.Contains(series + " ", StringComparison.Ordinal) ? Scrape.Value(exposition, series) : 0;
    }

    /// <summary>The names every loaded entity's behaviors mark with, which is what the engine writes
    /// into the profiler tree: <c>done-behavior-</c> and the behavior's property name.</summary>
    private static IEnumerable<string> BehaviorNames(ICoreServerAPI api)
        => api.World.LoadedEntities.Values
            .SelectMany(entity => entity.Properties.Server.Behaviors)
            .Select(behavior => behavior.ProfilerName["done-behavior-".Length..])
            .Distinct();

    /// <summary>What attribution serves the moment it is armed, before any burst has run. Now
    /// order-independent within the class: engine and unattributed are always both reported while
    /// attribution runs (see AttributionMetrics.ShareMeasurements), zero when a burst has not
    /// produced them yet, so this holds whichever of the scenarios below happens to run first.</summary>
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

    /// <summary>A behavior is marked with its property name, and for a good third of the game's own
    /// classes that is not the code the class is registered under (the despawn behavior marks as
    /// <c>timeddespawn</c>, the player's name tag as <c>displayname</c>), so the class registry
    /// cannot turn the mark back into a type. Those marks used to land in <c>unattributed</c> even
    /// though the game's own mods own them. Pulse reads the behaviors of the loaded entities, which
    /// know both ends, before a burst folds anything, so even the first burst credits them.</summary>
    [AtlasScenario(FreshWorld = true)]
    public async Task Attribution_Credits_AGameBehaviorWithADifferentName_ToTheModThatShipsIt()
    {
        ITestPlayer player = await World.JoinPlayer("renamed-names");
        for (int i = 0; i < 300; i++)
        {
            World.SpawnEntity("game:chicken-hen", player.Position.Offset(2 + (i % 20), 1, 2 + (i / 20)));
        }

        await World.Ticks(10);

        // The premise, read off the live entities: behaviors marking with a name the class registry
        // has no class for. Without one in this world the checks below would prove nothing.
        string[] renamed =
        [
            .. BehaviorNames(World.Api).Where(name => World.Api.ClassRegistry.GetEntityBehaviorClass(name) == null),
        ];
        Assert.NotEmpty(renamed);

        // What the bursts that run from here on credit, not the whole run: a burst that completed
        // while the player was still joining measured a world with nothing in it.
        string before = await Scrape.Metrics(Port);
        double ticksBefore = Scrape.Value(before, "pulse_attribution_ticks_total");
        string after = before;
        for (int attempt = 0; attempt < 40 && Scrape.Value(after, "pulse_attribution_ticks_total") < ticksBefore + 10; attempt++)
        {
            await World.Ticks(30);
            after = await Scrape.Metrics(Port);
        }

        Assert.True(
            Scrape.Value(after, "pulse_attribution_ticks_total") >= ticksBefore + 10,
            "no two bursts completed:\n" + after);

        double Credited(string modid) => Seconds(after, modid) - Seconds(before, modid);

        // Compared with the time the game's own mods were credited rather than with the whole tick,
        // so a slow host, which inflates the engine's unmarked remainder, cannot hide the bug. Before
        // the fix the renamed behaviors alone were about a tenth of what the mods were credited, and
        // what is left once they are credited is a few late-registered listeners at most.
        double mods = Credited("game") + Credited("survival");
        Assert.True(mods > 0, "the game's own mods were credited nothing:\n" + after);
        Assert.True(
            Credited("unattributed") < 0.03 * mods,
            $"behaviors with a different name are still unattributed ({Credited("unattributed")} s against {mods} s credited to the game's mods):\n" + after);
    }
}
