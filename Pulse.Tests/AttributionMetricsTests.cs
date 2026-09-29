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

    /// <summary>Advances the duty cycle through the interval tick, the discarded warm-up sample,
    /// and a whole five-tick burst (the <see cref="Metrics"/> helper's own BurstTicks), folding
    /// <paramref name="tick"/> every time a tick is actually profiled. Setting
    /// <c>profiler.PrevRootEntry</c> once up front, the way <c>TickAttributionTests</c> builds its
    /// own trees, rather than calling Begin/End keeps the burst's arithmetic exact: a hand-built
    /// tree has no timing noise for entry.Value or BusySeconds to pick up.</summary>
    private static void RunWholeBurst(AttributionMetrics metrics, FrameProfilerUtil profiler, ProfileEntryRange tick)
    {
        profiler.PrevRootEntry = tick;
        for (int i = 0; i < 7; i++)
        {
            metrics.Tick(1.0);
        }
    }

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

    private const string AttributionWarning =
        "Pulse could not read the engine's frame profiler ({0}). Per-mod tick attribution is off "
        + "for the rest of this run and its families stop updating; every other metric is "
        + "unaffected.";

    /// <summary>A resolver that returns null rather than throwing (the world is not the type Pulse
    /// expects, say) is not a failure: <c>Tick</c> is simply a no-op for that call, attribution
    /// stays alive, and nothing is warned.</summary>
    [Fact]
    public void Tick_DoesNothing_WhenTheProfilerCannotBeResolvedAtAll()
    {
        using Meter meter = new(UniqueMeterName());
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = new(
            meter,
            new AttributionConfig { Enabled = true, BurstTicks = 5, IntervalSeconds = 1 },
            () => null,
            _ => { },
            (template, message) => warnings.Add((template, message)));

        metrics.Tick(1.0);

        Assert.Empty(warnings);
        Assert.Equal(EnumCommandStatus.Success, metrics.Status().Status);
    }

    /// <summary>Item 4: a resolver that throws, the shape a future engine reshaping
    /// FrameProfilerUtil or ProfileEntryRange would take, degrades once instead of crashing or
    /// repeating on every tick. The retry right after, from the give-up path's own best-effort
    /// shutdown, still succeeds here because the resolver only fails the first time.</summary>
    [Fact]
    public void Tick_Degrades_WhenTheProfilerResolverThrows_AndStillDisablesTheProfilerAfterward()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        List<(string Template, string Message)> warnings = [];
        int calls = 0;
        AttributionMetrics metrics = new(
            meter,
            new AttributionConfig { Enabled = false, BurstTicks = 5, IntervalSeconds = 1 },
            () =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new TypeLoadException("FrameProfilerUtil reshaped");
                }

                return profiler;
            },
            _ => { },
            (template, message) => warnings.Add((template, message)));

        metrics.Tick(1.0);

        (string template, string message) = Assert.Single(warnings);
        Assert.Equal(AttributionWarning, template);
        Assert.Equal("FrameProfilerUtil reshaped", message);
        Assert.False(profiler.Enabled);
        Assert.Equal(EnumCommandStatus.Error, metrics.Status().Status);
    }

    /// <summary>The retry itself can fail too, the same reshape breaking both reads: the second
    /// failure is swallowed rather than escaping the tick listener a second time.</summary>
    [Fact]
    public void Tick_Degrades_WhenTheProfilerResolverThrows_AndTheRetryAlsoFails()
    {
        using Meter meter = new(UniqueMeterName());
        List<(string Template, string Message)> warnings = [];
        AttributionMetrics metrics = new(
            meter,
            new AttributionConfig { Enabled = false, BurstTicks = 5, IntervalSeconds = 1 },
            () => throw new TypeLoadException("FrameProfilerUtil reshaped"),
            _ => { },
            (template, message) => warnings.Add((template, message)));

        Exception? escaped = Record.Exception(() => metrics.Tick(1.0));

        Assert.Null(escaped);
        (string template, string message) = Assert.Single(warnings);
        Assert.Equal(AttributionWarning, template);
        Assert.Equal("FrameProfilerUtil reshaped", message);
        Assert.Equal(EnumCommandStatus.Error, metrics.Status().Status);
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

    /// <summary>Names, units and help text are not decoration: Unit feeds PrometheusText's own
    /// translation of the OTLP unit into a name suffix, and Help becomes the exported HELP line,
    /// so a wrong one is wrong in what a server operator actually reads.</summary>
    [Fact]
    public void Constructor_Names_TheFourInstruments_WithTheStatedUnitAndHelpText()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        metrics.Seed();
        IReadOnlyList<MetricSample> samples = aggregator.Collect();

        MetricSample share = samples.First(s => s.Name == "pulse_mod_tick_share");
        Assert.Equal("{share}", share.Unit);
        Assert.Equal(
            "Fraction of the profiled main-thread busy time attributed to one mod over the last completed burst, while attribution is running.",
            share.Help);

        MetricSample seconds = samples.First(s => s.Name == "pulse_mod_tick_seconds_total");
        Assert.Equal("s", seconds.Unit);
        Assert.Equal(
            "Main-thread seconds attributed to one mod while attribution was profiling. Sampled: this is time inside the bursts, not since startup.",
            seconds.Help);

        MetricSample ticks = samples.Single(s => s.Name == "pulse_attribution_ticks_total");
        Assert.Equal("{tick}", ticks.Unit);
        Assert.Equal(
            "Ticks actually profiled, so the sampled seconds can be normalised against the ticks they came from.",
            ticks.Help);

        MetricSample dropped = samples.Single(s => s.Name == "pulse_attribution_dropped_samples_total");
        Assert.Equal("{sample}", dropped.Unit);
        Assert.Equal(
            "Profiler marks discarded because their elapsed time had overflowed the engine's 32 bit counter.",
            dropped.Help);
    }

    /// <summary>No mod-owned marks at all: every busy tick lands on the engine bucket, so its
    /// measured seconds and the burst's busy seconds are the same number by construction. The
    /// share has to come out at exactly 1, not merely close to it, which is what tells a real
    /// division from one that quietly turned into a multiplication or a fixed 0 or 1.</summary>
    [Fact]
    public void PublishBurst_Reports_TheEngineShare_AsExactlyItsFractionOfBusyTime()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        RunWholeBurst(metrics, profiler, new ProfileEntryRange { ElapsedTicks = 1000 });

        MetricSample share = Assert.Single(
            aggregator.Collect(), s => s.Name == "pulse_mod_tick_share" && s.Labels.Any(l => l.Value == "engine"));
        Assert.Equal(1.0, share.Value);
    }

    /// <summary>The whole tick is the sleep mark Fold() subtracts before charging anything to the
    /// engine, so busy time and the engine's own seconds both land on exactly zero. An idle burst
    /// like this has to publish a zero share, not the NaN a 0/0 division would otherwise leak into
    /// the exported metric.</summary>
    [Fact]
    public void PublishBurst_Reports_AZeroShare_InsteadOfDividingByZero_WhenABurstMeasuresNoBusyTime()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        RunWholeBurst(metrics, profiler, new ProfileEntryRange
        {
            ElapsedTicks = 400,
            Marks = new Dictionary<string, ProfileEntry> { ["sleep"] = new ProfileEntry(400, 1) },
        });

        MetricSample share = Assert.Single(
            aggregator.Collect(), s => s.Name == "pulse_mod_tick_share" && s.Labels.Any(l => l.Value == "engine"));
        Assert.Equal(0.0, share.Value);
    }

    /// <summary>The mark's prefix names an owner, but the default walkListeners passed to
    /// <see cref="Metrics"/> never populates one, so the whole tick's busy time is charged to
    /// "unattributed" and nothing is left over for "engine". Both still have to appear exactly
    /// once each: not dropped (an empty family) and not doubled (the real value published once by
    /// the loop and then again by its own zero fallback).</summary>
    [Fact]
    public void PublishBurst_Reports_AnUnclaimedMark_AsUnattributed_WithoutDuplicatingEitherBucket()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        RunWholeBurst(metrics, profiler, new ProfileEntryRange
        {
            ElapsedTicks = 1000,
            Marks = new Dictionary<string, ProfileEntry> { ["gmleSome.Mod.Thing"] = new ProfileEntry(1000, 1) },
        });

        List<MetricSample> shares = aggregator.Collect().Where(s => s.Name == "pulse_mod_tick_share").ToList();
        Assert.Equal(2, shares.Count);
        Assert.Equal(0.0, Assert.Single(shares, s => s.Labels.Any(l => l.Value == "engine")).Value);
        Assert.Equal(1.0, Assert.Single(shares, s => s.Labels.Any(l => l.Value == "unattributed")).Value);
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

    /// <summary>The bug this suite exists to catch: Tick used to write
    /// <c>FrameProfilerUtil.Enabled</c> unconditionally on every call, even while it stayed false
    /// the whole time, which clobbered <c>/debug logticks</c>'s own <c>Enabled = true</c> a tick
    /// after it was set. An idle tick (below the burst interval, so the duty cycle itself never
    /// changes state) is the simplest case: nothing about attribution should touch the flag at
    /// all.</summary>
    [Fact]
    public void Tick_LeavesAnotherWritersProfilerEnabled_ThroughAnIdleTick()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true, PrintSlowTicks = true };
        profiler.Begin("tick");
        profiler.End();
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        // Well under the burst helper's one second interval: the duty cycle stays idle.
        metrics.Tick(0.1);

        Assert.True(profiler.Enabled);
    }

    /// <summary>The same guarantee across an entire burst: the flag must survive the transition
    /// that starts profiling, every tick folded during it, and the transition that ends it, which
    /// is exactly where the old unconditional write broke it (Profiling flips back to false the
    /// instant <c>Take()</c> closes the burst).</summary>
    [Fact]
    public void Tick_LeavesAnotherWritersProfilerEnabled_ThroughAWholeBurst()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true, PrintSlowTicks = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        RunWholeBurst(metrics, profiler, new ProfileEntryRange { ElapsedTicks = 1000 });

        Assert.True(profiler.Enabled);
    }

    /// <summary>The exact probe from the release review: a primed profiler, attribution off (the
    /// default), and another writer (<c>/debug logticks</c>) setting <c>PrintSlowTicks</c> and
    /// <c>Enabled</c> the way <c>CmdDebug</c> does. One tick used to be enough to clear
    /// <c>Enabled</c> even though nothing about attribution ever asked for that.</summary>
    [Fact]
    public void Tick_LeavesAnotherWritersProfilerEnabled_WhileAttributionIsOff()
    {
        using Meter meter = new(UniqueMeterName());
        // Primed first, the way a real server would have already done long before an operator
        // ever runs /debug logticks: Begin/End need Enabled or PrintSlowTicks set to build a tree
        // at all, which is exactly why PrevRootEntry is what proves "primed", not either flag.
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        profiler.Enabled = false;

        // What CmdDebug does for /debug logticks, after the fact.
        profiler.PrintSlowTicks = true;
        profiler.Enabled = true;
        AttributionMetrics metrics = Metrics(meter, profiler, [], enabled: false);

        metrics.Tick(1.0);

        Assert.True(profiler.Enabled);
    }

    /// <summary>The release review's missing case: every other "another writer" probe above sets
    /// <c>PrintSlowTicks</c>, which is <c>/debug logticks</c>'s own signature. A mod that just turns
    /// the profiler on for its own reasons, without that flag, is a different writer, and the guard
    /// in <see cref="AttributionMetrics.SetProfilerEnabled"/> is not what protects it while
    /// attribution is off: <c>profilerEnabledLastWritten</c> staying false is. Comparing against
    /// <c>attribution.Profiling</c> instead of that field would have skipped this write anyway on an
    /// idle tick, so only mutating the comparison itself (see <c>tools/mutation-check.sh</c>) tells
    /// these two apart.</summary>
    [Fact]
    public void Tick_LeavesAnotherNonLogticksWritersProfilerEnabled_WhileAttributionIsOff()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        profiler.Enabled = false;
        AttributionMetrics metrics = Metrics(meter, profiler, [], enabled: false);

        // Primes on this tick; attribution never turns on, so Pulse's own write leaves the flag
        // off, same as the profiler already was.
        metrics.Tick(1.0);

        // A non-logticks mod turns the profiler on afterwards, on its own account.
        profiler.Enabled = true;

        for (int i = 0; i < 5; i++)
        {
            metrics.Tick(1.0);
        }

        Assert.True(profiler.Enabled);
    }

    /// <summary>The same guarantee while attribution is enabled but has not started a burst yet:
    /// idle ticks below the interval must not touch a flag attribution never wrote.</summary>
    [Fact]
    public void Tick_LeavesAnotherNonLogticksWritersProfilerEnabled_WhileAttributionIsIdle()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        profiler.Begin("tick");
        profiler.End();
        profiler.Enabled = false;
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        // Primes on this tick, well under the burst helper's one second interval, so the duty
        // cycle stays idle rather than starting a burst.
        metrics.Tick(0.1);

        // A non-logticks mod turns the profiler on afterwards, on its own account.
        profiler.Enabled = true;

        for (int i = 0; i < 5; i++)
        {
            metrics.Tick(0.1);
        }

        Assert.True(profiler.Enabled);
    }

    /// <summary>The guard must not cost attribution its own measurements: a burst folds and
    /// publishes exactly as it would with <c>PrintSlowTicks</c> off, and the flag it leaves alone
    /// throughout is proof the skipped write was the only thing skipped.</summary>
    [Fact]
    public void Tick_StillMeasuresABurstCorrectly_WhileLogticksIsOn()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true, PrintSlowTicks = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        RunWholeBurst(metrics, profiler, new ProfileEntryRange
        {
            ElapsedTicks = 1000,
            Marks = new Dictionary<string, ProfileEntry> { ["gmleSome.Mod.Thing"] = new ProfileEntry(1000, 1) },
        });

        List<MetricSample> shares = aggregator.Collect().Where(s => s.Name == "pulse_mod_tick_share").ToList();
        Assert.Equal(2, shares.Count);
        Assert.Equal(1.0, Assert.Single(shares, s => s.Labels.Any(l => l.Value == "unattributed")).Value);
        Assert.True(profiler.Enabled);
    }

    /// <summary>Stop is one of the transitions this class itself writes the flag on, and it is
    /// bound by the same rule as every other one: not while another writer has asked to keep
    /// profiling running.</summary>
    [Fact]
    public void Stop_LeavesAnotherWritersProfilerEnabled_WhileItIsSet()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true, PrintSlowTicks = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        metrics.Stop();

        Assert.True(profiler.Enabled);
    }

    /// <summary>Seed() is also called from Switch(true), which only ever calls it once attribution
    /// is already enabled; nothing previously drove the guard's own early return while attribution
    /// was still off.</summary>
    [Fact]
    public void Seed_DoesNothing_WhileAttributionIsDisabled()
    {
        using Meter meter = new(UniqueMeterName());
        using MetricsAggregator aggregator = new(meter.Name);
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, [], enabled: false);

        metrics.Seed();

        Assert.Empty(aggregator.Collect());
    }

    /// <summary>The regression the release review found in the first fix for item 1:
    /// <c>Switch</c> and <c>Reload</c> both go through <c>TickAttribution.Apply</c>, which restarts
    /// the duty cycle and sets <c>Profiling</c> false between ticks, before the next call to
    /// <c>Tick</c> ever runs. A write keyed on comparing <c>Profiling</c> before and after one
    /// <c>OnTick</c> call never sees that change (both reads land on the same, already-restarted
    /// value), so calling <c>Switch(false)</c> mid-burst used to leave the engine's frame profiler
    /// on for the rest of the run. This drives the duty cycle through a whole primed tick first,
    /// so the burst is genuinely running (profiler on, a real transition already behind it) before
    /// <c>Switch(false)</c> fires.</summary>
    [Fact]
    public void Switch_Off_DuringABurst_TurnsTheProfilerOff_OnTheNextTick()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        // One primed tick is enough to start the burst: IntervalSeconds is 1, so this tick both
        // primes and crosses the interval, leaving Profiling true and the flag written true.
        profiler.PrevRootEntry = new ProfileEntryRange { ElapsedTicks = 1000 };
        metrics.Tick(1.0);
        Assert.True(profiler.Enabled);

        metrics.Switch(false);
        // Switch alone does not touch the flag (see the test below): still true right after it.
        Assert.True(profiler.Enabled);

        metrics.Tick(1.0);

        Assert.False(profiler.Enabled);
    }

    /// <summary>The same regression from the other direction: switching (or reloading) back on
    /// mid-burst also restarts the duty cycle, so the profiler has to go idle-off immediately and
    /// stay off until the next burst actually starts, rather than sit on, unread, for the whole
    /// interval the old before/after comparison let it coast through.</summary>
    [Fact]
    public void Switch_OnMidBurst_TurnsTheProfilerOff_ThroughTheIdleIntervalUntilTheNextBurst()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        profiler.PrevRootEntry = new ProfileEntryRange { ElapsedTicks = 1000 };
        metrics.Tick(1.0);
        Assert.True(profiler.Enabled);

        // Already on; restarts the duty cycle exactly like a config reload would mid-burst. The
        // confirming ticks below are 0.1s, not the 1.0s the first tick used: IntervalSeconds is 1,
        // so a 1.0s tick would cross it immediately on its own, restart or no restart, and prove
        // nothing about the restart itself.
        metrics.Switch(true);

        metrics.Tick(0.1);
        Assert.False(profiler.Enabled);

        // Stays off for the rest of the one second interval: five 0.1s ticks (this one and the
        // four below) sum to 0.5s, still short of the 1s interval, so the flag must not sit on,
        // unread, through the wait.
        for (int i = 0; i < 4; i++)
        {
            metrics.Tick(0.1);
            Assert.False(profiler.Enabled);
        }
    }

    /// <summary>Item 2 of the release review's new defects: while a burst is running, the flag now
    /// has to be re-asserted every tick, not only on the tick the burst starts on, because
    /// <c>/debug logticks</c> or another mod can turn <c>FrameProfilerUtil.Enabled</c> back off
    /// mid-burst. Left alone, the engine's own <c>End()</c> then returns early against a stale
    /// tree, and the rest of the burst folds and publishes that same stale sample.</summary>
    [Fact]
    public void Tick_ReassertsTheProfilerOn_WhenAnotherWriterClearsItMidBurst()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        AttributionMetrics metrics = Metrics(meter, profiler, []);

        // Starts the burst (interval crossed) and folds it warm, same as the tests above.
        profiler.PrevRootEntry = new ProfileEntryRange { ElapsedTicks = 1000 };
        metrics.Tick(1.0);
        Assert.True(profiler.Enabled);

        // What /debug logticks (or another mod) turning itself off mid-burst looks like from here.
        profiler.Enabled = false;

        metrics.Tick(1.0);

        Assert.True(profiler.Enabled);
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
        // Assert.All over a Where(...) passes vacuously on zero matches, which is exactly what a
        // dropped per-modid Add would produce: this is what actually proves the family is there.
        Assert.Equal(2, seeded.Count(s => s.Name == "pulse_mod_tick_seconds_total"));

        Assert.Equal(EnumCommandStatus.Success, metrics.Switch(false).Status);
        Assert.True(profiler.Enabled); // Switch alone does not touch the flag.

        metrics.Tick(1.0);
        Assert.False(profiler.Enabled); // The next tick syncs it to the duty cycle's own state.
    }
}
