namespace Pulse;

/// <summary>Turns a stream of entity codes into the gauge values for the busiest few, everything
/// else lumped together, and a zero for whatever has just fallen off the list.</summary>
/// <remarks>The label set here is the only one in Pulse whose values Pulse does not choose: a
/// modded server can load hundreds of entity types, and a series per type is cardinality no
/// dashboard wants. Hence a top ten and an "other" bucket.</remarks>
internal sealed class EntityBreakdown(int limit)
{
    public const string OtherCode = "other";

    /// <summary>The codes reported with a real count last time round.</summary>
    /// <remarks>This is the whole point of the class. A gauge series keeps whatever value it was
    /// last given, so a code that drops out of the top ten would freeze at the count it had when
    /// it left and read as a live number forever. Publishing an explicit zero once retires it. The
    /// zeroed codes are deliberately not carried forward: once a series reads zero, repeating that
    /// zero every refresh only grows the set of dead series Pulse keeps writing.</remarks>
    private string[] published = [];

    /// <summary>Counts the codes and returns what to publish: the busiest <c>limit</c> of them,
    /// the total of everything else under <see cref="OtherCode"/>, and a zero for every code that
    /// was published last time and is not published now.</summary>
    /// <remarks>Ties break on the code itself so two types with the same count do not swap places
    /// between refreshes, which would flap two series against each other for no reason.</remarks>
    public IReadOnlyList<KeyValuePair<string, long>> Refresh(IEnumerable<string> codes)
    {
        Dictionary<string, long> counts = [];
        long total = 0;
        foreach (string code in codes)
        {
            counts.TryGetValue(code, out long seen);
            counts[code] = seen + 1;
            total++;
        }

        List<KeyValuePair<string, long>> values =
        [
            .. counts
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(limit),
        ];

        long top = 0;
        foreach (KeyValuePair<string, long> entry in values)
        {
            top += entry.Value;
        }

        values.Add(new KeyValuePair<string, long>(OtherCode, total - top));

        HashSet<string> current = [.. values.Select(entry => entry.Key)];
        foreach (string code in published.Where(code => !current.Contains(code)))
        {
            values.Add(new KeyValuePair<string, long>(code, 0));
        }

        published = [.. current];
        return values;
    }
}
