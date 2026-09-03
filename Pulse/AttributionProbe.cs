using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Pulse;

/// <summary>The second place in Pulse that names types from VintagestoryLib, and the only one that
/// reflects.</summary>
/// <remarks>The engine marks a tick listener with the type name of its handler's target and stops
/// there; nothing public says which mod that type came from. Walking the listener lists closes the
/// gap exactly, because <c>GameTickListener.Handler</c> is a public field on a public type and its
/// target's assembly is the mod's. The lists themselves are assembly-scoped fields on
/// <c>Vintagestory.Common.EventManager</c>, hence one reflected read each.
/// <para>This walk only sharpens <see cref="ModOwners"/>; it is never the only source. Without it
/// the table still maps every listener a mod registered from its own ModSystem, which is most of
/// them. Losing it costs the rest, and nothing else.</para>
/// <para>Members are deliberately not inlinable, for the same reason as <see cref="EngineProbe"/>:
/// a moved or renamed engine type surfaces as a TypeLoadException when the method naming it is JIT
/// compiled, and the caller can only catch that if the naming stays behind a call.</para></remarks>
internal sealed class AttributionProbe
{
    private readonly EventManager[] managers;
    private readonly FieldInfo? entityListeners;
    private readonly FieldInfo? blockListeners;

    private AttributionProbe(ServerMain server)
    {
        // Both managers, on purpose. Listeners from sapi.Event.RegisterGameTickListener land on
        // EventManager and broadcast handlers on ModEventManager, and TriggerGameTickDebug runs
        // through both, so a walk of one alone misses whatever the other holds.
        managers = [server.EventManager, server.ModEventManager];
        entityListeners = ListField("GameTickListenersEntity");
        blockListeners = ListField("GameTickListenersBlock");
    }

    /// <summary>Resolves the probe, or returns null when the world is not a ServerMain.</summary>
    /// <remarks>Call from inside a try/catch: this throws, rather than returning null, when the
    /// engine type is gone entirely.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static AttributionProbe? TryResolve(ICoreServerAPI api)
        => api.World as ServerMain is { } server ? new AttributionProbe(server) : null;

    /// <summary>Walks both event managers' tick listener lists and teaches <paramref name="owners"/>
    /// which mod each handler's target type belongs to.</summary>
    /// <remarks>Main thread only. These are plain lists the tick loop mutates, so a walk from the
    /// scrape thread would risk an InvalidOperationException and a torn read of a block list that
    /// on a built-up server holds thousands of entries.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Refresh(ModOwners owners)
    {
        // Indexed rather than foreach, and the count re-read every step: unregistering a listener
        // nulls its slot, and a mod registering one from a background thread can grow the list
        // underneath us. Neither is worth a lock on a walk that runs once per burst.
        foreach (EventManager manager in managers)
        {
            if (entityListeners?.GetValue(manager) is List<GameTickListener> entity)
            {
                for (int i = 0; i < entity.Count; i++)
                {
                    GameTickListener? listener = entity[i];
                    Learn(owners, listener?.ProfilerName, listener?.Handler);
                }
            }

            if (blockListeners?.GetValue(manager) is List<GameTickListenerBlock> block)
            {
                for (int i = 0; i < block.Count; i++)
                {
                    GameTickListenerBlock? listener = block[i];
                    Learn(owners, listener?.ProfilerName, (Delegate?)listener?.Handler ?? listener?.HandlerBare);
                }
            }
        }
    }

    private static FieldInfo? ListField(string name)
        => typeof(EventManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Pins one listener's mark name to the mod that declared its handler's target.</summary>
    /// <remarks>A null name is a handler on a static method: the engine marks those with the bare
    /// prefix and no identity at all, so there is nothing to learn and the mark reports as
    /// unattributed.</remarks>
    private static void Learn(ModOwners owners, string? name, Delegate? handler)
    {
        if (name != null && handler?.Target?.GetType().Assembly is { } assembly
            && (owners.OfAssembly(assembly) ?? EngineOrNull(assembly)) is { } modid)
        {
            owners.Learn(name, modid);
        }
    }

    /// <summary>The engine's own listeners, which are not unattributed: they are the engine.</summary>
    private static string? EngineOrNull(Assembly assembly)
        => assembly == typeof(ServerMain).Assembly || assembly == typeof(FrameProfilerUtil).Assembly
            ? TickAttribution.Engine
            : null;
}
