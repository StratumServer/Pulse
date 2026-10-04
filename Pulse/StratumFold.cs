using System.Diagnostics;

namespace Pulse;

/// <summary>What one burst added to one behavior series: seconds in one entity behavior's tick.</summary>
/// <remarks>Every field is a label value exactly as it goes on the wire, which is why
/// <see cref="ThreadSafe"/> is the text <c>true</c> or <c>false</c> and not a bool: the aggregator
/// stringifies a bool as <c>True</c>.</remarks>
internal sealed record BehaviorDelta(string Category, string Behavior, string ThreadSafe, string Modid, double Seconds);

/// <summary>What one burst added to one AI task series: seconds in one task's
/// <c>ContinueExecute</c>.</summary>
internal sealed record TaskDelta(string Category, string Task, double Seconds);

/// <summary>What one burst added to one entity type's series: seconds in the whole tick of its
/// entities, and how many of those ticks ran. One call is one entity tick, so the seconds per
/// entity tick is one division.</summary>
internal sealed record EntityDelta(string Type, double Seconds, long Calls);

/// <summary>What one burst added to every series, and which families spilled into their
/// <c>other</c> series for the first time while it did.</summary>
internal sealed record StratumBurst(
    IReadOnlyList<BehaviorDelta> Behaviors,
    IReadOnlyList<TaskDelta> Tasks,
    IReadOnlyList<EntityDelta> Entities,
    IReadOnlyList<StratumFamily> NewlyOverflowed);

/// <summary>Turns the accumulator's totals at the two ends of a burst into what the burst added to
/// each series: seconds, plus the number of ticks run for entity types.</summary>
/// <remarks>Everything here is arithmetic over two lists, so a burst is drivable from a unit test
/// without a server or a Stratum. The accumulator's totals are cumulative, which is why a burst is
/// a difference of two snapshots and never a read of one.
/// <para>A key that is not in the first snapshot appeared during the burst, and everything it
/// holds was added in it: Stratum empties its accumulator when the last reader lets go, so a burst
/// usually opens on nothing and this is every key. A key that is not in the second says nothing. A total that went down was reset
/// in between, so what it holds now is what the burst added. A key the burst added nothing to is not
/// a series yet: it takes none of the places the cap hands out, and an instrument nothing has
/// recorded into is not served.</para>
/// <para>The labels are bounded by registries rather than by load, but a pathological mod list can
/// still push them out, so each family keeps at most <c>cap</c> series. The first label sets to
/// appear get their own and keep it for the life of the server; everything after is added to one
/// series per family whose labels all read <c>other</c>. Nothing is ever evicted or re-ranked: a
/// series that moved in and out of <c>other</c> would make <c>other</c> go down, which Prometheus
/// reads as a counter reset. When a burst brings several new label sets at once, as the first one
/// does, the heaviest are admitted first, so what ends up lumped together is the cheap tail.</para>
/// <para>The <c>modid</c> of a behavior is looked up through the same table attribution uses,
/// every burst: it is a function of the behavior name, but a name only that table learns late
/// (from an entity spawned mid-burst) changes owner once, from <c>unattributed</c> to its mod, and
/// pinning the first answer would keep it wrong for the rest of the run. The old series simply stops
/// growing. The lookup is made on the main thread, which the table requires.</para>
/// <para>Main thread only, once per burst.</para></remarks>
/// <param name="owner">Which mod ships a behavior, by the name the accumulator records it under:
/// <see cref="ModOwners.Owner"/>, shared with attribution.</param>
/// <param name="cap">How many series each family keeps before the rest is lumped together.</param>
internal sealed class StratumFold(Func<string, string?> owner, int cap = StratumFold.SeriesCap)
{
    /// <summary>How many series of each family get a label set of their own.</summary>
    internal const int SeriesCap = 100;

    /// <summary>The value of every label of a family's overflow series: the word the entity gauge
    /// already uses for its own remainder.</summary>
    private const string Other = EntityBreakdown.OtherCode;

    private readonly StratumKeyParser parser = new();

    /// <summary>The first snapshot of the burst being folded, by key. Kept between bursts only to
    /// reuse its storage.</summary>
    private readonly Dictionary<string, Total> startTotals = [];

    /// <summary>The keys that have a series of their own, by family. Only ever added to.</summary>
    private readonly Dictionary<StratumFamily, HashSet<StratumKey>> admitted =
        Enum.GetValues<StratumFamily>().ToDictionary(family => family, _ => new HashSet<StratumKey>());

    /// <summary>The families that have spilled into their overflow series at least once.</summary>
    private readonly HashSet<StratumFamily> overflowed = [];

