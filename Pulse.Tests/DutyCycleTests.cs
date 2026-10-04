using Xunit;

namespace Pulse.Tests;

/// <summary>The schedule alone: when a burst starts, how many ticks it lasts, when it ends, and
/// what a switch or a reload does to one in progress. No profiler, no trees and no owners:
/// <c>TickAttributionTests</c> covers what attribution does with each step.</summary>
public class DutyCycleTests
{
    /// <summary>Ticks the cycle <paramref name="ticks"/> times and returns what each tick was.</summary>
    private static DutyStep[] Run(DutyCycle cycle, int ticks, double elapsedSeconds = 1.0)
    {
        DutyStep[] steps = new DutyStep[ticks];
        for (int tick = 0; tick < ticks; tick++)
        {
            steps[tick] = cycle.OnTick(elapsedSeconds);
        }

        return steps;
    }

    [Fact]
    public void Constructor_Floors_TheIntervalAndTheBurstLength()
    {
        DutyCycle cycle = new(0, 0);

        Assert.Equal(1, cycle.BurstTicks);
        Assert.Equal(1, cycle.IntervalSeconds);
    }

    [Fact]
    public void Constructor_Caps_TheBurstLength()
        => Assert.Equal(300, new DutyCycle(100000, 10).BurstTicks);

    [Fact]
    public void Constructor_Keeps_AConfiguredDutyCycle()
    {
        DutyCycle cycle = new(30, 10);

        Assert.True(cycle.Enabled);
        Assert.Equal(30, cycle.BurstTicks);
        Assert.Equal(10, cycle.IntervalSeconds);
    }

    [Fact]
    public void OnTick_StaysIdle_UntilTheIntervalHasPassed()
    {
        DutyCycle cycle = new(5, 10);

        for (int tick = 0; tick < 9; tick++)
        {
            Assert.Equal(DutyStep.Idle, cycle.OnTick(1.0));
            Assert.False(cycle.InBurst);
        }

        Assert.Equal(DutyStep.Start, cycle.OnTick(1.0));
        Assert.True(cycle.InBurst);
    }

    /// <summary>The interval is seconds, not ticks: half-second ticks take twice as many to cross it,
    /// and the burst starts on the one that lands exactly on it.</summary>
    [Fact]
    public void OnTick_CountsTheInterval_InSeconds()
        => Assert.Equal(
            [DutyStep.Idle, DutyStep.Idle, DutyStep.Idle, DutyStep.Start],
            Run(new DutyCycle(5, 2), 4, 0.5));

    /// <summary>Whatever the caller switches on at the start is switched on part-way through that
    /// tick, so what it reads on the next one describes a tick only partly covered. That one is the
    /// warm-up, and it is not a sample.</summary>
    [Fact]
    public void OnTick_WarmsUp_OnTheTickAfterTheBurstStarts()
    {
        DutyCycle cycle = new(1, 1);

        Assert.Equal(DutyStep.Start, cycle.OnTick(1.0));
        Assert.Equal(DutyStep.WarmUp, cycle.OnTick(1.0));
        Assert.True(cycle.InBurst);
        Assert.Equal(DutyStep.LastSample, cycle.OnTick(1.0));
    }

    [Fact]
    public void OnTick_Takes_BurstTicksSamples_ThenEndsTheBurst()
    {
        DutyCycle cycle = new(3, 1);

        Assert.Equal([DutyStep.Start, DutyStep.WarmUp, DutyStep.Sample, DutyStep.Sample], Run(cycle, 4));
        Assert.True(cycle.InBurst);

        Assert.Equal(DutyStep.LastSample, cycle.OnTick(1.0));
        Assert.False(cycle.InBurst);
    }

