using System.Diagnostics.Metrics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Pulse;

/// <summary>The half of <see cref="StratumTimingsMetrics"/> that only a live server ever executes:
/// finding Stratum's type in the API the server actually loaded, and applying a reloaded config.</summary>
/// <remarks>Kept in its own file, and named on its own line in
/// <c>sonar.coverage.exclusions</c>, for the same reason <c>AttributionMetrics.Server.cs</c> is: it is
/// the wiring against <see cref="ICoreServerAPI"/>, which the Atlas scenarios exercise against a real
/// embedded server, outside coverlet's instrumentation. Nothing here reflects into engine internals
/// beyond the one by-name lookup the binder is built around.</remarks>
internal sealed partial class StratumTimingsMetrics
{
    /// <summary>Wires the feature against a live server: looks for Stratum's reading contract and
    /// binds it, and has the behavior walk read the entities the world has loaded.</summary>
    /// <remarks>Binds whatever the config says, the way attribution primes its profiler either way:
    /// it is one reflection pass, and it is what lets <c>/pulse reload</c> switch the feature on
    /// later without a restart. The type is looked up in the one assembly that matters, the API the
    /// server actually loaded, so a mod that declares a type of the same name in its own assembly
    /// cannot be mistaken for Stratum. The binder can throw on a type that is broken in a way of its
    /// own, so the caller guards this the way it guards the engine probe.</remarks>
    /// <param name="owners">Hands out the table of which mod ships what, which is built from the mod
    /// list. It is asked for only on a server the feature can serve: anywhere else the feature has no
    /// use for it, and a mod list that cannot be read must not cost it a line of its own in the log.</param>
    public static StratumTimingsMetrics Create(
        ICoreServerAPI api, ILogger logger, Meter meter, PulseConfig booted, Func<ModOwners> owners)
        => Create(
            typeof(Entity).Assembly.GetType(StratumTimingsSource.TypeName, throwOnError: false),
            api, logger, meter, booted, owners);

    /// <summary>The same, with the type that stands for Stratum's accumulator handed in rather than
    /// looked up in the game's API, which is what lets a test bind a fake without a Stratum.</summary>
    internal static StratumTimingsMetrics Create(
        Type? stratum, ICoreServerAPI api, ILogger logger, Meter meter, PulseConfig booted, Func<ModOwners> owners)
    {
        StratumTimingsSource? source = StratumTimingsSource.TryBind(stratum, out string? reason);
        return new StratumTimingsMetrics(
            meter,
            booted.StratumTimings ?? new StratumTimingsConfig(),
            source,
            reason,
            source != null ? owners() : new ModOwners(_ => null),
            modOwners => modOwners.LearnBehaviors(api.World.LoadedEntities),
            logger);
    }

    /// <summary>Applies the block of a config file that <c>/pulse reload</c> has just read, and returns
    /// what the reply says about it.</summary>
    /// <remarks>The command is registered by attribution's constructor, which calls this, so when that
    /// constructor fails, or another mod already owns <c>/pulse</c>, nothing ever calls it and the
    /// block can only be set at boot.</remarks>
    public string? Reload(PulseConfig loaded) => Apply(loaded.StratumTimings ?? new StratumTimingsConfig());
}
