using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Xunit;

namespace Pulse.Tests;

/// <summary>The branches a live server's wiring cannot cover: the unprimed-tick give-up, a failed
/// listener walk, a full burst, and the <c>/pulse</c> on/off/status transitions. The constructor
/// below supplies a resolved profiler, a listener walk and a warning sink directly instead of
/// reading them off <c>ICoreServerAPI</c>, which is what makes all four drivable without a live
/// server.</summary>
public class AttributionMetricsTests
{
    private static string UniqueMeterName([CallerMemberName] string caller = "")
        => $"Pulse.Test.{caller}.{Guid.NewGuid():N}";

    private static AttributionMetrics Metrics(
        Meter meter,
        FrameProfilerUtil profiler,
        List<(string Template, string Message)> warnings,
        bool enabled = true,
        Action<ModOwners>? walkListeners = null)
        => new(
            meter,
            new AttributionConfig { Enabled = enabled, BurstTicks = 5, IntervalSeconds = 1 },
            () => profiler,
            walkListeners ?? (_ => { }),
            (template, message) => warnings.Add((template, message)));

    [Fact]
    public void Tick_KeepsWaiting_WhileThePrimedTickHasNotCompletedYet()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = Metrics(meter, profiler, warnings);

        // Exactly the give-up threshold: still waiting, not yet given up.
        for (int tick = 0; tick < 1000; tick++)
        {
            metrics.Tick(1.0);
        }

