using System.Globalization;
using System.Text;

namespace Pulse;

/// <summary>Renders samples in the Prometheus text exposition format (version 0.0.4).</summary>
public static class PrometheusText
{
    public static string Render(IReadOnlyList<MetricSample> samples)
    {
        StringBuilder sb = new();

        // Grouped by family, in the order the families first appear. HELP and TYPE belong to the
        // family, not to the series, and the exposition format wants every series of a family
        // together in one block. The series of one family do not necessarily arrive together:
        // a labelled family opens a new series whenever a tag set is measured for the first time,
        // which for the entity breakdown happens minutes into a server's life.
        foreach (IGrouping<string, MetricSample> family in
                 samples.GroupBy(sample => MetricName(sample.Name, sample.Kind)))
        {
            MetricSample first = family.First();

            // ponytail: HELP text is not escaped. Help strings come from instrument
            // descriptions written in this assembly and in the runtime's own meter, none of
            // which carry a backslash or a newline. Escape it the day help text is user input.
            sb.Append("# HELP ").Append(family.Key).Append(' ').Append(first.Help).Append('\n');
            sb.Append("# TYPE ").Append(family.Key).Append(' ').Append(TypeName(first.Kind)).Append('\n');

            foreach (MetricSample sample in family)
            {
                Append(sb, sample, family.Key);
            }
        }

        return sb.ToString();
    }

    /// <summary>The exposition spelling of an instrument name: underscores for dots, and the
    /// _total suffix a monotonic counter is expected to carry.</summary>
    /// <remarks>The runtime's built-in meter uses dotted OpenTelemetry names, so
    /// <c>dotnet.gc.collections</c> becomes <c>dotnet_gc_collections_total</c>. Pulse's own names
    /// already comply and pass through untouched.</remarks>
    public static string MetricName(string name, MetricKind kind)
    {
        string mapped = name.Replace('.', '_');
        return kind == MetricKind.Counter && !mapped.EndsWith("_total", StringComparison.Ordinal)
            ? mapped + "_total"
            : mapped;
    }

    /// <summary>Label names take the same character set as metric names, so a tag key like
    /// <c>gc.heap.generation</c> renders as <c>gc_heap_generation</c>.</summary>
    private static string LabelName(string key) => key.Replace('.', '_');

    /// <summary>The three escapes the exposition format defines for a label value.</summary>
    // ponytail: three passes over the string, once per label per scrape. Hand-roll a single pass
    // the day a scrape carries enough labels for it to show up in a profile.
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>The brace-wrapped label list, or an empty string when there is nothing to put in
    /// it. <paramref name="le"/> is the histogram bucket bound, appended after the labels.</summary>
    private static string Braces(KeyValuePair<string, string>[] labels, string? le)
    {
        if (labels.Length == 0 && le == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new("{");
        foreach (KeyValuePair<string, string> label in labels)
        {
            sb.Append(LabelName(label.Key)).Append("=\"").Append(Escape(label.Value)).Append("\",");
        }

        if (le != null)
        {
            sb.Append("le=\"").Append(le).Append("\",");
        }

        sb.Length--;
        return sb.Append('}').ToString();
    }

    private static void Append(StringBuilder sb, MetricSample sample, string name)
    {
        if (sample.Kind != MetricKind.Histogram)
        {
            sb.Append(name).Append(Braces(sample.Labels, null)).Append(' ')
              .Append(Number(sample.Value)).Append('\n');
            return;
        }

        long cumulative = 0;
        for (int i = 0; i < sample.Bounds.Length; i++)
        {
            cumulative += sample.Buckets[i];
            sb.Append(name).Append("_bucket").Append(Braces(sample.Labels, Number(sample.Bounds[i])))
              .Append(' ').Append(Number(cumulative)).Append('\n');
        }

        // A histogram with no bucket advice has no bucket lines to write, +Inf included, and
        // degrades to the sum and count every Prometheus parser reads anyway.
        if (sample.Bounds.Length > 0)
        {
            sb.Append(name).Append("_bucket").Append(Braces(sample.Labels, "+Inf"))
              .Append(' ').Append(Number(sample.Count)).Append('\n');
        }

        sb.Append(name).Append("_sum").Append(Braces(sample.Labels, null)).Append(' ')
          .Append(Number(sample.Sum)).Append('\n');
        sb.Append(name).Append("_count").Append(Braces(sample.Labels, null)).Append(' ')
          .Append(Number(sample.Count)).Append('\n');
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
