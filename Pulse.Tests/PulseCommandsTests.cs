using Xunit;

namespace Pulse.Tests;

/// <summary>The replies an operator reads off their console, and the comparison behind the one
/// sentence that could quietly mislead them: which keys the file has moved away from and a reload
/// cannot do anything about.</summary>
public class PulseCommandsTests
{
    private static PulseConfig Config() => new()
    {
        Enabled = true,
        Bind = "127.0.0.1",
        Port = 9464,
        RuntimeMetrics = true,
        ChunksRefreshSeconds = 30,
    };

    [Fact]
    public void RestartKeys_Reports_Nothing_WhenTheFileMatchesTheRunningServer()
        => Assert.Empty(PulseCommands.RestartKeys(Config(), Config()));

    /// <summary>The Attribution block is the one part a reload does apply, so a change there is
    /// not a reason to restart and must not be named as one.</summary>
    [Fact]
    public void RestartKeys_Ignores_TheAttributionBlock()
    {
        PulseConfig loaded = Config();
        loaded.Attribution = new AttributionConfig { Enabled = true, BurstTicks = 7, IntervalSeconds = 2 };

        Assert.Empty(PulseCommands.RestartKeys(Config(), loaded));
    }

    [Fact]
    public void RestartKeys_Names_EveryKeyThatIsReadOnlyAtStartup()
    {
        PulseConfig loaded = Config();
        loaded.Enabled = false;
        loaded.Bind = "0.0.0.0";
        loaded.Port = 9999;
        loaded.RuntimeMetrics = false;
        loaded.ChunksRefreshSeconds = 5;

        Assert.Equal(
            ["Enabled", "Bind", "Port", "RuntimeMetrics", "ChunksRefreshSeconds"],
            PulseCommands.RestartKeys(Config(), loaded));
    }

    [Fact]
    public void RestartKeys_Names_OnlyTheKeysThatActuallyDiffer()
    {
        PulseConfig loaded = Config();
        loaded.Port = 9999;

        Assert.Equal(["Port"], PulseCommands.RestartKeys(Config(), loaded));
    }

    /// <summary>Switching attribution deliberately never writes the file, and the reply has to say
    /// so: an operator who took a ten minute look must not find it still running next month.</summary>
    [Fact]
    public void Switched_Says_TheFileWasNotTouched()
    {
        Assert.Contains("pulse.json was not changed", PulseCommands.Switched(true, 30, 10));
        Assert.Contains("pulse.json was not changed", PulseCommands.Switched(false, 30, 10));
    }

    [Fact]
    public void Switched_Reports_TheDutyCycleItStarted()
        => Assert.StartsWith("Attribution is on: bursts of 30 ticks every 10s.", PulseCommands.Switched(true, 30, 10));

    [Fact]
    public void Switched_Reports_TheProfilerGoingOffToo()
        => Assert.StartsWith("Attribution is off, and the engine's frame profiler with it.", PulseCommands.Switched(false, 30, 10));

    [Fact]
    public void Status_Reports_TheCycle_TheTicksProfiled_AndTheBurstInProgress()
        => Assert.Equal(
            "Attribution is on: bursts of 30 ticks every 10s, 120 ticks profiled so far, profiling right now.",
            PulseCommands.Status(true, 30, 10, 120, inBurst: true));

    [Fact]
    public void Status_Distinguishes_IdlingFromProfiling()
        => Assert.Equal(
            "Attribution is on: bursts of 30 ticks every 10s, 120 ticks profiled so far, waiting for the next burst.",
            PulseCommands.Status(true, 30, 10, 120, inBurst: false));

    /// <summary>Off, the cycle is what it would use, not what it is using, and the count is what a
    /// previous stretch of profiling left behind.</summary>
    [Fact]
    public void Status_Reports_TheCycleItWouldUse_WhenItIsOff()
        => Assert.Equal(
            "Attribution is off. It would run bursts of 5 ticks every 1s; 40 ticks profiled so far.",
            PulseCommands.Status(false, 5, 1, 40, inBurst: false));

    [Fact]
    public void Reloaded_Says_WhatItApplied_AndThatNothingElseMoved()
        => Assert.Equal(
            "Reloaded pulse.json. Attribution is on: bursts of 7 ticks every 2s. "
                + "Nothing else in the file differs from what the server is running.",
            PulseCommands.Reloaded(true, 7, 2, []));

    [Fact]
    public void Reloaded_Names_TheOneKeyThatNeedsARestart()
        => Assert.EndsWith(
            "Port differs from what the server is running and needs a restart.",
            PulseCommands.Reloaded(false, 30, 10, ["Port"]));

    [Fact]
    public void Reloaded_Lists_SeveralKeysThatNeedARestart()
        => Assert.EndsWith(
            "Port, Bind differ from what the server is running and need a restart.",
            PulseCommands.Reloaded(false, 30, 10, ["Port", "Bind"]));

    /// <summary>The engine runs a command reply through string.Format on its way to whoever asked,
    /// so the parse error goes in as a parameter. A sentence that interpolated it would throw on
    /// the very typo it is reporting: a stray brace in the JSON.</summary>
    [Fact]
    public void ReloadFailed_Keeps_TheErrorAsAFormatParameter()
    {
        Assert.Contains("{0}", PulseCommands.ReloadFailed);
        Assert.Equal(
            "Pulse could not read pulse.json (Unexpected character: }). Nothing changed: the server "
                + "is still running the config it booted with.",
            string.Format(PulseCommands.ReloadFailed, "Unexpected character: }"));
    }
}