        Assert.Empty(warnings);
        Assert.True(profiler.Enabled);
        Assert.Equal(EnumCommandStatus.Success, metrics.Status().Status);
    }

    /// <summary>Half a minute of a primed profiler that never completes a tick, at the default tick
    /// rate, is the give-up threshold. Past it, attribution gives up for the rest of the run instead
    /// of reporting zeros that would look like a server nothing is running on.</summary>
    [Fact]
    public void Tick_GivesUpForGood_WhenThePrimedTickNeverCompletes()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = Metrics(meter, profiler, warnings);

        // Established while attribution is still live, so the assertion below proves the family
        // was retired, not merely that it was never there against a fresh aggregator.
        Assert.Contains(aggregator.Collect(), s => s.Name == "pulse_mod_tick_share");

        for (int tick = 0; tick < 1001; tick++)
        {
            metrics.Tick(1.0);
        }

        (string template, string message) = Assert.Single(warnings);
        Assert.Equal(
            "Pulse could not read the engine's frame profiler ({0}). Per-mod tick attribution is off "
                + "for the rest of this run and its families stop updating; every other metric is "
                + "unaffected.",
            template);
        Assert.Equal("the engine's profiler never completed a primed tick", message);
        Assert.False(profiler.Enabled);

        // Gone for the rest of the run: further ticks are silent no-ops, not a second warning, and
        // the commands report it as unavailable rather than merely off.
        metrics.Tick(1.0);
        Assert.Single(warnings);
        Assert.Equal(EnumCommandStatus.Error, metrics.Status().Status);
        Assert.Equal(EnumCommandStatus.Error, metrics.Switch(true).Status);

        // Giving up must not leave the share family serving whatever it last measured either.
        Assert.DoesNotContain(aggregator.Collect(), s => s.Name == "pulse_mod_tick_share");
    }

    /// <summary>The bug this class exists to fix: pulse_mod_tick_share is an observable gauge
    /// precisely so that switching attribution off makes the family disappear from a scrape, rather
    /// than serve the shares of whichever mod was profiled in the last burst before the restart.
    /// The two counted families are unaffected: they are cumulative and simply stop moving.</summary>
    [Fact]
    public void Switch_Off_Retires_TheShareSeries_InsteadOfFreezingThem()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        // Run a whole burst (interval, warm-up, five folded samples) so the share family carries a
        // real, measured value rather than only the zero seed.
        for (int tick = 0; tick < 7; tick++)
        {
            metrics.Tick(1.0);
        }

        IReadOnlyList<MetricSample> running = aggregator.Collect();
        Assert.Contains(running, s => s.Name == "pulse_mod_tick_share");
        double ticksBefore = running.Single(s => s.Name == "pulse_attribution_ticks_total").Value;

        metrics.Switch(false);

        IReadOnlyList<MetricSample> stopped = aggregator.Collect();
        Assert.DoesNotContain(stopped, s => s.Name == "pulse_mod_tick_share");

        // The cumulative families are untouched by the switch: they simply stop moving.
        Assert.Equal(ticksBefore, stopped.Single(s => s.Name == "pulse_attribution_ticks_total").Value);

        // Switching back on must not flash the burst measured before it was switched off: the
        // remembered shares are cleared, so this is exactly the zero seed, not stale non-zero
        // values and not an empty family either.
        metrics.Switch(true);
        List<MetricSample> restarted = aggregator.Collect().Where(s => s.Name == "pulse_mod_tick_share").ToList();
        Assert.Equal(2, restarted.Count);
        Assert.All(restarted, s => Assert.Equal(0, s.Value));
        Assert.Contains(restarted, s => s.Labels.Any(l => l.Value == "engine"));
        Assert.Contains(restarted, s => s.Labels.Any(l => l.Value == "unattributed"));
    }

    /// <summary>A failed listener walk is not the same failure as an unreadable profiler: it logs
    /// through its own template, and unlike the give-up path above, attribution keeps running.
    /// This is the regression a wrong warning template would slip through unnoticed.</summary>
    [Fact]
    public void Tick_LogsTheListenerWalkTemplate_AndKeepsRunning_WhenWalkingListenersFails()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = Metrics(
            meter, profiler, warnings,
            walkListeners: _ => throw new InvalidOperationException("listener list is gone"));

        // IntervalSeconds is 1, so this one tick both starts the burst and triggers the walk that
        // runs the moment it starts.
        metrics.Tick(1.0);

        (string template, string message) = Assert.Single(warnings);
        Assert.Equal(
            "Pulse could not read the engine's tick listener lists ({0}). Per-mod attribution carries "
                + "on from the mod loader's own type list, which maps fewer marks: the rest report as "
                + "unattributed.",
            template);
        Assert.Equal("listener list is gone", message);
        Assert.Equal(EnumCommandStatus.Success, metrics.Status().Status);
    }

    /// <summary>The whole duty cycle against a warmed profiler: the flag goes on once the interval
    /// passes, stays on through the warm-up tick and every folded sample, and goes back off exactly
    /// when the burst completes, with the four families recorded and nothing warned.</summary>
    [Fact]
    public void Tick_RunsAWholeBurst_AgainstAPrimedProfiler()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = Metrics(meter, profiler, warnings);

        // One tick for the interval to pass and the profiler to come on, one more for the stale
        // warm-up sample the tick that flipped it on never got a Begin() for, then BurstTicks (5)
        // folded samples: the flag is still on after the first six, and only the seventh completes
        // the burst and turns it back off.
        for (int tick = 0; tick < 6; tick++)
        {
            metrics.Tick(1.0);
        }

        Assert.True(profiler.Enabled);

        metrics.Tick(1.0);

        Assert.False(profiler.Enabled);
        Assert.Empty(warnings);

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Contains(samples, s => s.Name == "pulse_attribution_ticks_total");
        Assert.Contains(samples, s => s.Name == "pulse_attribution_dropped_samples_total");
        Assert.Contains(samples, s => s.Name == "pulse_mod_tick_share");
        Assert.Contains(samples, s => s.Name == "pulse_mod_tick_seconds_total");
    }

    [Fact]
    public void Stop_DisablesTheProfiler_WhileAttributionIsStillLive()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        metrics.Stop();

        Assert.False(profiler.Enabled);
    }

    /// <summary>What PulseCommandsTests does not already cover: switching on seeds the families at
    /// zero straight away, and switching off does not touch the profiler flag by itself, the next
    /// tick does. Text formatting is PulseCommands' own job and is tested there.</summary>
    [Fact]
    public void Switch_Seeds_TheFamilies_AndTheProfilerFlagFollowsOnTheNextTick()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        AttributionMetrics metrics = Metrics(meter, profiler, [], enabled: false);

        Assert.Equal(EnumCommandStatus.Success, metrics.Switch(true).Status);

        IReadOnlyList<MetricSample> seeded = aggregator.Collect();
        Assert.Equal(0, seeded.Single(s => s.Name == "pulse_attribution_ticks_total").Value);
        Assert.Equal(0, seeded.Single(s => s.Name == "pulse_attribution_dropped_samples_total").Value);
        Assert.All(seeded.Where(s => s.Name == "pulse_mod_tick_share"), s => Assert.Equal(0, s.Value));
        Assert.All(seeded.Where(s => s.Name == "pulse_mod_tick_seconds_total"), s => Assert.Equal(0, s.Value));

        Assert.Equal(EnumCommandStatus.Success, metrics.Switch(false).Status);
        Assert.True(profiler.Enabled); // Switch alone does not touch the flag.

        metrics.Tick(1.0);
        Assert.False(profiler.Enabled); // The next tick syncs it to the duty cycle's own state.
    }
}
