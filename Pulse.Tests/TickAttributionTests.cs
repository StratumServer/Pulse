using Vintagestory.API.Common;
using Xunit;

// Same collision as in the class under test: the game's API declares its own Func delegate.
using OwnerLookup = System.Func<string, string?>;

namespace Pulse.Tests;

public class TickAttributionTests
{
    /// <summary>One profiled tick as the engine leaves it: a thousand ticks of wall time, four
    /// hundred of them asleep, and six hundred of work split between an engine system, a mod's tick
    /// listener, a listener nothing claims, an entity behavior in a nested range, and a hundred
    /// ticks nobody marked at all.</summary>
    private static ProfileEntryRange Tick() => new()
    {
        Code = "all",
        ElapsedTicks = 1000,
        Marks = new Dictionary<string, ProfileEntry>
        {
            ["sleep"] = new ProfileEntry(400, 1),
            ["ss-tick-Vintagestory.Server.ServerSystemEntitySimulation"] = new ProfileEntry(100, 1),
            ["gmleMy.Mod.Thing"] = new ProfileEntry(200, 1),
            ["gmleSomebody.Elses.Thing"] = new ProfileEntry(50, 1),
            ["end"] = new ProfileEntry(0, 1),
        },
        ChildRanges = new Dictionary<string, ProfileEntryRange>
        {
            ["tickentities"] = new ProfileEntryRange
            {
                Code = "tickentities",
                ElapsedTicks = 150,
                Marks = new Dictionary<string, ProfileEntry> { ["done-behavior-health"] = new ProfileEntry(150, 40) },
            },
        },
    };

    private static readonly OwnerLookup Owners = name => name switch
    {
        "My.Mod.Thing" => "mymod",
        "health" => "survival",
        _ => null,
    };

