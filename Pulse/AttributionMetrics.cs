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
/// about 200 lines behind one config flag, taking its field count from 31 to 23 (nine removed here,
/// one added back as the single reference to this class). Split across two files: this one is
/// everything a unit test can drive, and <c>AttributionMetrics.Server.cs</c> is the construction,
/// the <c>/pulse</c> command and the reload path, which only a live server ever executes and which
/// carries the coverage exclusion because of that, the same reason <c>PulseModSystem</c> and the two
/// probes carry it, not because of anything this class reflects into.
/// <para>The tick-processing core reads the engine's frame profiler through a resolver function,
/// walks tick listeners through a plain delegate, and logs through a sink that takes the warning
/// template and the detail as two arguments rather than one pre-selected template, instead of
/// reaching into <see cref="ICoreServerAPI"/> directly. That is what makes the unprimed-tick give-up
/// path, a failed listener walk, and the on/off/status command transitions drivable from a unit test
/// without a live server, and what makes it possible to assert which template a failure logs
/// through, not just that it logged something.</para></remarks>
internal sealed partial class AttributionMetrics
{
    private const string AttributionWarning =
        "Pulse could not read the engine's frame profiler ({0}). Per-mod tick attribution is off "
        + "for the rest of this run and its families stop updating; every other metric is "
        + "unaffected.";

    private const string ListenerWalkWarning =
        "Pulse could not read the engine's tick listener lists ({0}). Per-mod attribution carries "
        + "on from the mod loader's own type list, which maps fewer marks: the rest report as "
        + "unattributed.";

    /// <summary>How many ticks attribution waits for the primed profiler to complete one, before
    /// concluding that priming never took. Roughly half a minute at the default tick rate.</summary>
    private const int UnprimedTickLimit = 1000;

    private readonly Func<FrameProfilerUtil?> resolveProfiler;
    private readonly Action<string, string> warn;

    // Not readonly: the production constructor cannot close over its own attributionProbe field
    // from inside the chained constructor call below (an object initialiser argument list runs
    // before `this` exists), so it chains with a placeholder here and replaces it in its body,
    // once attributionProbe is a field it can actually reference.
    private Action<ModOwners> walkListeners;
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

    /// <summary>Creates the duty cycle and the instruments it publishes to, with the profiler
    /// resolver, the listener walk and the warning sink supplied directly rather than read off a
    /// live server: what a unit test calls to drive the unprimed-tick give-up path, a failed
    /// listener walk, and the on/off/status command transitions without one.</summary>
    internal AttributionMetrics(
        Meter meter,
        AttributionConfig config,
        Func<FrameProfilerUtil?> resolveProfiler,
        Action<ModOwners> walkListeners,
        Action<string, string> warn)
    {
        this.resolveProfiler = resolveProfiler;
        this.walkListeners = walkListeners;
        this.warn = warn;

        // A real, empty table rather than null: nothing but a live server's ModLoader walk can
        // populate it, but Owner still has to be a callable delegate the moment a primed tick asks
        // for one, in a test as much as on a server that has not walked any mods yet.
        owners = new ModOwners(_ => null);

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
                warn(AttributionWarning, "the engine's profiler never completed a primed tick");
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
            warn(AttributionWarning, e.Message);
        }
    }

    /// <summary>Re-reads which mod owns which tick listener, once per burst.</summary>
    /// <remarks>Once per burst rather than once at startup because mods register and drop listeners
    /// as the world runs. Its own catch: losing the walk costs precision in the map, not the
    /// feature, which is why it logs through <see cref="ListenerWalkWarning"/> and leaves
    /// <see cref="attribution"/> running rather than giving up on it the way <see cref="Tick"/>
    /// does when the profiler itself is unreadable.</remarks>
    private void RefreshOwners()
    {
        try
        {
            walkListeners(owners!);
        }
        catch (Exception e)
        {
            attributionProbe = null;
            warn(ListenerWalkWarning, e.Message);
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
