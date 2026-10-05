namespace Pulse;

/// <summary>The kinds of series Pulse reads out of Stratum's accumulator.</summary>
internal enum StratumFamily
{
    /// <summary>One entity behavior's tick, on the main thread or on the physics threads.</summary>
    Behavior,

    /// <summary>One AI task's <c>ContinueExecute</c>.</summary>
    AiTask,

    /// <summary>One entity's whole tick, grouped by the first part of its code.</summary>
    Entity,
}

/// <summary>What one accumulator key stands for: the family it feeds and the values of that
/// family's labels, before the cardinality cap and the mod lookup have had their say.</summary>
/// <remarks><see cref="Category"/> is empty for an entity, which has none, and
/// <see cref="ThreadSafe"/> means something only for a behavior. Records compare by value, so the
/// many entity codes that share a first part are one key, which is what lets the fold sum them by
/// grouping on the key itself.</remarks>
internal sealed record StratumKey(StratumFamily Family, string Category, string Name, bool ThreadSafe);

/// <summary>Reads the keys of Stratum's accumulator, which are built by string concatenation at the
/// call site (<c>entity.behavior.players.health</c>), into the series they belong to.</summary>
/// <remarks>A pure function, memoised per key string. Stratum builds a fresh string for nearly
/// every key on every call, but the set of distinct keys is bounded by the game's registries, so
/// each shape is parsed once and every later burst pays one dictionary lookup per key and
/// allocates no label strings. A key Pulse does not recognise is ignored, and remembered as ignored,
/// rather than guessed at: that is also what keeps a key family a future Stratum adds from appearing
/// under a wrong name.
/// <para>The shapes, from Stratum's timing call sites. <c>&lt;category&gt;</c> is
/// <c>players</c>, <c>creatures</c> or <c>inanimate</c> and runs to the next dot, and what follows it
/// is taken whole, dots included, so a mod's dotted behavior name survives:</para>
/// <list type="bullet">
/// <item><c>entity.behavior.threadsafe.&lt;category&gt;.done-behavior-&lt;name&gt;</c>: a thread-safe
/// behavior, named by the engine's profiler mark, whose prefix is stripped so that both threads
/// speak the same names.</item>
/// <item><c>entity.behavior.&lt;category&gt;.&lt;name&gt;</c>: a main-thread behavior (Stratum times
/// those for players only).</item>
/// <item><c>entity.ai.&lt;category&gt;.task.&lt;code&gt;</c>: one AI task. The four phase keys beside it
/// (<c>taskai.pathTraverser</c>, <c>taskai.taskManager</c>, <c>taskStartScan</c>, <c>taskContinue</c>)
/// nest inside each other and inside the entity totals, so they are ignored.</item>
/// <item><c>entity.type.&lt;code path&gt;</c>: one entity's whole tick, grouped by the first part of
/// its code, cut at the first dash like the engine's own <c>AssetLocation.FirstCodePart</c>. Stratum
/// writes the path without its domain, so <c>game:wolf-eurasian-adult-male</c> and
/// <c>mymod:wolf-arctic</c> are both <c>wolf</c>.</item>
/// </list>
/// <para>Main thread only: the memo is a plain dictionary.</para></remarks>
// ponytail: the memo keeps every distinct key it has met, which is no more than Stratum's own table
// holds at its largest. Bound it the day a mod mints entity codes at run time.
internal sealed class StratumKeyParser
{
    private const string ThreadSafeBehavior = "entity.behavior.threadsafe.";
    private const string MainThreadBehavior = "entity.behavior.";
    private const string Ai = "entity.ai.";
    private const string AiTask = "task.";
    private const string EntityType = "entity.type.";

    private readonly Dictionary<string, StratumKey?> memo = [];

    /// <summary>The series <paramref name="key"/> belongs to, or null for a key Pulse does not
    /// read.</summary>
    /// <remarks>The same instance comes back for every key with the same text.</remarks>
    public StratumKey? Parse(string key)
    {
        if (!memo.TryGetValue(key, out StratumKey? parsed))
        {
            memo[key] = parsed = Read(key);
        }

        return parsed;
    }

    private static StratumKey? Read(string key)
    {
        // The thread-safe shape first: it starts with the main-thread one.
        if (key.StartsWith(ThreadSafeBehavior, StringComparison.Ordinal))
        {
            return ReadBehavior(key, ThreadSafeBehavior.Length, threadSafe: true);
        }

        if (key.StartsWith(MainThreadBehavior, StringComparison.Ordinal))
        {
            return ReadBehavior(key, MainThreadBehavior.Length, threadSafe: false);
        }

        if (key.StartsWith(Ai, StringComparison.Ordinal))
        {
            return ReadTask(key);
        }

        return key.StartsWith(EntityType, StringComparison.Ordinal) ? ReadEntity(key) : null;
    }

    private static StratumKey? ReadBehavior(string key, int categoryStart, bool threadSafe)
    {
        // Covers both a key that stops at the category (no dot) and one whose category is empty.
        int dot = key.IndexOf('.', categoryStart);
        if (dot <= categoryStart)
        {
            return null;
        }

        string name = key[(dot + 1)..];
        if (threadSafe && name.StartsWith(TickAttribution.BehaviorPrefix, StringComparison.Ordinal))
        {
            name = name[TickAttribution.BehaviorPrefix.Length..];
        }

        return name.Length == 0
            ? null
            : new StratumKey(StratumFamily.Behavior, key[categoryStart..dot], name, threadSafe);
    }

    private static StratumKey? ReadTask(string key)
    {
        int dot = key.IndexOf('.', Ai.Length);
        if (dot <= Ai.Length)
        {
            return null;
        }

        // The segment after the category has to be exactly "task": "taskai" and the two "task" phase
        // keys are other shapes.
        int nameStart = dot + 1 + AiTask.Length;
        if (!key.AsSpan(dot + 1).StartsWith(AiTask, StringComparison.Ordinal) || nameStart >= key.Length)
        {
            return null;
        }

        return new StratumKey(StratumFamily.AiTask, key[Ai.Length..dot], key[nameStart..], false);
    }

    private static StratumKey? ReadEntity(string key)
    {
        // An empty cut (a path that starts with a dash) is ignored, not published as an empty label.
        int dash = key.IndexOf('-', EntityType.Length);
        string type = key[EntityType.Length..(dash < 0 ? key.Length : dash)];
        return type.Length == 0 ? null : new StratumKey(StratumFamily.Entity, string.Empty, type, false);
    }
}
