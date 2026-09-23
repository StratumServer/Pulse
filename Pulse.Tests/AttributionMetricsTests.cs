using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Xunit;

namespace Pulse.Tests;

/// <summary>The two branches a live server's wiring cannot cover: the unprimed-tick give-up, and
/// the <c>/pulse</c> on/off/status transitions. The constructor below supplies a resolved profiler
/// and a plain warning sink directly instead of reading them off <c>ICoreServerAPI</c>, which is
/// what makes both drivable without a live server.</summary>
public class AttributionMetricsTests
{
    private static string UniqueMeterName([CallerMemberName] string caller = "")
        => $"Pulse.Test.{caller}.{Guid.NewGuid():N}";

    private static AttributionMetrics Metrics(
        Meter meter, FrameProfilerUtil profiler, List<string> warnings, bool enabled = true)
        => new(
            meter,
            new AttributionConfig { Enabled = enabled, BurstTicks = 5, IntervalSeconds = 1 },
            () => profiler,
            warnings.Add);

    [Fact]
    public void Tick_KeepsWaiting_WhileThePrimedTickHasNotCompletedYet()
    {
        using Meter meter = new(UniqueMeterName());
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        List<string> warnings = [];
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
        FrameProfilerUtil profiler = new("test") { Enabled = true };
        List<string> warnings = [];
        AttributionMetrics metrics = Metrics(meter, profiler, warnings);

        for (int tick = 0; tick < 1001; tick++)
        {
            metrics.Tick(1.0);
        }

        string warning = Assert.Single(warnings);
        Assert.Equal("the engine's profiler never completed a primed tick", warning);
        Assert.False(profiler.Enabled);

        // Gone for the rest of the run: further ticks are silent no-ops, not a second warning, and
        // the commands report it as unavailable rather than merely off.
        metrics.Tick(1.0);
        Assert.Single(warnings);
        Assert.Equal(EnumCommandStatus.Error, metrics.Status().Status);
        Assert.Equal(EnumCommandStatus.Error, metrics.Switch(true).Status);
    }

    [Fact]
    public void Switch_StartsAndStops_TheDutyCycle_AndReportsItInTheReply()
    {
        using Meter meter = new(UniqueMeterName());
        AttributionMetrics metrics = Metrics(meter, new FrameProfilerUtil("test"), [], enabled: false);

        TextCommandResult on = metrics.Switch(true);
        Assert.Equal(EnumCommandStatus.Success, on.Status);
        Assert.StartsWith("Attribution is on: bursts of 5 ticks every 1s.", on.StatusMessage);
        Assert.StartsWith("Attribution is on:", metrics.Status().StatusMessage);

        TextCommandResult off = metrics.Switch(false);
        Assert.Equal(EnumCommandStatus.Success, off.Status);
        Assert.StartsWith("Attribution is off, and the engine's frame profiler with it.", off.StatusMessage);
        Assert.StartsWith("Attribution is off.", metrics.Status().StatusMessage);
    }
}
