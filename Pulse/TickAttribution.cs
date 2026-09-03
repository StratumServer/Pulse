using System.Diagnostics;
using Vintagestory.API.Common;

// The game's API declares a Func delegate of its own in Vintagestory.API.Common, so the one this
// file wants gets a name of its own rather than a namespace qualifier on every signature.
using OwnerLookup = System.Func<string, string?>;

namespace Pulse;

/// <summary>The duty cycle and the arithmetic behind per-mod tick attribution: when the engine's
/// frame profiler should be running, and how one profiled tick's mark tree becomes seconds per
/// mod.</summary>
/// <remarks>Knows nothing about meters, the server or the profiler flag itself. It is handed the
/// previous tick's completed tree and says whether the profiler should be on when the current tick
/// ends, which is what makes the whole duty cycle drivable from a unit test.</remarks>
internal sealed class TickAttribution
{
    /// <summary>Everything the engine spends on itself: its own server systems, the time between
    /// ranges nobody marked, and every mark that names no mod.</summary>
    public const string Engine = "engine";

    /// <summary>Marks that do name something, but nothing loaded claims it. A handler on a static
    /// method has no target type at all and lands here, as does a listener registered from a mod's
    /// side library rather than from the assembly its ModSystem lives in.</summary>
    public const string Unattributed = "unattributed";

    /// <summary>Shortest interval between bursts. The duty cycle is the whole reason this is
    /// affordable, so it stays a duty cycle.</summary>
    public const int MinimumIntervalSeconds = 1;

    /// <summary>Longest burst. Ten seconds of profiling at the default tick rate, which is already
    /// far more than tick composition varies over.</summary>
    public const int MaximumBurstTicks = 300;

    /// <summary>The engine's bucket for the throttle sleep, charged in <c>ServerMain.Process</c>
    /// (1.22.7:1553). It is the one root mark that is not work, so it is what busy time is measured
    /// against rather than attributed.</summary>
    private const string SleepMark = "sleep";

    /// <summary>Mark prefixes the engine puts in front of a name that identifies an owner. The
    /// first five come from <c>EventManager.TriggerGameTickDebug</c> (1.22.7:200-264) and carry the
    /// handler target's type name; the last is <c>EntityBehavior.ProfilerName</c> and carries a
    /// behavior code. Every other mark in the tree is the engine's own.</summary>
    private static readonly string[] OwnedPrefixes = ["gmle", "gmlb", "dce", "dcb", "sdcb", "done-behavior-"];

    private readonly Dictionary<string, long> ticksByMod = [];

    /// <summary>Every mod that has appeared in any burst so far, so one that goes quiet publishes a
    /// zero instead of freezing its gauge at the share it had when it stopped.</summary>
    private readonly HashSet<string> seenMods = [];

    private double idleSeconds;
    private int burstTicksElapsed;
    private int sampled;
    private long busyTicks;
    private long dropped;
    private bool warm;

    public TickAttribution(int burstTicks, int intervalSeconds)
    {
        BurstTicks = Math.Clamp(burstTicks, 1, MaximumBurstTicks);
        IntervalSeconds = Math.Max(MinimumIntervalSeconds, intervalSeconds);
    }

    public int BurstTicks { get; }

    public int IntervalSeconds { get; }

    /// <summary>Whether the engine's frame profiler has to be enabled when the current tick ends.</summary>
    public bool Profiling { get; private set; }

    /// <summary>Advances the duty cycle by one tick, folding <paramref name="previousTick"/> when
    /// it is a sample this burst wants. Returns the finished burst on the tick that completes
    /// one.</summary>
    public AttributionBurst? OnTick(double elapsedSeconds, ProfileEntryRange? previousTick, OwnerLookup owner)
    {
        if (!Profiling)
        {
            idleSeconds += elapsedSeconds;
            if (idleSeconds < IntervalSeconds)
            {
                return null;
            }

            idleSeconds = 0;
            burstTicksElapsed = 0;
            warm = false;
            Profiling = true;
            return null;
        }

        // The profiler was switched on part-way through the previous tick, so that tick never got
        // its Begin() and the tree it ended with is whatever the last burst left in the profiler.
        // One stale sample per burst, discarded here rather than folded.
        if (!warm)
        {
            warm = true;
            return null;
        }

        if (previousTick != null)
        {
            Fold(previousTick, owner);
        }

        // Counted whether or not there was a tree to read, so a burst always ends and the profiler
        // always goes back off.
        if (++burstTicksElapsed < BurstTicks)
        {
            return null;
        }

        Profiling = false;
        return Take();
    }

