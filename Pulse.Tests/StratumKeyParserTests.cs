using Vintagestory.API.Common;
using Xunit;

namespace Pulse.Tests;

public class StratumKeyParserTests
{
    private static StratumKey Read(string key)
        => new StratumKeyParser().Parse(key) ?? throw new InvalidOperationException(key + " was ignored");

    /// <summary>Every shape the accumulator's keys have today, one per call site, and the dotted
    /// names a mod's behavior or task may carry.</summary>
    [Theory]
    // A thread-safe behavior carries the engine's profiler mark, whose prefix is the one thing
    // stripped.
    [InlineData("entity.behavior.threadsafe.creatures.done-behavior-entitypassivephysics", nameof(StratumFamily.Behavior), "creatures", "entitypassivephysics", true)]
    [InlineData("entity.behavior.threadsafe.inanimate.done-behavior-harvestable", nameof(StratumFamily.Behavior), "inanimate", "harvestable", true)]
    [InlineData("entity.behavior.threadsafe.players.done-behavior-entitycontrolledphysics", nameof(StratumFamily.Behavior), "players", "entitycontrolledphysics", true)]
    [InlineData("entity.behavior.threadsafe.creatures.done-behavior-mymod.fancy", nameof(StratumFamily.Behavior), "creatures", "mymod.fancy", true)]
    [InlineData("entity.behavior.threadsafe.creatures.fancy", nameof(StratumFamily.Behavior), "creatures", "fancy", true)]
    // A main-thread behavior is recorded under its property name as it is.
    [InlineData("entity.behavior.players.health", nameof(StratumFamily.Behavior), "players", "health", false)]
    [InlineData("entity.behavior.players.entityStateTags", nameof(StratumFamily.Behavior), "players", "entityStateTags", false)]
    [InlineData("entity.behavior.players.mymod.fancy", nameof(StratumFamily.Behavior), "players", "mymod.fancy", false)]
    // The prefix is the thread-safe path's profiler mark. A main-thread name is taken as written.
    [InlineData("entity.behavior.players.done-behavior-health", nameof(StratumFamily.Behavior), "players", "done-behavior-health", false)]
    // One AI task, by the code in the task registry or the word Stratum writes for a task with none.
    [InlineData("entity.ai.creatures.task.idle", nameof(StratumFamily.AiTask), "creatures", "idle", false)]
    [InlineData("entity.ai.creatures.task.meleeattack", nameof(StratumFamily.AiTask), "creatures", "meleeattack", false)]
    [InlineData("entity.ai.creatures.task.unknown", nameof(StratumFamily.AiTask), "creatures", "unknown", false)]
    [InlineData("entity.ai.creatures.task.mymod.fly", nameof(StratumFamily.AiTask), "creatures", "mymod.fly", false)]
    [InlineData("entity.ai.inanimate.task.idle", nameof(StratumFamily.AiTask), "inanimate", "idle", false)]
    // One entity's whole tick, by the first part of its code. A path with no dash is all first part.
    [InlineData("entity.type.wolf-eurasian-adult-male", nameof(StratumFamily.Entity), "", "wolf", false)]
    [InlineData("entity.type.chicken-hen", nameof(StratumFamily.Entity), "", "chicken", false)]
    [InlineData("entity.type.butterfly", nameof(StratumFamily.Entity), "", "butterfly", false)]
    [InlineData("entity.type.unknown", nameof(StratumFamily.Entity), "", "unknown", false)]
    [InlineData("entity.type.mymod.eagle-adult", nameof(StratumFamily.Entity), "", "mymod.eagle", false)]
    public void Parse_Reads_EveryShapeOfKey(string key, string family, string category, string name, bool threadSafe)
    {
        StratumKey parsed = Read(key);

        Assert.Equal(family, parsed.Family.ToString());
        Assert.Equal(category, parsed.Category);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(threadSafe, parsed.ThreadSafe);
    }

