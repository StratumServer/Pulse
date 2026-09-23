using System.Diagnostics.Metrics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse;

/// <summary>Per-mod tick attribution end to end: the instruments and the duty cycle, which mod owns
/// which tick listener, the listener walk that sharpens it, the profiler priming that makes
/// switching it on safe, and the <c>/pulse</c> command and config reload that operate it from a
/// running server.</summary>
/// <remarks>Extracted out of <see cref="PulseModSystem"/>, which otherwise stays a table of contents
/// delegating to a collaborator per concern: this was the one concern still spelled out inline,
/// about 200 lines behind one config flag and five of its thirty fields. The tick-processing core
/// and the priming callback read the engine's frame profiler through a resolver function rather than
/// off <see cref="ICoreServerAPI"/> directly, which is what makes the unprimed-tick give-up path and
/// the on/off command transitions drivable from a unit test without a live server: the constructor
/// that skips server wiring supplies that resolver and the warning sink directly.</remarks>
internal sealed class AttributionMetrics
{
    private const string AttributionWarning =
        "Pulse could not read the engine's frame profiler ({0}). Per-mod tick attribution is off "
        + "for the rest of this run and its families stop updating; every other metric is "
        + "unaffected.";

    private const string ListenerWalkWarning =
        "Pulse could not read the engine's tick listener lists ({0}). Per-mod attribution carries "
        + "on from the mod loader's own type list, which maps fewer marks: the rest report as "
        + "unattributed.";

    private const string ConfigFile = "pulse.json";

    /// <summary>How many ticks attribution waits for the primed profiler to complete one, before
    /// concluding that priming never took. Roughly half a minute at the default tick rate.</summary>
    private const int UnprimedTickLimit = 1000;

    private readonly Func<FrameProfilerUtil?> resolveProfiler;
    private readonly Action<string> warn;
    private readonly Gauge<double> modTickShare;
    private readonly Counter<double> modTickSeconds;
    private readonly Counter<long> attributionTicks;
    private readonly Counter<long> attributionDropped;

