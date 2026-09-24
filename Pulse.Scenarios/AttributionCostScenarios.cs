using System.Text.Json;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>Measures attribution's real tick cost against a live engine, replacing the mark-count
/// estimate the README and CHANGELOG used to carry. Three scenarios:
/// <list type="bullet">
/// <item><see cref="Attribution_Cost_Interleaved_DenseCluster"/> and
/// <see cref="Attribution_Cost_Interleaved_SpreadAcrossTheLoadedArea"/> each spawn 4000 chickens
/// (dense versus spread across the loaded area, same count, to tell density from entity count
/// apart), then read tick busy time with <c>World.MeasureTicks</c> across three interleaved
/// off/on pairs. Interleaving, rather than one off window followed by one on window, is what
/// makes the result trustworthy on a load that keeps settling: a straight before/after split
/// cannot tell attribution's own cost from the world drifting heavier while it runs, but a paired
/// delta (the same pair's on minus its own off, right next to it in time) mostly cancels drift
/// that moves smoothly across the run.</item>
/// <item><see cref="Attribution_Cost_Splits_EngineMarking_FromPulseFold"/> separates the engine's
/// own cost of writing profiler marks from Pulse's own cost of folding them into a per-mod
/// breakdown once a tick. Pulse's tick listener sets the engine's profiler flag to
/// <c>attribution.Profiling</c> every tick regardless of the duty cycle's own state, which means
/// simply flipping the flag from here would be undone within the same tick. Registering a second,
/// plain tick listener from this scenario forces it back on immediately afterward: Atlas boots
/// the host and stages the mod before any scenario runs, so a listener this scenario registers
/// lands after Pulse's own in the engine's list and its write is the one still standing when the
/// next tick's entity simulation reads the flag. Pulse's own duty cycle stays off throughout
/// (<c>Attribution.Enabled: false</c> in the fixture), so nothing here ever calls Pulse's fold.
/// The scenario checks the resulting profiler tree for real per-entity-behaviour marks rather
/// than assuming the ordering held.</item>
/// </list>
/// <para>Spawning 4000 entities and holding a burst open across several measurement windows is
/// slow, and the numbers are meant to be read by hand and copied into the README and CHANGELOG,
/// not gated on every push, so all three stay a no-op unless
/// <c>PULSE_MEASURE_ATTRIBUTION_COST=1</c> is set: the default CI run (and a plain local
/// <c>dotnet test</c>) still boots each class, at the same cost as any other scenario class, but
/// returns immediately. Run them for real, with VINTAGE_STORY set:
/// <c>PULSE_MEASURE_ATTRIBUTION_COST=1 dotnet test Pulse.Scenarios --filter "Category=Cost"</c>.
/// Each scenario writes its own numbers to a JSON file next to the test binaries and prints them
/// to the test's own output.</para></summary>
[Trait("Category", "Cost")]
[AtlasDataFiles("data/attributioncost/pulse.json", TargetPath = "ModConfig")]
public class AttributionCostScenarios : AtlasScenarioBase
{
    private const string OptInVariable = "PULSE_MEASURE_ATTRIBUTION_COST";
    private const int TargetEntities = 4000;
    private const int MeasuredTicks = 100;
    private const double TickBudgetMs = 33.333;
    private const int Pairs = 3;

    // A dense cluster packs one chicken per block; the spread shape uses the same entity count
    // over four times the area (one per 2x2 blocks), the widest spacing that still keeps the
    // whole cluster inside a 128-block view distance around the anchor player.
    private const int DenseSpacingBlocks = 1;
    private const int SpreadSpacingBlocks = 2;

    // Settle after an "off" transition is short: TickAttribution.Apply resets Profiling to false
    // synchronously, so the very next tick is already clean. Settle after an "on" transition has
    // to clear the fixture's one-second IntervalSeconds (about 30 ticks at default pacing, fewer
    // under load, since a slower tick banks wall-clock time faster) plus the one discarded
    // warm-up sample, with margin.
    private const int SettleOffTicks = 20;
    private const int SettleOnTicks = 60;
    private const int InitialSettleTicks = 60;

