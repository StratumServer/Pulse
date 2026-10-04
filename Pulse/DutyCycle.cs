namespace Pulse;

/// <summary>The schedule of a measurement too costly to leave running: idle for an interval, then
/// a burst of consecutive ticks, then idle again.</summary>
/// <remarks>Only the schedule. It is told how long each tick took and says what this tick is for
/// (<see cref="DutyStep"/>); what a burst switches on, what it reads and where it publishes is the
/// caller's, and nothing here knows about meters, the server or the engine. That is what makes the
/// whole schedule drivable from a unit test, and what lets every measurement that runs in bursts
/// share it instead of writing it out again.
/// <para>A burst starts on one tick, spends the next as a warm-up, and takes
/// <see cref="BurstTicks"/> samples after that. The warm-up exists because whatever the caller
/// switches on at the start is switched on part-way through that tick, and what the caller reads at
/// the start of the next one describes the tick before: a tick only partly covered, so it never
/// counts. Every sample counts toward the burst whatever the caller managed to read on it, so a
/// burst always ends.</para></remarks>
internal sealed class DutyCycle
{
    /// <summary>Shortest interval between bursts. The duty cycle is the whole reason a measurement
    /// like this is affordable, so it stays a duty cycle.</summary>
    public const int MinimumIntervalSeconds = 1;

    /// <summary>Longest burst. Ten seconds at the default tick rate, which is already far more than
    /// tick composition varies over.</summary>
    public const int MaximumBurstTicks = 300;

    private double idleSeconds;
    private int burstTicksElapsed;
    private bool warm;

    public DutyCycle(int burstTicks, int intervalSeconds, bool enabled = true)
        => Apply(enabled, burstTicks, intervalSeconds);

    /// <summary>Whether the cycle runs at all. Off, every tick is <see cref="DutyStep.Idle"/>.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Samples per burst, not counting the warm-up.</summary>
    public int BurstTicks { get; private set; }

    /// <summary>Seconds between the end of one burst and the start of the next.</summary>
    public int IntervalSeconds { get; private set; }

    /// <summary>Whether a burst is running as of the last tick: true after a start, a warm-up and a
    /// sample, false after an idle tick and after the last sample, which ends the burst.</summary>
    public bool InBurst { get; private set; }

    /// <summary>Takes a cycle, clamped the way the config file's is, and starts it over.</summary>
    /// <remarks>Restarting rather than adjusting in place is what makes switching this off
    /// mid-burst safe: the burst in progress is dropped instead of finished, and a later switch-on
    /// begins from idle with the whole interval still to wait. A new interval or burst length
    /// applied to a running cycle restarts it the same way.</remarks>
    public void Apply(bool enabled, int burstTicks, int intervalSeconds)
    {
        Enabled = enabled;
        BurstTicks = Math.Clamp(burstTicks, 1, MaximumBurstTicks);
        IntervalSeconds = Math.Max(MinimumIntervalSeconds, intervalSeconds);
        Restart();
    }

    /// <summary>Advances the schedule by one tick of <paramref name="elapsedSeconds"/> and says
    /// what the caller does with it.</summary>
    public DutyStep OnTick(double elapsedSeconds)
    {
        if (!Enabled)
        {
            return DutyStep.Idle;
        }

        if (!InBurst)
        {
            idleSeconds += elapsedSeconds;
            if (idleSeconds < IntervalSeconds)
            {
                return DutyStep.Idle;
            }

            InBurst = true;
            return DutyStep.Start;
        }

        if (!warm)
        {
            warm = true;
            return DutyStep.WarmUp;
        }

        if (++burstTicksElapsed < BurstTicks)
        {
            return DutyStep.Sample;
        }

        Restart();
        return DutyStep.LastSample;
    }

    /// <summary>Back to idle, with the interval counted from now.</summary>
    private void Restart()
    {
        InBurst = false;
        idleSeconds = 0;
        burstTicksElapsed = 0;
        warm = false;
    }
}

/// <summary>What one tick of a <see cref="DutyCycle"/> asks of whoever owns the work.</summary>
internal enum DutyStep
{
    /// <summary>Nothing: the cycle is off, or still waiting out its interval.</summary>
    Idle,

    /// <summary>The interval has passed and a burst starts on this tick: whatever the burst needs
    /// switched on gets switched on now.</summary>
    Start,

    /// <summary>The tick after the start. What the caller reads on it describes the tick the burst
    /// started in, which was only partly covered, so it is not a sample.</summary>
    WarmUp,

    /// <summary>A tick of the burst to read a sample on.</summary>
    Sample,

    /// <summary>The burst's last sample, read the same way as any other. The burst is over by the
    /// time this is returned: the cycle is idle again and <see cref="DutyCycle.InBurst"/> is
    /// false.</summary>
    LastSample,
}
