namespace Pulse;

/// <summary>Round trip time across the players online, in seconds.</summary>
internal readonly record struct PingSummary(double AverageSeconds, double MaxSeconds)
{
    /// <summary>Averages and maxes the pings that are actual numbers.</summary>
    /// <remarks>The engine reports NaN for a player it has no connection for, which happens
    /// routinely between a disconnect and the client leaving AllOnlinePlayers. One NaN in the set
    /// would otherwise poison both aggregates for that scrape. An empty world, or one where every
    /// ping is NaN, reports zero rather than nothing: a gauge that vanishes and comes back is
    /// harder to plot than one that sits at zero.</remarks>
    public static PingSummary Of(IEnumerable<float> pings)
    {
        double total = 0;
        double max = 0;
        int counted = 0;

        foreach (float ping in pings)
        {
            if (!float.IsFinite(ping))
            {
                continue;
            }

            total += ping;
            max = Math.Max(max, ping);
            counted++;
        }

        return counted == 0 ? new PingSummary(0, 0) : new PingSummary(total / counted, max);
    }
}