    private TickAttribution? attribution;
    private ModOwners? owners;
    private AttributionProbe? attributionProbe;
    private ICoreServerAPI? api;
    private PulseConfig? booted;
    private int unprimedTicks;

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
            message => api.Logger.Warning(AttributionWarning, message))
    {
        this.api = api;
        this.booted = booted;

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

    /// <summary>Creates the duty cycle and the instruments it publishes to, with the profiler and
    /// the warning sink supplied directly rather than read off a live server: what a unit test calls
    /// to drive the unprimed-tick give-up path and the on/off command transitions without one.</summary>
    internal AttributionMetrics(
        Meter meter, AttributionConfig config, Func<FrameProfilerUtil?> resolveProfiler, Action<string> warn)
    {
        this.resolveProfiler = resolveProfiler;
        this.warn = warn;
        attribution = new TickAttribution(config.BurstTicks, config.IntervalSeconds, config.Enabled);
        modTickShare = meter.CreateGauge<double>(
            "pulse_mod_tick_share", "1",
            "Fraction of the profiled main-thread busy time attributed to one mod over the last completed burst.");
        modTickSeconds = meter.CreateCounter<double>(
            "pulse_mod_tick_seconds_total", "s",
            "Main-thread seconds attributed to one mod while attribution was profiling. Sampled: this is time inside the bursts, not since startup.");
        attributionTicks = meter.CreateCounter<long>(
            "pulse_attribution_ticks_total", "{tick}",
            "Ticks actually profiled, so the sampled seconds can be normalised against the ticks they came from.");
        attributionDropped = meter.CreateCounter<long>(
            "pulse_attribution_dropped_samples_total", "{sample}",
            "Profiler marks discarded because their elapsed time had overflowed the engine's 32 bit counter.");
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

    public TextCommandResult Switch(bool on)
    {
        if (attribution == null)
        {
            return TextCommandResult.Error(PulseCommands.Unavailable);
        }

        attribution.Apply(on, attribution.BurstTicks, attribution.IntervalSeconds);
        Seed();
        return TextCommandResult.Success(
            PulseCommands.Switched(on, attribution.BurstTicks, attribution.IntervalSeconds));
    }

    public TextCommandResult Status()
        => attribution == null
            ? TextCommandResult.Error(PulseCommands.Unavailable)
            : TextCommandResult.Success(PulseCommands.Status(
                attribution.Enabled,
                attribution.BurstTicks,
                attribution.IntervalSeconds,
                attribution.TicksProfiled,
                attribution.Profiling));

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
            loaded = api!.LoadModConfig<PulseConfig>(ConfigFile)
                ?? throw new FileNotFoundException(ConfigFile + " is not in ModConfig");
            ConfigUpgrade.Upgrade(api, loaded, ConfigFile, "Pulse");
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

    /// <summary>Advances the attribution duty cycle by one tick, and gives up on it for good if
    /// that ever throws.</summary>
    /// <remarks>Same bargain as the engine probe, with one addition: the profiler flag is put back
    /// before giving up, because leaving it on would charge every later tick a few percent for data
    /// nobody is reading any more.</remarks>
    public void Tick(double elapsedSeconds)
    {
        if (attribution == null || resolveProfiler() is not { } profiler)
        {
            return;
        }

        // The guard that makes the crash in PrimeFrameProfiler structurally impossible rather than
        // merely avoided. Only End() sets PrevRootEntry, and it sets it after dereferencing the
        // root range that Begin() creates, so a non-null value here is proof that the profiler has
        // completed a tick and that the same dereference will not throw next time. The flag is
        // never flipped on before that proof exists.
        if (profiler.PrevRootEntry == null)
        {
            // Priming runs once, before the tick loop, and the very next completed tick sets this.
            // Still null half a minute later means the flag never took, on a thread this cannot
            // reach: stop rather than report zeros that look like a server nothing is running on.
            if (++unprimedTicks > UnprimedTickLimit)
            {
                attribution = null;
                profiler.Enabled = false;
                warn("the engine's profiler never completed a primed tick");
            }

            return;
        }

        try
        {
            bool starting = !attribution!.Profiling;
            AttributionBurst? burst = attribution.OnTick(elapsedSeconds, profiler.PrevRootEntry, owners!.Owner);
            if (starting && attribution.Profiling)
            {
                RefreshOwners();
            }

            if (burst != null)
            {
                PublishBurst(burst);
            }

            profiler.Enabled = attribution.Profiling;
        }
        catch (Exception e)
        {
            attribution = null;
            profiler.Enabled = false;
            warn(e.Message);
        }
    }

    /// <summary>Re-reads which mod owns which tick listener, once per burst.</summary>
    /// <remarks>Once per burst rather than once at startup because mods register and drop listeners
    /// as the world runs. Its own catch: losing the walk costs precision in the map, not the
    /// feature.</remarks>
    private void RefreshOwners()
    {
        try
        {
            attributionProbe?.Refresh(owners!);
        }
        catch (Exception e)
        {
            attributionProbe = null;
            warn(e.Message);
        }
    }

    private void PublishBurst(AttributionBurst burst)
    {
        attributionTicks.Add(burst.Ticks);
        attributionDropped.Add(burst.Dropped);
        foreach (KeyValuePair<string, double> entry in burst.Seconds)
        {
            KeyValuePair<string, object?> modid = new("modid", entry.Key);
            modTickSeconds.Add(entry.Value, modid);
            modTickShare.Record(burst.BusySeconds > 0 ? entry.Value / burst.BusySeconds : 0, modid);
        }
    }

    /// <summary>Puts the attribution families on the wire from boot, at zero, rather than the first
    /// time a burst completes.</summary>
    /// <remarks>The two labelled families are seeded on the buckets that always exist. A mod's own
    /// series still appears the first time it is measured, which is unavoidable: nothing knows
    /// which mods eat tick time until one has been profiled.
    /// <para>Only once attribution is actually running, which is also what keeps a server that
    /// never switches it on free of four families that would never move. Called again by the
    /// command that switches it on, so the families reach the wire there too.</para></remarks>
    public void Seed()
    {
        if (attribution is not { Enabled: true })
        {
            return;
        }

        attributionTicks.Add(0);
        attributionDropped.Add(0);
        foreach (string modid in new[] { TickAttribution.Engine, TickAttribution.Unattributed })
        {
            KeyValuePair<string, object?> label = new("modid", modid);
            modTickSeconds.Add(0, label);
            modTickShare.Record(0, label);
        }
    }

    /// <summary>Whatever else is shutting down, the engine does not keep paying for a profiler that
    /// Pulse turned on and no longer reads.</summary>
    public void Stop()
    {
        if (attribution != null && resolveProfiler() is { } profiler)
        {
            profiler.Enabled = false;
        }
    }
}
