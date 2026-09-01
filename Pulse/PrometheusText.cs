using System.Globalization;
using System.Text;

namespace Pulse;

/// <summary>Renders samples in the Prometheus text exposition format (version 0.0.4).</summary>
public static class PrometheusText
{
    public static string Render(IReadOnlyList<MetricSample> samples)
    {
        StringBuilder sb = new();
        foreach (MetricSample sample in samples)
        {
            Append(sb, sample);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, MetricSample sample)
    {
        // ponytail: HELP text is not escaped. Every help string is a literal in this assembly and
        // carries no backslash or newline. Escape it the day help text becomes configurable.
        sb.Append("# HELP ").Append(sample.Name).Append(' ').Append(sample.Help).Append('\n');
        sb.Append("# TYPE ").Append(sample.Name).Append(' ').Append(TypeName(sample.Kind)).Append('\n');

        if (sample.Kind != MetricKind.Histogram)
        {
            sb.Append(sample.Name).Append(' ').Append(Number(sample.Value)).Append('\n');
            return;
        }

        long cumulative = 0;
        for (int i = 0; i < sample.Bounds.Length; i++)
        {
            cumulative += sample.Buckets[i];
            sb.Append(sample.Name).Append("_bucket{le=\"").Append(Number(sample.Bounds[i]))
              .Append("\"} ").Append(Number(cumulative)).Append('\n');
        }

        sb.Append(sample.Name).Append("_bucket{le=\"+Inf\"} ").Append(Number(sample.Count)).Append('\n');
        sb.Append(sample.Name).Append("_sum ").Append(Number(sample.Sum)).Append('\n');
        sb.Append(sample.Name).Append("_count ").Append(Number(sample.Count)).Append('\n');
    }

    private static string TypeName(MetricKind kind) => kind switch
    {
        MetricKind.Counter => "counter",
        MetricKind.Gauge => "gauge",
        _ => "histogram",
    };

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Invariant culture is not decoration: on a French locale the default formatter
    /// writes "0,025", which every Prometheus parser rejects.</summary>
    private static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