    /// <summary>Folds one completed tick's tree into the burst.</summary>
    /// <remarks>Every mark in the tree is disjoint from every other: entering a child range moves
    /// the parent's last-mark cursor past the child on the way out, so a child's time is never also
    /// charged to a parent mark. What the marks leave over is the engine's.</remarks>
    private void Fold(ProfileEntryRange root, OwnerLookup owner)
    {
        long sleep = root.Marks != null && root.Marks.TryGetValue(SleepMark, out ProfileEntry? nap)
            ? Elapsed(nap)
            : 0;

        long busy = Math.Max(0, root.ElapsedTicks - sleep);
        Add(Engine, Math.Max(0, busy - Walk(root, owner)));
        busyTicks += busy;
        sampled++;
    }

    /// <summary>Charges every mark under <paramref name="range"/> to its owner and returns their
    /// total.</summary>
    private long Walk(ProfileEntryRange range, OwnerLookup owner)
    {
        long total = 0;
        if (range.Marks != null)
        {
            foreach (KeyValuePair<string, ProfileEntry> mark in range.Marks)
            {
                if (mark.Key == SleepMark)
                {
                    continue;
                }

                long ticks = Elapsed(mark.Value);
                Add(Bucket(mark.Key, owner), ticks);
                total += ticks;
            }
        }

        if (range.ChildRanges != null)
        {
            foreach (ProfileEntryRange child in range.ChildRanges.Values)
            {
                total += Walk(child, owner);
            }
        }

        return total;
    }

    private static string Bucket(string mark, OwnerLookup owner)
    {
        foreach (string prefix in OwnedPrefixes)
        {
            if (mark.StartsWith(prefix, StringComparison.Ordinal))
            {
                return owner(mark[prefix.Length..]) ?? Unattributed;
            }
        }

        return Engine;
    }

    /// <summary>Reads one mark's elapsed time, dropping a reading that has wrapped.</summary>
    /// <remarks>A mark accumulates into an <c>int</c> (<c>FrameProfilerUtil.MarkInternal</c>) while
    /// the stopwatch behind it ticks at a nanosecond on Linux, so a single bucket goes negative
    /// past about 2.147 seconds inside one tick. That is exactly the pathological tick an operator
    /// wants explained, and a wrapped value is not a large number, it is garbage: drop it, count it
    /// and let the meta counter say how often it happened.</remarks>
    private long Elapsed(ProfileEntry entry)
    {
        if (entry.ElapsedTicks < 0)
        {
            dropped++;
            return 0;
        }

        return entry.ElapsedTicks;
    }

    private void Add(string modid, long ticks)
    {
        seenMods.Add(modid);
        ticksByMod.TryGetValue(modid, out long accumulated);
        ticksByMod[modid] = accumulated + ticks;
    }

    /// <summary>Closes the burst and starts the next one empty.</summary>
    private AttributionBurst Take()
    {
        double frequency = Stopwatch.Frequency;
        List<KeyValuePair<string, double>> seconds = [];
        foreach (string modid in seenMods.Order(StringComparer.Ordinal))
        {
            ticksByMod.TryGetValue(modid, out long ticks);
            seconds.Add(new KeyValuePair<string, double>(modid, ticks / frequency));
        }

        AttributionBurst burst = new(seconds, busyTicks / frequency, sampled, dropped);
        ticksByMod.Clear();
        busyTicks = 0;
        sampled = 0;
        dropped = 0;
        return burst;
    }
}

/// <summary>One completed burst: profiled seconds per mod, the busy time they are a share of, how
/// many ticks were folded into it, and how many marks were thrown away as wrapped.</summary>
internal sealed record AttributionBurst(
    IReadOnlyList<KeyValuePair<string, double>> Seconds, double BusySeconds, int Ticks, long Dropped);
