using System.Text.Json;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>Measures attribution's real tick cost against a live engine, replacing the mark-count
/// estimate the README used to carry. Spawns a real entity load, then uses Atlas 0.14.0's
/// <c>World.MeasureTicks</c> to read tick busy time with attribution off, on mid-burst, and
/// derives the amortised cost at the shipped default duty cycle from those two readings and the
/// measured tick rate.
/// <para>Spawning thousands of entities and holding a burst open across a measurement window is
/// slow, and the result is meant to be read by hand and copied into the README, not gated on
/// every push, so this stays a no-op unless <c>PULSE_MEASURE_ATTRIBUTION_COST=1</c> is set: the
/// default CI run (and a plain local <c>dotnet test</c>) still boots the class, at the same cost
/// as any other scenario class, but returns immediately. Run it for real, with VINTAGE_STORY
/// set: <c>PULSE_MEASURE_ATTRIBUTION_COST=1 dotnet test Pulse.Scenarios --filter
/// "Category=Cost"</c>. The numbers are written to <c>attribution-cost.json</c> next to the test
/// binaries and printed to the test's own output.</para></summary>
[Trait("Category", "Cost")]
[AtlasDataFiles("data/attributioncost/pulse.json", TargetPath = "ModConfig")]
public class AttributionCostScenarios : AtlasScenarioBase
{
    private const string OptInVariable = "PULSE_MEASURE_ATTRIBUTION_COST";
    private const int TargetEntities = 4000;
    private const int MeasuredTicks = 100;
    private const double TickBudgetMs = 33.333;

    // The shipped default (PulseConfig.AttributionConfig), what the README documents. The
    // fixture sets a much wider burst so the "on" measurement below can sit safely inside one
    // continuous profiled window; the amortised figure is then computed at these defaults, not
    // at the wide-open burst used to take a clean reading.
    private const int DefaultBurstTicks = 30;
    private const int DefaultIntervalSeconds = 10;

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task Attribution_Cost_Is_Measured_Off_OnBurst_AndAmortised()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Console.WriteLine($"Skipped: set {OptInVariable}=1 to run the real measurement (see class remarks).");
            return;
        }

        ITestPlayer anchor = await World.JoinPlayer("load-anchor");
        int spawned = SpawnLoad(anchor.Position, TargetEntities);

        // Let the drop, chunk relight and the first AI ticks settle before measuring either side.
        await World.Ticks(60);
        TickMeasurement off = await World.MeasureTicks(MeasuredTicks);

        await World.ExecuteCommand("/pulse attribution on");

        // The fixture's IntervalSeconds (1) elapses in about 30 ticks at default pacing, or fewer
        // under load (a slower tick banks wall-clock time faster); 60 ticks clears it plus the
        // one discarded warm-up sample with margin, while the 300-tick burst it lands in still
        // has well over MeasuredTicks left to give.
        await World.Ticks(60);
        TickMeasurement on = await World.MeasureTicks(MeasuredTicks);

        double ticksPerSecond = off.Passes / off.WallTime.TotalSeconds;
        double idleTicksPerCycle = DefaultIntervalSeconds * ticksPerSecond;
        double dutyFraction = DefaultBurstTicks / (DefaultBurstTicks + idleTicksPerCycle);
        double amortisedMs = off.BusyTime.MedianMs + (dutyFraction * (on.BusyTime.MedianMs - off.BusyTime.MedianMs));

        var result = new
        {
            entitiesSpawned = spawned,
            tickBudgetMs = TickBudgetMs,
            off = new { off.Passes, off.BusyTime.MedianMs, off.BusyTime.P95Ms },
            onBurst = new { on.Passes, on.BusyTime.MedianMs, on.BusyTime.P95Ms },
            ticksPerSecond,
            defaultBurstTicks = DefaultBurstTicks,
            defaultIntervalSeconds = DefaultIntervalSeconds,
            dutyFraction,
            amortisedMs,
            offPctOfBudget = off.BusyTime.MedianMs / TickBudgetMs * 100,
            onBurstPctOfBudget = on.BusyTime.MedianMs / TickBudgetMs * 100,
            amortisedPctOfBudget = amortisedMs / TickBudgetMs * 100,
        };

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync("attribution-cost.json", json);
        Console.WriteLine(json);

        // A sanity gate, not just a report. Bounded on attribution's own marginal share rather
        // than on the absolute busy time: a dense enough load can push the baseline tick itself
        // past the budget on a loaded machine with nothing to do with attribution (measured:
        // 105% of budget with attribution off on one run), so the whole-tick percentages above
        // are not something this assertion can bound without also failing on a slow host.
        Assert.True(spawned > 0, "no load was spawned to measure against");
        Assert.True(
            on.BusyTime.MedianMs >= off.BusyTime.MedianMs,
            $"attribution on ({on.BusyTime.MedianMs}ms) read cheaper than off ({off.BusyTime.MedianMs}ms)");
        double burstDeltaMs = on.BusyTime.MedianMs - off.BusyTime.MedianMs;
        Assert.True(
            burstDeltaMs < TickBudgetMs,
            $"attribution's own marginal cost ({burstDeltaMs}ms) exceeds a whole tick budget ({TickBudgetMs}ms)");
    }

    private int SpawnLoad(BlockPos origin, int target)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(target));
        int half = side / 2;
        int spawned = 0;
        for (int x = 0; x < side && spawned < target; x++)
        {
            for (int z = 0; z < side && spawned < target; z++)
            {
                World.SpawnEntity("game:chicken-hen", origin.Offset(x - half, 1, z - half));
                spawned++;
            }
        }

        return spawned;
    }
}