    // The shipped default (PulseConfig.AttributionConfig.BurstTicks/IntervalSeconds), what the
    // README and CHANGELOG document; keep this pair in sync with that class, since the two
    // cannot share the literal across the assembly boundary (Pulse.Scenarios deliberately does
    // not reference Pulse's types, see this project's csproj). Not changed here: the fixture's
    // own burst is much wider so a clean "on" reading fits inside one continuous profiled
    // window, and the amortised figure is computed at these defaults from that reading, same as
    // the alternative duty cycles in the PR report.
    private const int DefaultBurstTicks = 10;
    private const int DefaultIntervalSeconds = 10;

    private static bool OptedIn => Environment.GetEnvironmentVariable(OptInVariable) == "1";

    [AtlasScenario(FreshWorld = true, TimeoutMs = 360_000)]
    public async Task Attribution_Cost_Interleaved_DenseCluster()
    {
        if (!OptedIn)
        {
            Skip();
            return;
        }

        await MeasureLoadShape("dense", DenseSpacingBlocks);
    }

    [AtlasScenario(FreshWorld = true, TimeoutMs = 360_000)]
    public async Task Attribution_Cost_Interleaved_SpreadAcrossTheLoadedArea()
    {
        if (!OptedIn)
        {
            Skip();
            return;
        }

        await MeasureLoadShape("spread", SpreadSpacingBlocks);
    }

    [AtlasScenario(FreshWorld = true, TimeoutMs = 360_000)]
    public async Task Attribution_Cost_Splits_EngineMarking_FromPulseFold()
    {
        if (!OptedIn)
        {
            Skip();
            return;
        }

        ITestPlayer anchor = await World.JoinPlayer("load-anchor");
        int spawned = SpawnCluster(anchor.Position, TargetEntities, DenseSpacingBlocks);
        await World.Ticks(InitialSettleTicks);

        TickMeasurement off = await World.MeasureTicks(MeasuredTicks);

        long listenerId = World.Api.Event.RegisterGameTickListener(
            ForceProfilerOn, e => World.Api.Logger.Error(e), 0);
        bool marksSeen;
        TickMeasurement engineOnly;
        try
        {
            await World.Ticks(SettleOffTicks);
            engineOnly = await World.MeasureTicks(MeasuredTicks);
            marksSeen = EngineRecordedBehaviourMarks(World.Api.World.FrameProfiler.PrevRootEntry);
        }
        finally
        {
            World.Api.Event.UnregisterGameTickListener(listenerId);
        }

        // Give Pulse's own tick listener a few ticks to reassert control before the next phase;
        // it always runs, it was just outvoted while the extra listener above was registered.
        await World.Ticks(SettleOffTicks);

        await World.ExecuteCommand("/pulse attribution on");
        await World.Ticks(SettleOnTicks);
        TickMeasurement onFull = await World.MeasureTicks(MeasuredTicks);
        await World.ExecuteCommand("/pulse attribution off");

        double engineMs = engineOnly.BusyTime.MedianMs - off.BusyTime.MedianMs;
        double pulseFoldMs = onFull.BusyTime.MedianMs - engineOnly.BusyTime.MedianMs;

        var result = new
        {
            entitiesSpawned = spawned,
            loadShape = "dense",
            marksSeenOnEngineOnlyWindow = marksSeen,
            off = Summarise(off),
            engineOnly = Summarise(engineOnly),
            onFull = Summarise(onFull),
            engineMarkingMs = engineMs,
            engineMarkingPctOfBudget = engineMs / TickBudgetMs * 100,
            pulseFoldMs,
            pulseFoldPctOfBudget = pulseFoldMs / TickBudgetMs * 100,
            note = marksSeen
                ? "the engine-only window recorded real per-entity-behaviour marks; the split above is meaningful"
                : "no per-entity-behaviour marks were seen on the engine-only window; the listener-ordering trick "
                    + "did not win the race this run, and engineMarkingMs/pulseFoldMs should not be trusted",
        };

        await WriteReport("attribution-cost-engine-split.json", result);

        Assert.True(spawned > 0, "no load was spawned to measure against");
    }

