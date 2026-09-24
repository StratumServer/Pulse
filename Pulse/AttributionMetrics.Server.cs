using System.Diagnostics.Metrics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse;

/// <summary>The half of <see cref="AttributionMetrics"/> that only a live server ever executes:
/// wiring it up in the first place, the <c>/pulse</c> command, and the config reload path.</summary>
/// <remarks>Kept in its own file, and named on its own line in
/// <c>sonar.coverage.exclusions</c>, for the same reason <c>PulseModSystem</c> and
/// <c>PulseOtlpModSystem</c> are: the Atlas scenarios are what exercise this, against a real
/// embedded server, and that execution happens outside coverlet's instrumentation, so it can never
/// reach a coverage report. Nothing here reflects into engine internals; it is ordinary
/// <see cref="ICoreServerAPI"/> use, same as the rest of <c>PulseModSystem</c>.</remarks>
internal sealed partial class AttributionMetrics
{
    /// <summary>Wires attribution against a live server: which mod owns which tick listener, the
    /// listener walk that sharpens it, the profiler priming, and the <c>/pulse</c> command, on top
    /// of the instruments and the duty cycle the chained constructor sets up.</summary>
    /// <remarks>All of it runs whether or not <c>Attribution.Enabled</c> is set, priming included,
    /// because that is what makes switching attribution on later structurally safe rather than
    /// merely likely to work: see PrimeFrameProfiler for what happens to a server whose profiler is
    /// enabled part-way through a tick having never completed one. The cost of arming an operator
    /// never uses is two profiled ticks at startup and four instruments nothing records into, and
    /// an instrument with no measurement is not a series: an idle server serves the same exposition
    /// it did before.</remarks>
    public AttributionMetrics(ICoreServerAPI api, Meter meter, PulseConfig booted)
        : this(
            meter,
            booted.Attribution ?? new AttributionConfig(),
            () => api.World.FrameProfiler,
            _ => { }, // replaced below: cannot reference attributionProbe before `this` exists
            (template, message) => api.Logger.Warning(template, message))
    {
        this.api = api;
        this.booted = booted;
        walkListeners = modOwners => attributionProbe?.Refresh(modOwners);

        owners = new ModOwners(api.ClassRegistry.GetEntityBehaviorClass);
        foreach (Mod mod in api.ModLoader.Mods)
        {
            foreach (ModSystem system in mod.Systems)
            {
                owners.AddSystem(mod.Info.ModID, system.GetType());
            }
        }

        try
        {
            attributionProbe = AttributionProbe.TryResolve(api);
        }
        catch (Exception e)
        {
            attributionProbe = null;
            api.Logger.Warning(ListenerWalkWarning, e.Message);
        }

        // Before the tick loop exists, and not one moment later. See PrimeFrameProfiler.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, PrimeFrameProfiler);
        if (attribution!.Enabled)
        {
            api.Logger.Notification(
                "Pulse attributes the tick per mod: bursts of {0} ticks every {1}s.",
                attribution.BurstTicks, attribution.IntervalSeconds);
        }
        else
        {
            api.Logger.Notification(
                "Pulse is ready to attribute the tick per mod but is not measuring: /pulse attribution on starts it.");
        }

        RegisterCommands(api);
    }

    /// <summary>Registers <c>/pulse</c>, which is how attribution gets switched on while the
    /// server is the thing you wanted to look at.</summary>
    /// <remarks><c>controlserver</c> rather than a privilege of its own, so the admins and the
    /// hosting panel console that already run <c>/stats</c> can run this too. Every handler here
    /// reaches the runtime state on the main thread: chat commands are dispatched while the server
    /// is handling packets, and the console reader enqueues its line as a main thread task.</remarks>
    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands.Create("pulse")
            .WithDescription("Per-mod tick attribution and config reload, without restarting the server.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("attribution")
                .WithDescription("Per-mod tick attribution on the running server.")
                .BeginSubCommand("on")
                    .WithDescription("Start the attribution duty cycle now. Does not write pulse.json.")
                    .HandleWith(_ => Switch(true))
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Stop it, and switch the engine's frame profiler back off.")
                    .HandleWith(_ => Switch(false))
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Say whether attribution is running, and what it has measured.")
                    .HandleWith(_ => Status())
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("reload")
                .WithDescription("Re-read pulse.json and apply what can change without a restart.")
                .HandleWith(_ => Reload())
            .EndSubCommand();
    }

    /// <summary>Re-reads the config file and applies the part of it a running server can take.</summary>
    /// <remarks>The same load and upgrade path startup uses, so a file that gained keys since it
    /// was written is completed here as well. A file that does not parse leaves everything exactly
    /// as it was: the reply carries the parse error and the server keeps running on what it
    /// booted with.</remarks>
    public TextCommandResult Reload()
    {
        PulseConfig loaded;
        try
        {
            loaded = api!.LoadModConfig<PulseConfig>(PulseModSystem.ConfigFile)
                ?? throw new FileNotFoundException(PulseModSystem.ConfigFile + " is not in ModConfig");
            ConfigUpgrade.Upgrade(api, loaded, PulseModSystem.ConfigFile, "Pulse");
        }
        catch (Exception e)
        {
            return new TextCommandResult
            {
                Status = EnumCommandStatus.Error,
                StatusMessage = PulseCommands.ReloadFailed,
                MessageParams = [e.Message],
            };
        }

        AttributionConfig cycle = loaded.Attribution ?? new AttributionConfig();
        attribution?.Apply(cycle.Enabled, cycle.BurstTicks, cycle.IntervalSeconds);
        Seed();

        return TextCommandResult.Success(PulseCommands.Reloaded(
            attribution?.Enabled ?? false,
            attribution?.BurstTicks ?? cycle.BurstTicks,
            attribution?.IntervalSeconds ?? cycle.IntervalSeconds,
            PulseCommands.RestartKeys(booted!, loaded)));
    }

    /// <summary>Turns the engine's frame profiler on once, before the server starts ticking.</summary>
    /// <remarks>This is not a nicety, it is the difference between a working feature and a server
    /// that dies the first time Pulse starts a burst. <c>FrameProfilerUtil.End</c> dereferences the
    /// root range that the matching <c>Begin</c> creates, and <c>ServerMain.Process</c> calls
    /// <c>End</c> outside the try/catch guarding the tick (1.22.7:1556-1562), from a loop with no
    /// guard of its own (<c>ServerProgram.cs:133-137</c>). On a server whose profiler has never
    /// run, flipping the flag part-way through a tick means <c>End</c> runs with no <c>Begin</c>
    /// before it and the NullReferenceException takes the process down. Enabling here, while
    /// <c>Launch</c> is still running, guarantees the first <c>Begin</c> establishes that root.
    /// Afterwards the duty cycle flips the flag from Pulse's own tick listener, where the profiler
    /// sits at depth zero and both directions are safe.</remarks>
    private void PrimeFrameProfiler()
    {
        // Guarded even though the profiler is thread-static and this runs on the thread that will
        // do the ticking: nothing wraps a run phase handler, and throwing out of one would take the
        // server's startup with it.
        if (resolveProfiler() is { } profiler)
        {
            profiler.Enabled = true;
        }
    }
}
