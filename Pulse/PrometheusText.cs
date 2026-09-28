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

    /// <summary>The exposition spelling of an instrument name and unit: a port of Prometheus's
    /// otlptranslator v1.0.0 (a namespace-less MetricNamer with metric suffixes on and legacy
    /// names escaped), the library Prometheus's own OTLP receiver, Mimir and Grafana Cloud use to
    /// turn an OTLP instrument into a Prometheus name. A name Pulse serves here therefore matches
    /// the name the same instrument gets over any of those paths, instead of only resembling
    /// it.</summary>
    /// <remarks>Dots and underscores both end a word, so <c>dotnet.gc.heap.total_allocated</c>
    /// tokenises as dotnet/gc/heap/total/allocated. The unit's Prometheus word becomes a trailing
    /// token unless the name already contains it as a word, which is what keeps
    /// <c>pulse_network_sent_bytes_total</c>, which already has "bytes" as one of its words, from
    /// growing a second one. A monotonic counter then moves (or adds) a trailing "total" and a
    /// gauge measuring the dimensionless unit "1" moves (or adds) a trailing "ratio": since "total"
    /// is removed before it is re-added, a counter named ...total_allocated (unit By) becomes
    /// ..._allocated_bytes_total rather than the doubled ..._total_allocated_bytes_total. Pulse's
    /// own instruments were named to already read this way; only the runtime's dotted names, and
    /// the three families units used to leave misnamed, actually move. The one shape where this
    /// still diverges from OTLP: an UpDownCounter measuring unit "1" renders as a gauge here and
    /// picks up "_ratio", but OTLP carries it as a non-monotonic sum, a shape the ratio rule
    /// excludes, so the two paths would disagree on that one name (no instrument Pulse publishes
    /// does this today). See PrometheusTextTests for the full instrument table this is pinned
    /// against.</remarks>
    public static string MetricName(string name, MetricKind kind, string unit)
    {
        List<string> tokens = Tokenize(name);
        (string mainUnit, string perUnit) = BuildUnitSuffixes(unit);
        AddUnitTokens(tokens, CleanUpUnit(mainUnit), CleanUpUnit(perUnit));

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
    /// verbatim from prometheus/otlptranslator v1.0.0's unitMap: that version predates the
    /// library's later TiBy/kBy correction, so TiBy still means tibibytes here and there is no
    /// lowercase kBy entry (only KBy). An unrecognised unit passes through as its own word, same
    /// as upstream.</summary>
    private static readonly Dictionary<string, string> MainUnits = new()
    {
        ["d"] = "days", ["h"] = "hours", ["min"] = "minutes", ["s"] = "seconds",
        ["ms"] = "milliseconds", ["us"] = "microseconds", ["ns"] = "nanoseconds",
        ["By"] = "bytes", ["KiBy"] = "kibibytes", ["MiBy"] = "mebibytes", ["GiBy"] = "gibibytes",
        ["TiBy"] = "tibibytes", ["KBy"] = "kilobytes", ["MBy"] = "megabytes",
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

    /// <summary>A valid Prometheus metric name character: ASCII letter, digit or colon. Ported
    /// from prometheus/otlptranslator's isValidCompliantMetricChar; notably this excludes
    /// underscore, which is why <see cref="Tokenize"/> below splits a name on it too.</summary>
    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == ':';

    /// <summary>Splits a name into the words prometheus/otlptranslator finds in it: runs of
    /// <see cref="IsNameChar"/> characters, with every dot, underscore or other character acting
    /// as a separator that does not itself survive into a token.</summary>
    private static List<string> Tokenize(string name)
    {
        List<string> tokens = [];
        int start = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (IsNameChar(name[i]) && start < 0)
            {
                start = i;
            }
            else if (!IsNameChar(name[i]) && start >= 0)
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

    /// <summary>The unit's raw main and "per" components, each mapped to its Prometheus word but
    /// not yet cleaned up (the caller applies <see cref="CleanUpUnit"/>, matching upstream's own
    /// two-step buildUnitSuffixes-then-cleanUpUnit split). Ported from the UnitNamer half of
    /// prometheus/otlptranslator v1.0.0: split at the first '/' only, so a second slash rides
    /// along inside the "per" half rather than starting a third component, and a side still
    /// wrapped in its OTLP "{annotation}" braces contributes nothing, since an annotation names no
    /// Prometheus-mappable unit at all.</summary>
    private static (string Main, string Per) BuildUnitSuffixes(string unit)
    {
        string[] parts = unit.Split('/', 2);
        string main = RawUnitWord(parts[0], MainUnits);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return (main, string.Empty);
        }

        string per = RawUnitWord(parts[1], PerUnits);
        return (main, per.Length > 0 ? "per_" + per : string.Empty);
    }

    private static string RawUnitWord(string part, IReadOnlyDictionary<string, string> words)
    {
        string trimmed = part.Trim();
        return trimmed.Length > 0 && trimmed.IndexOfAny(BraceChars) < 0
            ? words.GetValueOrDefault(trimmed, trimmed)
            : string.Empty;
    }

    /// <summary>Makes a raw unit word safe to join into a metric name: every character that is not
    /// a valid name character (see <see cref="IsNameChar"/>), underscore included, becomes an
    /// underscore, consecutive underscores collapse to one, and a single leading underscore is
    /// stripped (a trailing one is not: prometheus/otlptranslator does not strip it either, and a
    /// unit like "[degF]" is the reason, cleaning up to "degF_" with the trailing underscore
    /// intact). Ported verbatim from cleanUpUnit; without it, a unit built from an unrecognised
    /// word (a space, a slash left over from a "per" split, a non-ASCII sign) reaches the name raw
    /// and produces a line the exposition format cannot parse.</summary>
    private static string CleanUpUnit(string unit)
    {
        char[] mapped = new char[unit.Length];
        for (int i = 0; i < unit.Length; i++)
        {
            mapped[i] = IsNameChar(unit[i]) ? unit[i] : '_';
        }

        StringBuilder collapsed = new(mapped.Length);
        bool previousWasUnderscore = false;
        foreach (char c in mapped)
        {
            if (c == '_')
            {
                if (previousWasUnderscore)
                {
                    continue;
                }

                previousWasUnderscore = true;
            }
            else
            {
                previousWasUnderscore = false;
            }

            collapsed.Append(c);
        }

        return collapsed.Length > 0 && collapsed[0] == '_' ? collapsed.ToString(1, collapsed.Length - 1) : collapsed.ToString();
    }

    /// <summary>Appends the cleaned main and "per" unit words to <paramref name="tokens"/>, unless
    /// a word already ends the name with the same spelling. Ported verbatim from
    /// prometheus/otlptranslator's addUnitTokens: both words are checked against the name's
    /// original tokens before either is appended (so a name whose main and per word happen to
    /// match, such as unit "per_second/s", still gets both), a "per" word that is only the bare
    /// "per_" prefix (an unrecognised or blank per-unit collapses to exactly this after cleanup)
    /// is dropped entirely rather than appended, and the main word's own trailing underscore is
    /// trimmed when a "per" word follows it, so the two do not end up separated by a double
    /// underscore.</summary>
    private static void AddUnitTokens(List<string> tokens, string mainUnit, string perUnit)
    {
        if (tokens.Contains(mainUnit))
        {
            mainUnit = string.Empty;
        }

        if (perUnit == "per_")
        {
            perUnit = string.Empty;
        }
        else
        {
            perUnit = perUnit.TrimEnd('_');
            if (tokens.Contains(perUnit))
            {
                perUnit = string.Empty;
            }
        }

        if (perUnit.Length > 0)
        {
            mainUnit = mainUnit.TrimEnd('_');
        }

        if (mainUnit.Length > 0)
        {
            tokens.Add(mainUnit);
        }

        if (perUnit.Length > 0)
        {
            tokens.Add(perUnit);
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
