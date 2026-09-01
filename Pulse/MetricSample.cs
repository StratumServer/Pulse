namespace Pulse;

public enum MetricKind
{
    Counter,
    Gauge,
    Histogram,
}

/// <summary>One metric family as of a single scrape. The histogram fields stay empty for the
/// other two kinds.</summary>
public sealed record MetricSample(string Name, MetricKind Kind, string Help, double Value)
{
    /// <summary>Upper bucket bounds, ascending, without the implicit +Inf bucket.</summary>
    public double[] Bounds { get; init; } = [];

    /// <summary>Observations per bucket, one entry per bound, NOT cumulative. The writer
    /// accumulates them; the +Inf bucket is <see cref="Count"/>.</summary>
    public long[] Buckets { get; init; } = [];

    public double Sum { get; init; }

    public long Count { get; init; }
}