    /// <summary>Runs the three-pair interleaved off/on measurement for one entity arrangement and
    /// writes its own report.</summary>
    private async Task MeasureLoadShape(string shapeLabel, int spacingBlocks)
    {
        ITestPlayer anchor = await World.JoinPlayer("load-anchor");
        int spawned = SpawnCluster(anchor.Position, TargetEntities, spacingBlocks);
        await World.Ticks(InitialSettleTicks);

        List<TickMeasurement> offs = [];
        List<TickMeasurement> ons = [];
        for (int pair = 0; pair < Pairs; pair++)
        {
            if (pair > 0)
            {
                await World.ExecuteCommand("/pulse attribution off");
            }

            await World.Ticks(SettleOffTicks);
            offs.Add(await World.MeasureTicks(MeasuredTicks));

            await World.ExecuteCommand("/pulse attribution on");
            await World.Ticks(SettleOnTicks);
            ons.Add(await World.MeasureTicks(MeasuredTicks));
        }

        await World.ExecuteCommand("/pulse attribution off");

        double[] deltasMs = [.. offs.Zip(ons, (off, on) => on.BusyTime.MedianMs - off.BusyTime.MedianMs)];
        double medianDeltaMs = Median(deltasMs);
        double ticksPerSecond = offs[0].Passes / offs[0].WallTime.TotalSeconds;
        double dutyFraction = DefaultBurstTicks / (DefaultBurstTicks + (DefaultIntervalSeconds * ticksPerSecond));
        double offMedianMs = Median([.. offs.Select(o => o.BusyTime.MedianMs)]);
        double amortisedMs = offMedianMs + (dutyFraction * medianDeltaMs);

        var result = new
        {
            entitiesSpawned = spawned,
            loadShape = shapeLabel,
            spacingBlocks,
            tickBudgetMs = TickBudgetMs,
            offWindows = offs.Select(Summarise).ToArray(),
            onWindows = ons.Select(Summarise).ToArray(),
            offBaselineTrendMs = offs.Select(o => o.BusyTime.MedianMs).ToArray(),
            pairedDeltasMs = deltasMs,
            medianPairedDeltaMs = medianDeltaMs,
            medianPairedDeltaPctOfBudget = medianDeltaMs / TickBudgetMs * 100,
            ticksPerSecond,
            defaultBurstTicks = DefaultBurstTicks,
            defaultIntervalSeconds = DefaultIntervalSeconds,
            dutyFractionAtDefault = dutyFraction,
            amortisedMsAtDefault = amortisedMs,
            amortisedPctOfBudgetAtDefault = (amortisedMs - offMedianMs) / TickBudgetMs * 100,
        };

        await WriteReport($"attribution-cost-{shapeLabel}.json", result);

        Assert.True(spawned > 0, "no load was spawned to measure against");
        Assert.True(
            medianDeltaMs >= 0,
            $"the median paired delta ({medianDeltaMs}ms) reads negative; attribution should never make ticks cheaper");
    }

    private void ForceProfilerOn(float _)
    {
        FrameProfilerUtil profiler = World.Api.World.FrameProfiler;
        profiler.Enabled = true;
    }

    /// <summary>Checks the profiler tree for the same per-entity-behaviour marks
    /// <c>TickAttribution.Walk</c> reads, so the engine-only split is reported only when the
    /// listener-ordering trick actually worked rather than assumed.</summary>
    private static bool EngineRecordedBehaviourMarks(ProfileEntryRange? root)
        => root?.ChildRanges != null
            && root.ChildRanges.TryGetValue("tickentities", out ProfileEntryRange? tickEntities)
            && tickEntities.ChildRanges != null
            && tickEntities.ChildRanges.TryGetValue("behaviors", out ProfileEntryRange? behaviours)
            && behaviours.Marks is { Count: > 0 };

    private static object Summarise(TickMeasurement measurement) => new
    {
        measurement.Passes,
        measurement.BusyTime.MedianMs,
        measurement.BusyTime.P95Ms,
    };

    private static double Median(double[] values)
    {
        double[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private async Task WriteReport(string fileName, object result)
    {
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(fileName, json);
        Console.WriteLine(json);
    }

    private void Skip() => Console.WriteLine($"Skipped: set {OptInVariable}=1 to run the real measurement (see class remarks).");

    private int SpawnCluster(BlockPos origin, int target, int spacingBlocks)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(target));
        int half = side * spacingBlocks / 2;
        int spawned = 0;
        for (int x = 0; x < side && spawned < target; x++)
        {
            for (int z = 0; z < side && spawned < target; z++)
            {
                World.SpawnEntity(
                    "game:chicken-hen", origin.Offset((x * spacingBlocks) - half, 1, (z * spacingBlocks) - half));
                spawned++;
            }
        }

        return spawned;
    }
}
