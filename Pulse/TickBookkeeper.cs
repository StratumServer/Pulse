using System.Diagnostics.Metrics;

namespace Pulse;

/// <summary>The counting, histogram recording and snapshot-cadence accounting <c>OnTick</c> does,
/// pulled out of PulseModSystem so it can be driven by a plain elapsed-seconds value instead of a
/// live Stopwatch and a live server tick.</summary>
/// <remarks>Fed a duration on every tick; PulseModSystem still owns the Stopwatch that produces
/// it. The very first duration is meaningless (there was no previous tick to measure since), so
/// it is neither recorded nor accumulated: this mirrors the original code checking
/// <c>Stopwatch.IsRunning</c> before trusting <c>Elapsed</c>.</remarks>
internal sealed class TickBookkeeper(Counter<long> ticks, Histogram<double> tickSeconds, double snapshotIntervalSeconds)
{
    private bool warm;
    private double sinceSnapshotSeconds;

    /// <summary>Records one tick. Returns true once accumulated time reaches the snapshot
    /// interval, at which point the caller publishes a snapshot; the accumulator resets whether
    /// or not this was the tick that crossed it.</summary>
    public bool OnTick(double elapsedSeconds)
    {
        ticks.Add(1);

        if (warm)
        {
            tickSeconds.Record(elapsedSeconds);
            sinceSnapshotSeconds += elapsedSeconds;
        }

        warm = true;

        if (sinceSnapshotSeconds < snapshotIntervalSeconds)
        {
            return false;
        }

        sinceSnapshotSeconds = 0;
        return true;
    }
}