    /// <summary>The interval counts from the end of a burst, and the next burst has the same shape
    /// as the first: its own warm-up, then its own samples.</summary>
    [Fact]
    public void OnTick_Runs_ASecondBurstAfterTheNextInterval()
    {
        DutyCycle cycle = new(2, 2);
        DutyStep[] oneBurst = [DutyStep.Idle, DutyStep.Start, DutyStep.WarmUp, DutyStep.Sample, DutyStep.LastSample];

        Assert.Equal(oneBurst, Run(cycle, 5));
        Assert.Equal(oneBurst, Run(cycle, 5));
    }

    [Fact]
    public void OnTick_StaysIdle_WhileDisabled()
    {
        DutyCycle cycle = new(1, 1, enabled: false);

        Assert.All(Run(cycle, 100), step => Assert.Equal(DutyStep.Idle, step));
        Assert.False(cycle.Enabled);
        Assert.False(cycle.InBurst);
    }

    /// <summary>The whole point of arming a cycle on a server that did not ask for it: it can be
    /// switched on later, and then it runs exactly as if the config had said so.</summary>
    [Fact]
    public void Apply_Starts_TheCycle_OnAServerThatBootedWithItOff()
    {
        DutyCycle cycle = new(2, 1, enabled: false);
        cycle.OnTick(1.0);

        cycle.Apply(true, 2, 1);

        Assert.Equal([DutyStep.Start, DutyStep.WarmUp, DutyStep.Sample, DutyStep.LastSample], Run(cycle, 4));
    }

    /// <summary>Switching it off part-way through a burst drops the burst instead of finishing it:
    /// the cycle is out of the burst at once, and no later tick is a sample.</summary>
    [Fact]
    public void Apply_Drops_ABurstInProgress_WhenItIsSwitchedOff()
    {
        DutyCycle cycle = new(30, 1);
        Run(cycle, 5);
        Assert.True(cycle.InBurst);

        cycle.Apply(false, 30, 1);

        Assert.False(cycle.Enabled);
        Assert.False(cycle.InBurst);
        Assert.All(Run(cycle, 100), step => Assert.Equal(DutyStep.Idle, step));
        Assert.False(cycle.InBurst);
    }

    /// <summary>A reload that leaves the cycle on drops the burst in progress the same way, and the
    /// burst length it brings is the one the next burst runs.</summary>
    [Fact]
    public void Apply_Drops_ABurstInProgress_WhenItIsReapplied()
    {
        DutyCycle cycle = new(30, 1);
        Run(cycle, 5);
        Assert.True(cycle.InBurst);

        cycle.Apply(true, 2, 1);

        Assert.False(cycle.InBurst);
        Assert.Equal([DutyStep.Start, DutyStep.WarmUp, DutyStep.Sample, DutyStep.LastSample], Run(cycle, 4));
    }

    /// <summary>A new interval is counted from the reload, not from whatever the old one had already
    /// waited.</summary>
    [Fact]
    public void Apply_Restarts_TheInterval_OfARunningCycle()
    {
        DutyCycle cycle = new(5, 10);
        Run(cycle, 6);

        cycle.Apply(true, 5, 10);

        Assert.All(Run(cycle, 9), step => Assert.Equal(DutyStep.Idle, step));
        Assert.Equal(DutyStep.Start, cycle.OnTick(1.0));
    }

    /// <summary>Switching back on starts a fresh cycle, so the burst begins with its warm-up again
    /// rather than carrying on from where the old one stopped.</summary>
    [Fact]
    public void Apply_WarmsUpAgain_WhenItIsSwitchedBackOn()
    {
        DutyCycle cycle = new(2, 1);
        Run(cycle, 3);
        cycle.Apply(false, 2, 1);

        cycle.Apply(true, 2, 1);

        Assert.Equal([DutyStep.Start, DutyStep.WarmUp, DutyStep.Sample, DutyStep.LastSample], Run(cycle, 4));
    }

    [Fact]
    public void Apply_Takes_ANewDutyCycle_AndClampsItTheSameWay()
    {
        DutyCycle cycle = new(30, 10);

        cycle.Apply(true, 100000, 0);

        Assert.Equal(300, cycle.BurstTicks);
        Assert.Equal(1, cycle.IntervalSeconds);
    }
}
