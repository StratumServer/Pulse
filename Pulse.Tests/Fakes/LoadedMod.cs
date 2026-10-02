using Vintagestory.API.Common;
using Vintagestory.Common;

namespace Pulse.Tests.Fakes;

/// <summary>Does to a mod system what the mod loader does before it runs it: hands it the mod it
/// belongs to, with the engine's own logger for that mod, which marks every entry with the mod id
/// and passes it on to the server logger.</summary>
internal static class LoadedMod
{
    /// <summary>Sets <see cref="ModSystem.Mod"/> the way <c>ModContainer.InstantiateModSystems</c>
    /// does. Both setters are internal to the engine, hence the reflection; the container, and the
    /// <c>ModLogger</c> it builds over <paramref name="serverLogger"/>, are the engine's own.</summary>
    public static void Attach(ModSystem system, string modId, ILogger serverLogger)
    {
        ModContainer container = new(new DirectoryInfo(modId), serverLogger, logDebug: false);
        typeof(Mod).GetProperty(nameof(Mod.Info))!.SetValue(container, new ModInfo { ModID = modId });
        typeof(ModSystem).GetProperty(nameof(ModSystem.Mod))!.SetValue(system, container);
    }
}
