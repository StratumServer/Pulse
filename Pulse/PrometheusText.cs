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
                 samples.GroupBy(sample => MetricName(sample.Name, sample.Kind, sample.Unit)))
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

    /// <summary>The exposition spelling of an instrument name and unit: prometheus/otlptranslator
    /// v1.0.0's rules for a namespace-less MetricNamer with metric suffixes on and legacy names
    /// escaped, the same translation OTLP collectors, Grafana and opentelemetry-dotnet's own
    /// Prometheus exporter apply. A name Pulse serves here therefore matches the name the same
    /// instrument gets over any of those paths, instead of only resembling it.</summary>
    /// <remarks>Dots and underscores both end a word, so <c>dotnet.gc.heap.total_allocated</c>
    /// tokenises as dotnet/gc/heap/total/allocated. The unit's Prometheus word becomes a trailing
    /// token unless the name already ends in it, which is what keeps
    /// <c>pulse_network_sent_bytes_total</c>, already spelled out, from growing a second "_bytes".
    /// A monotonic counter then moves (or adds) a trailing "total" and a gauge measuring the
    /// dimensionless unit "1" moves (or adds) a trailing "ratio": since "total" is removed before
    /// it is re-added, a counter named ...total_allocated (unit By) becomes
    /// ..._allocated_bytes_total rather than the doubled ..._total_allocated_bytes_total. Pulse's
    /// own instruments were named to already read this way; only the runtime's dotted names, and
    /// the three families units used to leave misnamed, actually move. See PrometheusTextTests for
    /// the full instrument table this is pinned against.</remarks>
    public static string MetricName(string name, MetricKind kind, string unit)
    {
        List<string> tokens = Tokenize(name);
        (string mainUnit, string perUnit) = UnitWords(unit);
        AddIfMissing(tokens, mainUnit);
        AddIfMissing(tokens, perUnit);

        if (kind == MetricKind.Counter)
        {
            MoveToEnd(tokens, "total");
        }
        else if (kind == MetricKind.Gauge && unit == "1")
        {
            MoveToEnd(tokens, "ratio");
        }

        string joined = string.Join('_', tokens);
        return joined.Length > 0 && char.IsAsciiDigit(joined[0]) ? "_" + joined : joined;
    }

    /// <summary>OTLP's UCUM-derived base units, mapped to the Prometheus word each becomes. Ported
    /// from prometheus/otlptranslator v1.0.0's unitMap: the spec-correct mapping, which is that
    /// library's default (Pulse takes no dependency on its legacy TiBy/kBy aliases). An
    /// unrecognised unit passes through as its own word, same as upstream.</summary>
    private static readonly Dictionary<string, string> MainUnits = new()
    {
        ["d"] = "days", ["h"] = "hours", ["min"] = "minutes", ["s"] = "seconds",
        ["ms"] = "milliseconds", ["us"] = "microseconds", ["ns"] = "nanoseconds",
        ["By"] = "bytes", ["KiBy"] = "kibibytes", ["MiBy"] = "mebibytes", ["GiBy"] = "gibibytes",
        ["TiBy"] = "tebibytes", ["kBy"] = "kilobytes", ["KBy"] = "kilobytes", ["MBy"] = "megabytes",
        ["GBy"] = "gigabytes", ["TBy"] = "terabytes",
        ["m"] = "meters", ["V"] = "volts", ["A"] = "amperes", ["J"] = "joules", ["W"] = "watts",
        ["g"] = "grams", ["Cel"] = "celsius", ["Hz"] = "hertz", ["1"] = "", ["%"] = "percent",
    };

    /// <summary>The unit after a '/', mapped to the singular word Prometheus naming convention
    /// spells a rate with: "requests/s" becomes "requests_per_second", not "per_seconds".</summary>
    private static readonly Dictionary<string, string> PerUnits = new()
    {
        ["s"] = "second", ["m"] = "minute", ["h"] = "hour", ["d"] = "day", ["w"] = "week",
        ["mo"] = "month", ["y"] = "year",
    };

    private static readonly char[] BraceChars = ['{', '}'];

    /// <summary>Splits a name into the words prometheus/otlptranslator finds in it: runs of ASCII
    /// letters, digits and colons, with every dot, underscore or other character acting as a
    /// separator that does not itself survive into a token.</summary>
    private static List<string> Tokenize(string name)
    {
        List<string> tokens = [];
        int start = -1;
        for (int i = 0; i < name.Length; i++)
        {
            bool valid = char.IsAsciiLetterOrDigit(name[i]) || name[i] == ':';
            if (valid && start < 0)
            {
                start = i;
            }
            else if (!valid && start >= 0)
            {
                tokens.Add(name[start..i]);
                start = -1;
            }
        }

        if (start >= 0)
        {
            tokens.Add(name[start..]);
        }

        return tokens;
    }

    /// <summary>The unit's main and "per" components, each mapped to its Prometheus word. Ported
    /// from the UnitNamer half of prometheus/otlptranslator v1.0.0: split at the first '/', and a
    /// side still wrapped in its OTLP "{annotation}" braces (every one of Pulse's own instruments
    /// but the real units "s" and "By") contributes nothing, since an annotation names no
    /// Prometheus-mappable unit at all.</summary>
    private static (string Main, string Per) UnitWords(string unit)
    {
        string[] parts = unit.Split('/', 2);
        string main = UnitWord(parts[0], MainUnits);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return (main, string.Empty);
        }

        string per = UnitWord(parts[1], PerUnits);
        return (main, per.Length > 0 ? "per_" + per : string.Empty);
    }

    private static string UnitWord(string part, IReadOnlyDictionary<string, string> words)
    {
        string trimmed = part.Trim();
        return trimmed.Length > 0 && trimmed.IndexOfAny(BraceChars) < 0
            ? words.GetValueOrDefault(trimmed, trimmed)
            : string.Empty;
    }

    /// <summary>Appends <paramref name="word"/> unless it is empty or already one of the tokens,
    /// which is the mechanical form of "the unit is already spelled out in the name".</summary>
    private static void AddIfMissing(List<string> tokens, string word)
    {
        if (word.Length > 0 && !tokens.Contains(word))
        {
            tokens.Add(word);
        }
    }

    /// <summary>Removes every existing occurrence of <paramref name="word"/> before appending one,
    /// so a name that already ends in it is untouched and one that spells it out mid-name gets it
    /// moved to the end instead of duplicated.</summary>
    private static void MoveToEnd(List<string> tokens, string word)
    {
        tokens.RemoveAll(t => t == word);
        tokens.Add(word);
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