    /// <summary>Runs the duty cycle from idle to the end of one burst, feeding every profiled tick
    /// the same tree.</summary>
    private static AttributionBurst Cycle(TickAttribution attribution, ProfileEntryRange tick, OwnerLookup owners)
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            if (attribution.OnTick(1.0, tick, owners) is { } burst)
            {
                return burst;
            }
        }

        throw new InvalidOperationException("the burst never completed");
    }

    private static double Share(AttributionBurst burst, string modid)
        => burst.Seconds.Single(entry => entry.Key == modid).Value / burst.BusySeconds;

    [Fact]
    public void Constructor_Floors_TheIntervalAndTheBurstLength()
    {
        TickAttribution attribution = new(0, 0);

        Assert.Equal(1, attribution.BurstTicks);
        Assert.Equal(TickAttribution.MinimumIntervalSeconds, attribution.IntervalSeconds);
    }

    [Fact]
    public void Constructor_Caps_TheBurstLength()
        => Assert.Equal(TickAttribution.MaximumBurstTicks, new TickAttribution(100000, 10).BurstTicks);

    [Fact]
    public void Constructor_Keeps_AConfiguredDutyCycle()
    {
        TickAttribution attribution = new(30, 10);

        Assert.Equal(30, attribution.BurstTicks);
        Assert.Equal(10, attribution.IntervalSeconds);
    }

    [Fact]
    public void OnTick_LeavesTheProfilerOff_UntilTheIntervalHasPassed()
    {
        TickAttribution attribution = new(5, 10);

        for (int tick = 0; tick < 9; tick++)
        {
            Assert.Null(attribution.OnTick(1.0, Tick(), Owners));
            Assert.False(attribution.Profiling);
        }

        Assert.Null(attribution.OnTick(1.0, Tick(), Owners));
        Assert.True(attribution.Profiling);
    }

    /// <summary>The tick that turns the profiler on never got its Begin(), so the tree it ends with
    /// is whatever the last burst left behind. Folding it would count that stale tick again.</summary>
    [Fact]
    public void OnTick_Discards_TheFirstSampleAfterTheProfilerComesOn()
    {
        TickAttribution attribution = new(1, 1);

        Assert.Null(attribution.OnTick(1.0, Tick(), Owners));   // the profiler comes on
        Assert.Null(attribution.OnTick(1.0, Tick(), Owners));   // stale sample, discarded
        AttributionBurst burst = attribution.OnTick(1.0, Tick(), Owners)!;

        Assert.Equal(1, burst.Ticks);
    }

    [Fact]
    public void OnTick_TurnsTheProfilerBackOff_WhenTheBurstIsDone()
    {
        TickAttribution attribution = new(3, 1);

        AttributionBurst burst = Cycle(attribution, Tick(), Owners);

        Assert.False(attribution.Profiling);
        Assert.Equal(3, burst.Ticks);
    }

    [Fact]
    public void OnTick_Runs_ASecondBurstAfterTheNextInterval()
    {
        TickAttribution attribution = new(2, 1);

        Cycle(attribution, Tick(), Owners);
        AttributionBurst second = Cycle(attribution, Tick(), Owners);

        Assert.Equal(2, second.Ticks);
        Assert.Equal(150.0 / 600.0, Share(second, "survival"), 6);
    }

    [Fact]
    public void OnTick_Ends_ABurstEvenWhenTheProfilerLeftNoTree()
    {
        TickAttribution attribution = new(2, 1);

        AttributionBurst burst = Cycle(attribution, null!, Owners);

        Assert.False(attribution.Profiling);
        Assert.Equal(0, burst.BusySeconds);
        Assert.Empty(burst.Seconds);
    }

    [Fact]
    public void Fold_Attributes_AListenerMarkToTheModThatOwnsIt()
        => Assert.Equal(200.0 / 600.0, Share(Cycle(new TickAttribution(1, 1), Tick(), Owners), "mymod"), 6);

    /// <summary>Entity behaviors are marked in a nested range and keyed by behavior code rather than
    /// by type name, so a fold that only read the root would report this mod's cost as the
    /// engine's.</summary>
    [Fact]
    public void Fold_Attributes_ABehaviorMarkFromANestedRange()
        => Assert.Equal(150.0 / 600.0, Share(Cycle(new TickAttribution(1, 1), Tick(), Owners), "survival"), 6);

    [Fact]
    public void Fold_Reports_AMarkNoModClaims_AsUnattributed()
        => Assert.Equal(50.0 / 600.0, Share(Cycle(new TickAttribution(1, 1), Tick(), Owners), TickAttribution.Unattributed), 6);

    /// <summary>The engine's own systems, plus everything the marks did not name: the gap before the
    /// first mark and every range entered without a mark inside it.</summary>
    [Fact]
    public void Fold_Charges_TheEnginesOwnMarksAndTheUnmarkedRemainder_ToTheEngine()
        => Assert.Equal(200.0 / 600.0, Share(Cycle(new TickAttribution(1, 1), Tick(), Owners), TickAttribution.Engine), 6);

    [Fact]
    public void Fold_Excludes_TheThrottleSleep_FromBusyTime()
    {
        AttributionBurst burst = Cycle(new TickAttribution(1, 1), Tick(), Owners);

        Assert.DoesNotContain(burst.Seconds, entry => entry.Key == "sleep");
        Assert.Equal(1.0, burst.Seconds.Sum(entry => entry.Value) / burst.BusySeconds, 6);
    }

    /// <summary>A mark's elapsed time accumulates into an int, so past about two seconds inside one
    /// tick it wraps negative. That reading is garbage rather than a large number.</summary>
    [Fact]
    public void Fold_Drops_AMarkWhoseElapsedTimeHasWrappedNegative()
    {
        ProfileEntryRange tick = Tick();
        tick.Marks!["gmleMy.Mod.Thing"] = new ProfileEntry(-1234, 1);

        AttributionBurst burst = Cycle(new TickAttribution(1, 1), tick, Owners);

        Assert.Equal(1, burst.Dropped);
        Assert.Equal(0, Share(burst, "mymod"));

        // The wrapped time is not silently handed to somebody else either: it lands in the
        // remainder, which is the engine's bucket, and the shares still add to one.
        Assert.Equal(1.0, burst.Seconds.Sum(entry => entry.Value) / burst.BusySeconds, 6);
    }

    [Fact]
    public void Fold_Counts_EveryWrappedMark_AndResetsTheCountEachBurst()
    {
        ProfileEntryRange tick = Tick();
        tick.Marks!["gmleMy.Mod.Thing"] = new ProfileEntry(-1, 1);
        tick.ChildRanges!["tickentities"].Marks!["done-behavior-health"] = new ProfileEntry(-1, 1);
        TickAttribution attribution = new(2, 1);

        Assert.Equal(4, Cycle(attribution, tick, Owners).Dropped);
        Assert.Equal(0, Cycle(attribution, Tick(), Owners).Dropped);
    }

    /// <summary>A gauge keeps whatever it was last given, so a mod that stops ticking would sit at
    /// the share it had when it stopped until the server restarted.</summary>
    [Fact]
    public void Take_Keeps_ReportingAModThatWentQuiet()
    {
        TickAttribution attribution = new(1, 1);
        ProfileEntryRange quiet = Tick();
        quiet.Marks!.Remove("gmleMy.Mod.Thing");

        Cycle(attribution, Tick(), Owners);
        AttributionBurst second = Cycle(attribution, quiet, Owners);

        Assert.Equal(0, Share(second, "mymod"));
    }

    [Fact]
    public void Take_Orders_TheBucketsStably()
    {
        AttributionBurst burst = Cycle(new TickAttribution(1, 1), Tick(), Owners);

        Assert.Equal(
            ["engine", "mymod", "survival", "unattributed"],
            burst.Seconds.Select(entry => entry.Key));
    }

    [Fact]
    public void Take_Accumulates_AcrossTheTicksOfOneBurst()
    {
        AttributionBurst one = Cycle(new TickAttribution(1, 1), Tick(), Owners);
        AttributionBurst four = Cycle(new TickAttribution(4, 1), Tick(), Owners);

        Assert.Equal(4 * one.BusySeconds, four.BusySeconds, 12);
        Assert.Equal(
            4 * one.Seconds.Single(entry => entry.Key == "mymod").Value,
            four.Seconds.Single(entry => entry.Key == "mymod").Value,
            12);
    }
}
