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
/// apart), then read tick busy time with <c>World.MeasureTicks</c> across off, on, off, on, off,
/// on, off: four off windows bracketing three on windows rather than one off window per on. Each
/// on is compared against the mean of the two off windows next to it in time, not just the one
/// before it, so a baseline that drifts smoothly across the run (a settling load, or this shared
/// machine's own noise) is cancelled rather than folded one-sidedly into every delta.</item>
/// <item><see cref="Attribution_Cost_Splits_EngineMarking_FromPulseFold"/> separates the engine's
/// own cost of writing profiler marks from Pulse's own cost of reading them back. Pulse's tick
/// listener sets the engine's profiler flag to <c>attribution.Profiling</c> every tick regardless
/// of the duty cycle's own state, which means simply flipping the flag from here would be undone
/// within the same tick. Registering a second, plain tick listener from this scenario and setting
/// the flag back to true from it usually wins the last write for the tick, since Pulse's listener
/// was registered first and the engine's list is normally appended to in order, but
/// <c>AddGameTickListener</c> reuses the first empty slot a prior remove left behind, so that
/// ordering is never guaranteed. What actually makes this sound is the runtime check: the scenario
/// reads the resulting profiler tree back and asserts it holds real per-entity-behaviour marks,
/// and fails outright rather than reporting a split it cannot back up when it does not. Pulse's
/// own share of the same burst is read from its own attribution (<c>modid="pulse"</c>), at the
/// stopwatch resolution that is measured at, rather than from a millisecond-rounded
/// <c>MeasureTicks</c> difference too small for that resolution to see.</item>
/// </list>
/// <para>Spawning 4000 entities and holding a burst open across several measurement windows is
/// slow, and the numbers are meant to be read by hand and copied into the README and CHANGELOG,
/// not gated on every push, so all three stay a no-op unless
/// <c>PULSE_MEASURE_ATTRIBUTION_COST=1</c> is set: the default CI run (and a plain local
/// <c>dotnet test</c>) still boots each class, at the same cost as any other scenario class, but
/// returns immediately; the CI workflows additionally filter this trait out, so they never pay
/// even that. Run them for real, with VINTAGE_STORY set:
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
    private const int Port = 39475;

    // Matches data/attributioncost/pulse.json's Attribution.BurstTicks: wide open, so a clean
    // "on" MeasureTicks window fits inside one continuous profiled burst regardless of pacing.
    private const int FixtureBurstTicks = 300;

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
    // not reference Pulse's types, see this project's csproj).
    private const int DefaultBurstTicks = 10;
    private const int DefaultIntervalSeconds = 10;

    private static readonly JsonSerializerOptions ReportFormat = new() { WriteIndented = true };

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
        AssertLoadIsLive(spawned);

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

        Assert.True(
            marksSeen,
            "the engine-only window recorded no per-entity-behaviour marks; the listener-ordering "
                + "trick did not win the race this run, so the engine/Pulse split cannot be trusted "
                + "and this scenario refuses to report it rather than publish a guess");

        // Give Pulse's own tick listener a few ticks to reassert control before the next phase; it
        // always runs, it was just outvoted while the extra listener above was registered.
        await World.Ticks(SettleOffTicks);

        CommandResult on = await World.ExecuteCommand("/pulse attribution on");
        Assert.True(on.Ok, on.Message);
        await World.Ticks(SettleOnTicks);
        TickMeasurement onFull = await World.MeasureTicks(MeasuredTicks);

        // Pulse's own share of this same burst, read from its own attribution at the stopwatch
        // resolution that is measured at: the ms figures above are whole-millisecond-rounded and
        // cannot resolve Pulse's own tick listener (bookkeeping and the attribution fold together)
        // against a rounding step that size. Wait for the wide fixture burst to actually finish
        // (it is still running: SettleOnTicks + MeasuredTicks ticks into a FixtureBurstTicks + 1
        // tick burst) before scraping, so the published counters reflect this burst.
        await World.Ticks(FixtureBurstTicks + 1 - SettleOnTicks - MeasuredTicks + 40);
        string exposition = await Scrape.Metrics(Port);
        double pulseSeconds = Scrape.Value(exposition, "pulse_mod_tick_seconds_total{modid=\"pulse\"}");
        double profiledTicks = Scrape.Value(exposition, "pulse_attribution_ticks_total");
        double pulseOwnMsPerTick = profiledTicks > 0 ? pulseSeconds / profiledTicks * 1000 : 0;

        CommandResult offAgain = await World.ExecuteCommand("/pulse attribution off");
        Assert.True(offAgain.Ok, offAgain.Message);

        double engineMs = engineOnly.BusyTime.MedianMs - off.BusyTime.MedianMs;

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
            pulseOwnMsPerProfiledTick = pulseOwnMsPerTick,
            pulseOwnPctOfBudget = pulseOwnMsPerTick / TickBudgetMs * 100,
            pulseOwnNote = "Pulse's own tick listener share (bookkeeping and the attribution fold "
                + "together), from its own attribution at stopwatch resolution, not a difference of "
                + "whole-millisecond MeasureTicks readings",
        };

        await WriteReport("attribution-cost-engine-split.json", result);

        Assert.True(spawned > 0, "no load was spawned to measure against");
    }

    /// <summary>Runs the off/on/off/on/off/on/off interleaved measurement for one entity
    /// arrangement and writes its own report.</summary>
    private async Task MeasureLoadShape(string shapeLabel, int spacingBlocks)
    {
        ITestPlayer anchor = await World.JoinPlayer("load-anchor");
        int spawned = SpawnCluster(anchor.Position, TargetEntities, spacingBlocks);
        await World.Ticks(InitialSettleTicks);
        AssertLoadIsLive(spawned);

        List<TickMeasurement> offs = [];
        List<TickMeasurement> ons = [];
        for (int pair = 0; pair < Pairs; pair++)
        {
            if (pair > 0)
            {
                CommandResult offCmd = await World.ExecuteCommand("/pulse attribution off");
                Assert.True(offCmd.Ok, offCmd.Message);
            }

            await World.Ticks(SettleOffTicks);
            offs.Add(await World.MeasureTicks(MeasuredTicks));

            CommandResult onCmd = await World.ExecuteCommand("/pulse attribution on");
            Assert.True(onCmd.Ok, onCmd.Message);
            await World.Ticks(SettleOnTicks);
            ons.Add(await World.MeasureTicks(MeasuredTicks));
        }

        // The closing off window: without it, every on can only be compared against the off
        // before it, so a baseline moving smoothly in one direction biases every delta the same
        // way. Bracketing each on between two off readings and comparing it to their mean cancels
        // that drift instead of assuming it away.
        CommandResult closeCmd = await World.ExecuteCommand("/pulse attribution off");
        Assert.True(closeCmd.Ok, closeCmd.Message);
        await World.Ticks(SettleOffTicks);
        offs.Add(await World.MeasureTicks(MeasuredTicks));

        double[] deltasMs = new double[Pairs];
        for (int i = 0; i < Pairs; i++)
        {
            double neighbourMeanMs = (offs[i].BusyTime.MedianMs + offs[i + 1].BusyTime.MedianMs) / 2.0;
            deltasMs[i] = ons[i].BusyTime.MedianMs - neighbourMeanMs;
        }

        double medianDeltaMs = Median(deltasMs);
        double ticksPerSecond = offs[0].Passes / offs[0].WallTime.TotalSeconds;

        // A burst profiles BurstTicks + 1 ticks, not BurstTicks: the first tick after the flag
        // comes on is the discarded warm-up sample (its data is thrown away, but the profiler
        // still ran and the engine still paid for it), and BurstTicks more are folded afterward.
        double burstTicks = DefaultBurstTicks + 1;
        double dutyFraction = burstTicks / (burstTicks + (DefaultIntervalSeconds * ticksPerSecond));
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

    /// <summary>Asserts the spawned load is actually loaded and ticking, not just that
    /// <c>SpawnEntity</c> was called: a despawn, an out-of-range placement or a chunk that never
    /// loaded would leave the server measuring an idle world while still claiming a busy one.</summary>
    private void AssertLoadIsLive(int spawned)
    {
        int loaded = World.Api.World.LoadedEntities.Count;
        Assert.True(
            loaded >= spawned,
            $"only {loaded} entities are loaded, fewer than the {spawned} spawned; "
                + "the load is not what this measurement thinks it is measuring against");
    }

    private static async Task WriteReport(string fileName, object result)
    {
        // Atlas sets the working directory to the VS install (VINTAGE_STORY) for the embedded
        // server's own sake, so a relative path here would write into a live game install rather
        // than next to the test output. This assembly's own directory is stable regardless.
        string directory = Path.GetDirectoryName(typeof(AttributionCostScenarios).Assembly.Location)!;
        string json = JsonSerializer.Serialize(result, ReportFormat);
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), json);
        Console.WriteLine(json);
    }

    private static void Skip()
        => Console.WriteLine($"Skipped: set {OptInVariable}=1 to run the real measurement (see class remarks).");

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
