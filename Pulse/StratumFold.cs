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
/// holds was added in it. Stratum empties its accumulator when the last reader lets go, so the first
/// snapshot is meant to be taken on the burst's warm-up tick, once recording is on: it then holds
/// nearly every key, and what is missing from it is a type or a task that did not run in that tick.
/// A key that is not in the second snapshot says nothing. A key with a total that went down, ticks
/// or calls, was reset in between, and is taken as the second snapshot holds it, both totals, so
/// that what it holds now is what the burst added. A key the burst added nothing to is not a series
/// yet: it takes none of the places the cap hands out, and an instrument nothing has recorded into
/// is not served.</para>
/// <para>The labels are bounded by registries rather than by load, but a pathological mod list can
/// still push them out, so each family keeps at most <c>cap</c> series. The first label sets to
/// appear get their own and keep it for the life of the server; the rest are added to an overflow
/// series whose name label (<c>behavior</c>, <c>task</c> or <c>type</c>, and the <c>modid</c> that
/// goes with a behavior's name) reads <c>other</c>. The category and the thread stay on it: the
/// thread separates CPU time summed across the physics threads from main-thread time, and a panel
/// that filters on it must not lose what overflowed. So a family has one overflow series for each
/// category and thread it spilled in, which with Stratum's three categories is at most four for
/// behaviors (main-thread behaviors are timed for players only), three for tasks and one for entity
/// types. Nothing is ever evicted or re-ranked: a series that moved in and out of <c>other</c>
/// would make <c>other</c> go down, which Prometheus reads as a counter reset. When a burst brings
/// several new label sets at once, as the first one does, the heaviest are admitted first, so what
/// ends up lumped together is the cheap tail.</para>
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

    /// <summary>The value of the name label of an overflow series: the word the entity gauge
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
        (List<KeyValuePair<StratumKey, Total>> kept, Dictionary<StratumKey, Total> spill) = Admit(Deltas(start, end));

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
                        ThreadLabel(key.ThreadSafe),
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
        foreach ((StratumKey lump, Total total) in spill)
        {
            double seconds = Seconds(total.Ticks);
            switch (lump.Family)
            {
                case StratumFamily.Behavior:
                    // A name that stands for many behaviors has no owner to look up: the modid
                    // follows the name.
                    behaviors.Add(new BehaviorDelta(
                        lump.Category, lump.Name, ThreadLabel(lump.ThreadSafe), Other, seconds));
                    break;
                case StratumFamily.AiTask:
                    tasks.Add(new TaskDelta(lump.Category, lump.Name, seconds));
                    break;
                default:
                    entities.Add(new EntityDelta(lump.Name, seconds, total.Calls));
                    break;
            }

            if (overflowed.Add(lump.Family))
            {
                capped.Add(lump.Family);
            }
        }

        return new StratumBurst(behaviors, tasks, entities, capped);
    }

    /// <summary>Splits the burst's deltas into the ones that land on a series of their own and what
    /// spills into the overflow series, keyed by the key it came from with its name replaced.</summary>
    private (List<KeyValuePair<StratumKey, Total>> Kept, Dictionary<StratumKey, Total> Spill) Admit(
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

        Dictionary<StratumKey, Total> spill = [];
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
                // Only the name is lumped. What the category and the thread say about the seconds
                // is not something to lose to the cap.
                StratumKey lump = entry.Key with { Name = Other };
                spill[lump] = spill.GetValueOrDefault(lump) + entry.Value;
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
            Total added = Added(before, new Total(ticks, calls));
            if (added != default)
            {
                deltas[series] = deltas.GetValueOrDefault(series) + added;
            }
        }

        return deltas;
    }

    /// <summary>What a key's two totals gained between two readings. If either went down the key was
    /// reset in between, so both are taken as they stand: all of what they hold was gained since,
    /// and an end value paired with a delta would be seconds and a count that are not each other's.
    /// A total that is negative is nonsense, and must not run a counter backwards.</summary>
    private static Total Added(Total before, Total after)
        => after.Ticks >= before.Ticks && after.Calls >= before.Calls
            ? new Total(after.Ticks - before.Ticks, after.Calls - before.Calls)
            : new Total(Math.Max(0, after.Ticks), Math.Max(0, after.Calls));

    private static string ThreadLabel(bool threadSafe) => threadSafe ? "true" : "false";

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    private readonly record struct Total(long Ticks, long Calls)
    {
        public static Total operator +(Total a, Total b) => new(a.Ticks + b.Ticks, a.Calls + b.Calls);
    }
}
