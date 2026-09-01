namespace Pulse;

/// <summary>One reading of the engine's own statistics: the last completed two-second bucket, plus
/// the two running UDP byte totals and the current connection queue depth.</summary>
/// <remarks>Plain numbers, no engine types, so the arithmetic below is testable without a server
/// and the record survives the probe being unavailable. The window figures cover the bucket the
/// engine has finished with, which is why they are gauges and not counters: the engine zeroes the
/// bucket on rotation, so there is nothing monotonic to expose.</remarks>
internal sealed record EngineSample(
    double TickBusySeconds,
    long TcpPackets,
    long TcpBytes,
    long UdpPackets,
    long UdpBytes,
    int ConnectionQueue,
    long UdpSentBytes,
    long UdpReceivedBytes)
{
    /// <summary>Average busy time of one tick in the bucket, in seconds. The engine accumulates
    /// whole milliseconds, hence the divisor.</summary>
    /// <remarks>A bucket with no ticks in it reads 0 rather than dividing by zero. That happens
    /// on the very first sample and whenever a suspend swallowed a whole two-second window.</remarks>
    public static double BusySeconds(long tickTimeTotalMs, long ticksTotal)
        => ticksTotal > 0 ? tickTimeTotalMs / (double)ticksTotal / 1000.0 : 0;
}
