using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace Pulse;

/// <summary>The only place in Pulse that names a type from VintagestoryLib.</summary>
/// <remarks>The engine measures its own tick busy time and its own packet traffic, and exposes
/// neither through the modding API. Reading them means casting <c>sapi.World</c> to the concrete
/// <c>ServerMain</c>, which is public and stable in shape at 1.22.7 but carries no compatibility
/// promise. Keeping every such read in one small class means a game update that breaks it costs
/// exactly these families and nothing else.
/// <para>Both members are deliberately not inlinable. A missing or moved engine type surfaces as a
/// TypeLoadException when the method referring to it is JIT compiled, so the code that names
/// ServerMain has to stay behind a call the caller can wrap in try/catch. Inlined into the caller,
/// the failure would escape that catch and take the mod down at boot.</para></remarks>
internal sealed class EngineProbe
{
    private readonly ServerMain server;

    private EngineProbe(ServerMain server) => this.server = server;

    /// <summary>Resolves the probe, or returns null when the world is not a ServerMain.</summary>
    /// <remarks>Call from inside a try/catch: this throws, rather than returning null, when the
    /// engine type is gone entirely.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EngineProbe? TryResolve(ICoreServerAPI api)
        => api.World as ServerMain is { } server ? new EngineProbe(server) : null;

    /// <summary>Reads the last completed statistics bucket and the running totals beside it.</summary>
    /// <remarks>Main thread only. The engine writes these fields from the tick loop with no
    /// synchronisation of any kind, and rotates the bucket ring every two seconds; the live bucket
    /// is a partial window, so this reads the one behind it, exactly as the engine's own /stats
    /// command does.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EngineSample Read()
    {
        StatsCollection bucket = server.StatsCollector[
            GameMath.Mod(server.StatsCollectorIndex - 1, server.StatsCollector.Length)];

        // ponytail: ConnectionQueue.Count is read without taking the lock the engine puts around
        // its own mutations of that list. It is an int field read, so it cannot tear; the worst
        // case is a depth one admission stale, which is what a gauge is for. Take the lock the day
        // something here needs a consistent view of more than one field.
        return new EngineSample(
            EngineSample.BusySeconds(bucket.tickTimeTotal, bucket.ticksTotal),
            bucket.statTotalPackets,
            bucket.statTotalPacketsLength,
            bucket.statTotalUdpPackets,
            bucket.statTotalUdpPacketsLength,
            server.ConnectionQueue.Count,
            server.TotalSentBytesUdp,
            server.TotalReceivedBytesUdp);
    }
}