    /// <summary>Folds one burst: <paramref name="start"/> is the accumulator's totals when the burst
    /// began and <paramref name="end"/> its totals when it ended.</summary>
    /// <remarks>Two lists, not one read twice: a snapshot clears the list it fills.</remarks>
    public StratumBurst Fold(
        IReadOnlyList<(string Key, long Ticks, long Calls)> start,
        IReadOnlyList<(string Key, long Ticks, long Calls)> end)
    {
        (List<KeyValuePair<StratumKey, Total>> kept, Dictionary<StratumFamily, Total> spill) = Admit(Deltas(start, end));

        List<BehaviorDelta> behaviors = [];
        List<TaskDelta> tasks = [];
        List<EntityDelta> entities = [];
        foreach ((StratumKey key, Total total) in kept)
        {
            double seconds = Seconds(total.Ticks);
            switch (key.Family)
            {
                case StratumFamily.Behavior:
                    behaviors.Add(new BehaviorDelta(
                        key.Category,
                        key.Name,
                        key.ThreadSafe ? "true" : "false",
                        owner(key.Name) ?? TickAttribution.Unattributed,
                        seconds));
                    break;
                case StratumFamily.AiTask:
                    tasks.Add(new TaskDelta(key.Category, key.Name, seconds));
                    break;
                default:
                    entities.Add(new EntityDelta(key.Name, seconds, total.Calls));
                    break;
            }
        }

        List<StratumFamily> capped = [];
        foreach ((StratumFamily family, Total total) in spill)
        {
            double seconds = Seconds(total.Ticks);
            switch (family)
            {
                case StratumFamily.Behavior:
                    behaviors.Add(new BehaviorDelta(Other, Other, Other, Other, seconds));
                    break;
                case StratumFamily.AiTask:
                    tasks.Add(new TaskDelta(Other, Other, seconds));
                    break;
                default:
                    entities.Add(new EntityDelta(Other, seconds, total.Calls));
                    break;
            }

            if (overflowed.Add(family))
            {
                capped.Add(family);
            }
        }

        return new StratumBurst(behaviors, tasks, entities, capped);
    }

    /// <summary>Splits the burst's deltas into the ones that land on a series of their own and what
    /// spills into each family's overflow series.</summary>
    private (List<KeyValuePair<StratumKey, Total>> Kept, Dictionary<StratumFamily, Total> Spill) Admit(
        Dictionary<StratumKey, Total> deltas)
    {
        List<KeyValuePair<StratumKey, Total>> kept = [];
        List<KeyValuePair<StratumKey, Total>> fresh = [];
        foreach (KeyValuePair<StratumKey, Total> entry in deltas)
        {
            if (admitted[entry.Key.Family].Contains(entry.Key))
            {
                kept.Add(entry);
            }
            else
            {
                fresh.Add(entry);
            }
        }

        // Ties break on the labels so that the same burst admits the same keys on every run.
        IEnumerable<KeyValuePair<StratumKey, Total>> heaviestFirst = fresh
            .OrderByDescending(candidate => candidate.Value.Ticks)
            .ThenBy(candidate => candidate.Key.Category, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Key.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Key.ThreadSafe);

        Dictionary<StratumFamily, Total> spill = [];
        foreach (KeyValuePair<StratumKey, Total> entry in heaviestFirst)
        {
            HashSet<StratumKey> room = admitted[entry.Key.Family];
            if (room.Count < cap)
            {
                room.Add(entry.Key);
                kept.Add(entry);
            }
            else
            {
                spill[entry.Key.Family] = spill.GetValueOrDefault(entry.Key.Family) + entry.Value;
            }
        }

        return (kept, spill);
    }

    /// <summary>What the burst added to each series, summed over the keys that fold into it.</summary>
    private Dictionary<StratumKey, Total> Deltas(
        IReadOnlyList<(string Key, long Ticks, long Calls)> start,
        IReadOnlyList<(string Key, long Ticks, long Calls)> end)
    {
        startTotals.Clear();
        foreach ((string key, long ticks, long calls) in start)
        {
            startTotals[key] = new Total(ticks, calls);
        }

        Dictionary<StratumKey, Total> deltas = [];
        foreach ((string key, long ticks, long calls) in end)
        {
            if (parser.Parse(key) is not { } series)
            {
                continue;
            }

            startTotals.TryGetValue(key, out Total before);
            Total added = new(Delta(before.Ticks, ticks), Delta(before.Calls, calls));
            if (added != default)
            {
                deltas[series] = deltas.GetValueOrDefault(series) + added;
            }
        }

        return deltas;
    }

    /// <summary>What a cumulative total gained between two readings. One that went down was reset
    /// in between, so all of it was gained since; one that is negative is nonsense, and must not run
    /// a counter backwards.</summary>
    private static long Delta(long before, long after) => Math.Max(0, after >= before ? after - before : after);

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    private readonly record struct Total(long Ticks, long Calls)
    {
        public static Total operator +(Total a, Total b) => new(a.Ticks + b.Ticks, a.Calls + b.Calls);
    }
}
