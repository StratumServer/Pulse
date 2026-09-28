namespace Pulse;

public enum MetricKind
{
    Counter,
    Gauge,
    Histogram,
}

/// <summary>One series of a metric family as of a single scrape. The histogram fields stay empty
/// for the other two kinds.</summary>
/// <remarks>A family with tagged measurements yields one sample per distinct tag set, all
/// carrying the same <see cref="Name"/>, so the writer emits HELP and TYPE once for the lot.</remarks>
public sealed record MetricSample(string Name, MetricKind Kind, string Help, double Value)
{
    /// <summary>The instrument's OpenTelemetry unit (<c>By</c>, <c>s</c>, <c>{tick}</c>, empty for
    /// an unset unit), carried through so the writer can derive the Prometheus name the same way
    /// OTLP does. Not itself written to the wire: Prometheus text exposition has no unit line.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>Label pairs, sorted by key, values already stringified. Empty for an untagged
    /// series, which renders without braces.</summary>
    public KeyValuePair<string, string>[] Labels { get; init; } = [];

    /// <summary>Upper bucket bounds, ascending, without the implicit +Inf bucket.</summary>
    public double[] Bounds { get; init; } = [];

    /// <summary>Observations per bucket, one entry per bound, NOT cumulative. The writer
    /// accumulates them; the +Inf bucket is <see cref="Count"/>.</summary>
    public long[] Buckets { get; init; } = [];

    public double Sum { get; init; }

    public long Count { get; init; }
}
