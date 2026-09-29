using System.Reflection;

namespace Pulse;

/// <summary>Maps the name the engine stamps into a profiler mark back to the mod that owns it.</summary>
/// <remarks>Two name spaces share one table, and they cannot collide. Game tick listeners, block
/// listeners and delayed callbacks are marked with the fully qualified type name of the handler's
/// target (<c>GameTickListener.ProfilerName</c>); entity behaviors are marked with the code their
/// class was registered under (<c>EntityBehavior.ProfilerName</c>). A dotted CLR type name is never
/// a behavior code.
/// <para>The table is seeded from the mod loader, which is public API and always available, and
/// sharpened by the listener walk in <see cref="AttributionProbe"/>, which is not. Behavior codes
/// are resolved on first sight through the class registry and then remembered, misses
/// included.</para></remarks>
internal sealed class ModOwners(Func<string, Type?> behaviorClass)
{
    private readonly Dictionary<Assembly, string> byAssembly = [];
    private readonly Dictionary<string, string?> byName = [];

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

    /// <summary>The mod behind a mark name, or null when nothing claims it.</summary>
    public string? Owner(string name)
    {
        if (byName.TryGetValue(name, out string? known))
        {
            return known;
        }

        // Not a type name the table was told about, so try it as an entity behavior code: the
        // class registry is the only thing that can turn one back into a type. Remembered either
        // way, so a name that resolves to nothing is looked up once and never again.
        Type? behavior = behaviorClass(name);
        string? resolved = behavior == null ? null : OfAssembly(behavior.Assembly);
        byName[name] = resolved;
        return resolved;
    }
}
