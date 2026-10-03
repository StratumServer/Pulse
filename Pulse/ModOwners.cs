using System.Reflection;
using Vintagestory.API.Common.Entities;

namespace Pulse;

/// <summary>Maps the name the engine stamps into a profiler mark back to the mod that owns it.</summary>
/// <remarks>Two name spaces share one table, and they cannot collide. Game tick listeners, block
/// listeners and delayed callbacks are marked with the fully qualified type name of the handler's
/// target (<c>GameTickListener.ProfilerName</c>); entity behaviors are marked with the behavior's
/// property name (<c>EntityBehavior.ProfilerName</c>). A dotted CLR type name is never a behavior's
/// property name.
/// <para>The table is seeded from the mod loader, which is public API and always available, and
/// sharpened by the listener walk in <see cref="AttributionProbe"/>, which is not.</para>
/// <para>A behavior's property name is not the code its class was registered under: in 1.22.7, 18
/// of the 59 classes the game's own mods register report another name (the controlled and the
/// player physics both report <c>entitycontrolledphysics</c>, the name tag <c>displayname</c>, the
/// despawn and revive-on-death pair <c>timeddespawn</c>), and a third-party class is free to do the
/// same. The class registry is keyed by code, so it can only turn back the names that happen to
/// match. What knows both ends is a live instance, which carries the mark it stamps and the type
/// that declares it, so <see cref="LearnBehaviors"/> reads them off the loaded entities and the
/// registry stays as the fallback for a name no entity has shown yet. A behavior class declared in
/// the game's own API assembly (the passive physics, which the engine registers itself) belongs to
/// the engine, like the listeners the engine registers.</para></remarks>
internal sealed class ModOwners(Func<string, Type?> behaviorClass)
{
    /// <summary>The assembly the game's own entity behaviors are declared in, when they are not a
    /// mod's.</summary>
    private static readonly Assembly EngineApi = typeof(EntityBehavior).Assembly;

    private readonly Dictionary<Assembly, string> byAssembly = [];
    private readonly Dictionary<string, string?> byName = [];

    /// <summary>Behavior names a live instance has taught the table, as opposed to the ones
    /// <see cref="byName"/> only guessed through the class registry.</summary>
    private readonly HashSet<string> learnedBehaviors = [];

    /// <summary>Behavior classes <see cref="LearnBehavior"/> has already looked at. A class marks with
    /// one name, so a server with thousands of chickens and a few dozen classes pays one set lookup
    /// per instance and nothing more.</summary>
    private readonly HashSet<Type> seenBehaviorClasses = [];

    /// <summary>The entities whose behaviors <see cref="LearnBehaviors"/> has already read, and the
    /// set the next walk fills while it goes. They swap places at the end of a walk, so once both
    /// have grown to the number of loaded entities a burst allocates nothing for them.</summary>
    private HashSet<long> readEntities = [];
    private HashSet<long> stillLoaded = [];

    /// <summary>Records one of a mod's own systems: its assembly identifies the mod, and its type
    /// name is the mark a listener registered from that system produces.</summary>
    public void AddSystem(string modid, Type system)
    {
        byAssembly[system.Assembly] = modid;
        byName[system.ToString()] = modid;
    }

    /// <summary>The mod that ships <paramref name="assembly"/>, or null when no loaded mod claims
    /// it. A mod's side libraries are among the nulls: only the assembly a ModSystem was declared
    /// in is claimed.</summary>
    public string? OfAssembly(Assembly assembly)
        => byAssembly.TryGetValue(assembly, out string? modid) ? modid : null;

    /// <summary>Pins a mark name to a mod id, overriding whatever the table would work out on its
    /// own.</summary>
    public void Learn(string name, string modid) => byName[name] = modid;

    /// <summary>Teaches the table what the behaviors of the loaded entities mark with, and which mod
    /// ships their classes.</summary>
    /// <remarks>Main thread only, once per burst: this is the walk, so it never runs per mark. An
    /// entity is read the first time it is seen and not again. The engine builds an entity's
    /// behaviors before the entity becomes visible here and nothing in the game adds one later, so
    /// a burst costs a set lookup per entity rather than a visit to every behavior of every one,
    /// which on thousands of entities is the difference between a fraction of a millisecond and
    /// several. A mod that adds a behavior to an entity already read is still covered by the class
    /// registry, and by any other entity that carries the class. The ids of entities that have gone
    /// are dropped, so they do not pile up. The entity table is a concurrent dictionary, but each
    /// entity's behavior list is a plain list the tick loop mutates: indexed, with the count
    /// re-read every step, because a mod adding or removing a behavior must not turn into an
    /// exception that ends the walk.</remarks>
    public void LearnBehaviors(IEnumerable<KeyValuePair<long, Entity>> loaded)
    {
        stillLoaded.Clear();
        foreach (KeyValuePair<long, Entity> entry in loaded)
        {
            stillLoaded.Add(entry.Key);
            if (readEntities.Contains(entry.Key))
            {
                continue;
            }

            List<EntityBehavior>? behaviors = entry.Value?.Properties?.Server?.Behaviors;
            for (int i = 0; i < behaviors?.Count; i++)
            {
                if (behaviors[i] is { } behavior)
                {
                    LearnBehavior(behavior.ProfilerName, behavior.GetType());
                }
            }
        }

        (readEntities, stillLoaded) = (stillLoaded, readEntities);
    }

    /// <summary>Teaches the table what one live behavior marks with and which mod ships its
    /// class.</summary>
    /// <remarks>Two classes can share one name, since a subclass inherits its parent's property
    /// name and the mark carries nothing else, so the table cannot tell them apart. The first class
    /// that has an owner keeps the name for good: otherwise the answer would depend on which entity
    /// the next walk happens to meet first, and a series would flap between two mods from one burst
    /// to the next. A class nothing owns never takes a name from one that has an owner.</remarks>
    public void LearnBehavior(string profilerName, Type behavior)
    {
        if (!seenBehaviorClasses.Add(behavior)
            || !profilerName.StartsWith(TickAttribution.BehaviorPrefix, StringComparison.Ordinal))
        {
            return;
        }

        string name = profilerName[TickAttribution.BehaviorPrefix.Length..];
        string? modid = OwnerOfClass(behavior);
        if (learnedBehaviors.Add(name) || (byName[name] == null && modid != null))
        {
            byName[name] = modid;
        }
    }

    /// <summary>The mod behind a mark name, or null when nothing claims it.</summary>
    public string? Owner(string name)
    {
        if (byName.TryGetValue(name, out string? known))
        {
            return known;
        }

        // Neither a type name the table was told about nor a behavior name a live instance has
        // shown, so try it as the code a behavior class was registered under: the class registry
        // is the only thing that can turn one back into a type, and it answers for the names that
        // match their class's code. Remembered either way, so a name that resolves to nothing is
        // looked up once and never again (until an instance of it turns up and teaches the table).
        Type? behavior = behaviorClass(name);
        string? resolved = behavior == null ? null : OwnerOfClass(behavior);
        byName[name] = resolved;
        return resolved;
    }

    private string? OwnerOfClass(Type behavior)
        => OfAssembly(behavior.Assembly) ?? (behavior.Assembly == EngineApi ? TickAttribution.Engine : null);
}