    /// <summary>What Pulse does not read is left alone, not guessed at: the AI phase keys that nest
    /// inside each other, the sections Stratum times on its own, and anything malformed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("entity")]
    [InlineData("entity.")]
    // The four AI phase keys, which nest inside each other and inside the entity totals.
    [InlineData("entity.ai.creatures.taskai.pathTraverser")]
    [InlineData("entity.ai.creatures.taskai.taskManager")]
    [InlineData("entity.ai.creatures.taskStartScan")]
    [InlineData("entity.ai.creatures.taskContinue")]
    // Keys that stop short of what they name.
    [InlineData("entity.behavior.")]
    [InlineData("entity.behavior.players")]
    [InlineData("entity.behavior.players.")]
    [InlineData("entity.behavior..health")]
    [InlineData("entity.behavior.threadsafe.")]
    [InlineData("entity.behavior.threadsafe.creatures")]
    [InlineData("entity.behavior.threadsafe.creatures.")]
    [InlineData("entity.behavior.threadsafe.creatures.done-behavior-")]
    [InlineData("entity.behavior.threadsafe..done-behavior-health")]
    [InlineData("entity.ai.")]
    [InlineData("entity.ai.creatures")]
    [InlineData("entity.ai.creatures.")]
    [InlineData("entity.ai.creatures.task")]
    [InlineData("entity.ai.creatures.task.")]
    [InlineData("entity.ai..task.idle")]
    [InlineData("entity.type.")]
    [InlineData("entity.type.-wolf")]
    // Another shape that happens to share a word.
    [InlineData("entity.types.wolf")]
    [InlineData("entity.behaviors.players.health")]
    [InlineData("entity.tick.wolf")]
    [InlineData("physics.tickWork")]
    [InlineData("eventTick.listeners")]
    // Case matters, and so does where the prefix sits.
    [InlineData("Entity.type.wolf")]
    [InlineData("xentity.type.wolf")]
    [InlineData(" entity.type.wolf")]
    public void Parse_Ignores_AKeyItDoesNotRead(string key)
        => Assert.Null(new StratumKeyParser().Parse(key));

    /// <summary>The cut is the engine's own: <c>AssetLocation.FirstCodePart</c> on the same path,
    /// which Stratum has already stripped of its domain.</summary>
    [Theory]
    [InlineData("wolf-eurasian-adult-male")]
    [InlineData("chicken-hen")]
    [InlineData("chicken-baby")]
    [InlineData("fish-salmon-adult")]
    [InlineData("butterfly")]
    [InlineData("a-b-c")]
    [InlineData("hare-forest-adult-female-")]
    public void Parse_Cuts_AnEntityTypeWhereTheEnginesFirstCodePartDoes(string path)
        => Assert.Equal(new AssetLocation("game", path).FirstCodePart(), Read("entity.type." + path).Name);

    /// <summary>Why the type, and not the code, is the label: every variant of an entity is one
    /// key, so the fold sums them without being told.</summary>
    [Fact]
    public void Parse_Gives_EveryEntityCodeWithTheSameFirstPart_TheSameKey()
        => Assert.Equal(Read("entity.type.wolf-eurasian-adult-male"), Read("entity.type.wolf-arctic-baby-female"));

    [Fact]
    public void Parse_Tells_ABehaviorOnTheMainThread_FromTheSameNameOnThePhysicsThreads()
        => Assert.NotEqual(
            Read("entity.behavior.players.entitycontrolledphysics"),
            Read("entity.behavior.threadsafe.players.done-behavior-entitycontrolledphysics"));

    [Fact]
    public void Parse_Tells_TheCategoriesApart()
        => Assert.NotEqual(
            Read("entity.ai.creatures.task.idle"),
            Read("entity.ai.inanimate.task.idle"));

    /// <summary>Stratum builds a fresh string for nearly every key on every call, so the memo has to
    /// be by content. A parse that were not remembered would allocate its label strings again at
    /// every burst, which shows as a different instance.</summary>
    [Theory]
    [InlineData("entity.behavior.threadsafe.creatures.done-behavior-entitypassivephysics")]
    [InlineData("entity.behavior.players.health")]
    [InlineData("entity.ai.creatures.task.idle")]
    [InlineData("entity.type.wolf-eurasian-adult-male")]
    public void Parse_Remembers_AKeyByItsContent_NotItsReference(string key)
    {
        StratumKeyParser parser = new();
        string again = new(key.AsSpan());
        Assert.NotSame(key, again);

        StratumKey? first = parser.Parse(key);

        Assert.NotNull(first);
        Assert.Same(first, parser.Parse(again));
    }
}
