using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vintagestory.API.Common;

namespace Pulse;

/// <summary>Per-behavior, per-AI-task and per-entity-type tick timings on a Stratum server, end to
/// end: the instruments, the duty cycle that leases Stratum's recording for a burst of ticks, and the
/// fold of what a burst recorded into what each series gained.</summary>
/// <remarks>Split across two files the way <see cref="AttributionMetrics"/> is: this one is
/// everything a unit test can drive, and <c>StratumTimingsMetrics.Server.cs</c> is the lookup of
/// Stratum's type on a live server and the config reload, which only a live server ever executes and
/// which carries the coverage exclusion because of that. Stratum itself is reached through the two
/// delegates of a <see cref="StratumTimingsSource"/> this class is handed, never found, so the whole
/// bound path runs in a test against fakes.
/// <para>A burst maps onto the steps of the <see cref="DutyCycle"/> like this. <c>Start</c> asks
/// Stratum to record: the recording begins when the request returns, part-way through that tick.
/// <c>WarmUp</c> takes the first snapshot of the accumulator's totals, once recording is on and the
/// tick it began in is behind it, so what is read describes whole ticks from here on. The samples in
/// between cost nothing: Stratum records on its own, and this class does not even look. The last
/// sample takes the second snapshot, lets go of the lease, which stops the recording, and folds the
/// difference of the two snapshots into the series. That window is exactly
/// <see cref="DutyCycle.BurstTicks"/> tick cycles, which is what
/// <c>pulse_stratum_timed_ticks_total</c> counts, so the sampled seconds can be divided by the ticks
/// they came from.</para>
/// <para>Whatever is left holding the lease is let go of on every way out: the burst ends, a
/// reload drops it, something throws, and the mod stops. A lease that is never released would keep
/// Stratum recording for the rest of its run.</para>
/// <para>The instruments exist only when Stratum's contract is bound. On any other server none of the
/// five is registered, so nothing is served, and an instrument nothing has recorded into is not a
/// series: a server with the block off serves what it served before. Main thread only.</para></remarks>
internal sealed partial class StratumTimingsMetrics
{
    private const string AbsentNotification =
        "Pulse cannot serve Stratum's entity timings here: StratumTimings.Enabled is set in pulse.json, "
        + "but they need a Stratum server and this is not one. Every other metric is unaffected.";

    private const string UnreadableWarning =
        "Pulse cannot read Stratum's entity timings: {0}. They are not served on this server; every "
        + "other metric is unaffected.";

    private const string GiveUpWarning =
        "Pulse could not read Stratum's entity timings ({0}). They are off for the rest of this run and "
        + "their families stop updating; every other metric is unaffected.";

    private const string BehaviorWalkWarning =
        "Pulse could not read the behaviors of the loaded entities ({0}). The Stratum timings carry on "
        + "from the class registry, which maps only the behaviors whose name is their registration "
        + "code: the modid of the rest reads unattributed.";

    private const string OverflowWarning =
        "Pulse already serves {1} series of Stratum {0} timings, the most it keeps for that family. What "
        + "comes after is added to series whose {2} label reads other.";

    private const string StartedNotification =
        "Pulse reads Stratum's entity timings: bursts of {0} ticks every {1}s.";

    private readonly StratumTimingsSource? source;
    private readonly string? unavailable;
    private readonly ModOwners owners;
    private readonly Action<ModOwners> walkBehaviors;
    private readonly ILogger logger;
    private readonly Families? families;
    private readonly DutyCycle cycle;

    /// <summary>Built once and kept for the life of the server, across every reload. The cap on each
    /// family only bounds what is served because the fold remembers which label sets it has admitted
    /// and which families have spilled over: a fold made again on a reload would admit another set of
    /// series while the aggregator went on serving the old ones.</summary>
    private readonly StratumFold fold;

    /// <summary>Two lists, because a snapshot clears the one it fills.</summary>
    private readonly List<(string Key, long Ticks, long Calls)> start = [];
    private readonly List<(string Key, long Ticks, long Calls)> end = [];

    private IDisposable? lease;

    /// <summary>Set once something Stratum-side has thrown: the feature is off for the rest of the
    /// run, whatever a reload says.</summary>
    private bool gaveUp;

    /// <summary>Set once the behavior walk has thrown, after which it is not tried again: one
    /// warning, not one per burst for the rest of the run.</summary>
    private bool walkFailed;

    /// <summary>Whether the line that says Stratum's timings cannot be served here has been written.
    /// It is a fact about the server, not about a reload, so it is said once per run.</summary>
    private bool unavailableSaid;

    /// <summary>Creates the duty cycle and, when Stratum is bound, the instruments it publishes to.
    /// What a unit test calls to drive the bound path with fake delegates, and the one constructor
    /// the live-server <see cref="Create"/> goes through.</summary>
    /// <param name="source">Stratum's reading contract, bound, or null when this is not a Stratum
    /// server or not one Pulse can read.</param>
    /// <param name="unavailable">Why <paramref name="source"/> is null when Stratum is there but not
    /// readable: the reason the binder gave. Null when Stratum is simply absent.</param>
    /// <param name="owners">Which mod ships which behavior, shared with attribution.</param>
    /// <param name="walkBehaviors">Teaches <paramref name="owners"/> what the behaviors of the loaded
    /// entities mark with. Run right before each fold, on the main thread, whether or not attribution
    /// is: that is the only thing that learns the names whose behavior class is registered under
    /// another code.</param>
    internal StratumTimingsMetrics(
        Meter meter,
        StratumTimingsConfig config,
        StratumTimingsSource? source,
        string? unavailable,
        ModOwners owners,
        Action<ModOwners> walkBehaviors,
        ILogger logger)
    {
        this.source = source;
        this.unavailable = unavailable;
        this.owners = owners;
        this.walkBehaviors = walkBehaviors;
        this.logger = logger;
        fold = new StratumFold(owners.Owner);

        if (source != null)
        {
            families = new Families(meter);
        }

        cycle = new DutyCycle(config.BurstTicks, config.IntervalSeconds, config.Enabled && source != null);

        // Said at boot only: a reload says what it did in its reply, which is where an admin who
        // runs it from chat reads it.
        if (cycle.Enabled)
        {
            logger.Notification(StartedNotification, cycle.BurstTicks, cycle.IntervalSeconds);
        }

        SayUnavailable(config.Enabled);
    }

    /// <summary>Applies a block of the config to the running feature: a reload. Returns the sentence
    /// the reload's reply says about it, or null when there is nothing to say: the block is off and
    /// was not running.</summary>
    /// <remarks>The burst in progress is dropped, whatever the new block says, and the lease it held
    /// is let go of. A cycle restarted with a lease still out would request a second one at its next
    /// burst, and the first would never be released. A feature that has given up stays off: the
    /// block cannot talk it back into reading a Stratum that threw. On a server that cannot serve the
    /// block, the first reload that asks for it is also the first time the log says so, once.</remarks>
    public string? Apply(StratumTimingsConfig config)
    {
        bool wasRunning = cycle.Enabled;
        ReleaseLeaseBestEffort();
        cycle.Apply(config.Enabled && source != null && !gaveUp, config.BurstTicks, config.IntervalSeconds);
        SayUnavailable(config.Enabled);
        Seed();
        return Reply(config.Enabled, wasRunning);
    }

    /// <summary>Puts the one counter that has no labels on the wire at zero, so that a panel can tell
    /// a feature that is timing nothing from one that is not there. Only while it is actually
    /// running.</summary>
    /// <remarks>The labelled families cannot be seeded: nothing knows which behaviors, tasks and
    /// entity types a burst will see until one has run.</remarks>
    public void Seed()
    {
        if (families != null && cycle.Enabled)
        {
            families.TimedTicks.Add(0);
        }
    }

    /// <summary>Advances the duty cycle by one tick, and gives the feature up for good if Stratum
    /// ever throws.</summary>
    public void Tick(double elapsedSeconds)
    {
        try
        {
            Run(cycle.OnTick(elapsedSeconds));
        }
        catch (Exception e)
        {
            GiveUp(e);
        }
    }

    /// <summary>Whatever else is shutting down, Stratum does not keep recording for a reader that is
    /// gone.</summary>
    public void Stop() => ReleaseLeaseBestEffort();

    private void Run(DutyStep step)
    {
        switch (step)
        {
            case DutyStep.Start:
                lease = source!.Request();
                break;
            case DutyStep.WarmUp:
                source!.Snapshot(start);
                break;
            case DutyStep.LastSample:
                Finish();
                break;
        }
    }

    private void Finish()
    {
        source!.Snapshot(end);

        // Recording stops here. Everything below is arithmetic on two lists, so it is not worth a
        // moment of Stratum's overhead.
        ReleaseLease();

        // Right before the fold, and not at the start of the burst: an entity that spawned while it
        // ran, and carries a behavior no entity has shown yet, is credited to its mod in this burst
        // rather than the next.
        LearnBehaviorOwners();

        Publish(fold.Fold(start, end));
    }

    private void LearnBehaviorOwners()
    {
        if (walkFailed)
        {
            return;
        }

        try
        {
            walkBehaviors(owners);
        }
        catch (Exception e)
        {
            // Losing the walk costs precision in the modid label, not the feature.
            walkFailed = true;
            logger.Warning(BehaviorWalkWarning, e.Message);
        }
    }

    private void Publish(StratumBurst burst)
    {
        Families served = families!;
        served.TimedTicks.Add(cycle.BurstTicks);

        foreach (BehaviorDelta delta in burst.Behaviors)
        {
            served.BehaviorSeconds.Add(
                delta.Seconds,
                new TagList
                {
                    { "category", delta.Category },
                    { "behavior", delta.Behavior },
                    { "threadsafe", delta.ThreadSafe },
                    { "modid", delta.Modid },
                });
        }

        foreach (TaskDelta delta in burst.Tasks)
        {
            served.TaskSeconds.Add(
                delta.Seconds, new TagList { { "category", delta.Category }, { "task", delta.Task } });
        }

        foreach (EntityDelta delta in burst.Entities)
        {
            KeyValuePair<string, object?> type = new("type", delta.Type);
            served.EntitySeconds.Add(delta.Seconds, type);
            served.EntityTicks.Add(delta.Calls, type);
        }

        foreach (StratumFamily family in burst.NewlyOverflowed)
        {
            (string noun, string label) = family switch
            {
                StratumFamily.Behavior => ("behavior", "behavior"),
                StratumFamily.AiTask => ("AI task", "task"),
                _ => ("entity type", "type"),
            };
            logger.Warning(OverflowWarning, noun, StratumFold.SeriesCap, label);
        }
    }

    /// <summary>The first time the block is wanted on a server that cannot serve it, says why not.</summary>
    private void SayUnavailable(bool wanted)
    {
        if (!wanted || source != null || unavailableSaid)
        {
            return;
        }

        unavailableSaid = true;
        if (unavailable == null)
        {
            logger.Notification(AbsentNotification);
        }
        else
        {
            logger.Warning(UnreadableWarning, unavailable);
        }
    }

    /// <summary>What a reload says about the block, now that it has been applied: that it is on, or why
    /// a block that is wanted is not, or that it was running and is off. Nothing for a block that is
    /// off and was not running, which keeps the reply of a server that never used it the reply it
    /// always was.</summary>
    private string? Reply(bool wanted, bool wasRunning)
    {
        if (cycle.Enabled)
        {
            return PulseCommands.StratumTimingsOn(cycle.BurstTicks, cycle.IntervalSeconds);
        }

        if (!wanted)
        {
            return wasRunning ? PulseCommands.StratumTimingsOff : null;
        }

        // Wanted and not running: there is no Stratum to read, or there was and it threw.
        if (source != null)
        {
            return PulseCommands.StratumTimingsGivenUp;
        }

        return unavailable == null
            ? PulseCommands.StratumTimingsAbsent
            : PulseCommands.StratumTimingsUnreadable(unavailable);
    }

    /// <summary>Stratum threw: let go of the lease if one is out, say so once, and stay off.</summary>
    private void GiveUp(Exception e)
    {
        ReleaseLeaseBestEffort();
        gaveUp = true;
        cycle.Apply(false, cycle.BurstTicks, cycle.IntervalSeconds);
        logger.Warning(GiveUpWarning, e.Message);
    }

    /// <summary>Lets go of the lease, if one is held. A lease that cannot be released throws, which a
    /// burst that ends normally counts as Stratum failing.</summary>
    private void ReleaseLease()
    {
        IDisposable? held = lease;
        lease = null;
        held?.Dispose();
    }

    /// <summary>The same, for the ways out that have no one to tell: a reload, the give-up path and the
    /// mod stopping. The failure that got the give-up here may be Stratum itself, and a second one
    /// must not escape the catch block it is called from.</summary>
    private void ReleaseLeaseBestEffort()
    {
        try
        {
            ReleaseLease();
        }
        catch
        {
            // Stratum's own code failed to release a lease; there is nothing more Pulse can do.
        }
    }

    /// <summary>The five counters, registered only when Stratum's contract is bound.</summary>
    private sealed class Families(Meter meter)
    {
        public Counter<double> BehaviorSeconds { get; } = meter.CreateCounter<double>(
            "pulse_stratum_behavior_tick_seconds_total", "s",
            "Seconds spent in one entity behavior's tick on a Stratum server while Pulse was timing. Sampled. Thread-safe behaviors are summed across the physics threads.");

        public Counter<double> TaskSeconds { get; } = meter.CreateCounter<double>(
            "pulse_stratum_ai_task_tick_seconds_total", "s",
            "Seconds spent running one AI task while Pulse was timing. Sampled.");

        public Counter<double> EntitySeconds { get; } = meter.CreateCounter<double>(
            "pulse_stratum_entity_tick_seconds_total", "s",
            "Seconds spent in the whole tick of entities of one type while Pulse was timing. Sampled. Includes their main-thread behaviors and AI, not their thread-safe behaviors.");

        public Counter<long> EntityTicks { get; } = meter.CreateCounter<long>(
            "pulse_stratum_entity_ticks_total", "{tick}",
            "Entity ticks run for entities of one type while Pulse was timing, so seconds per entity tick is one division.");

        public Counter<long> TimedTicks { get; } = meter.CreateCounter<long>(
            "pulse_stratum_timed_ticks_total", "{tick}",
            "Server ticks Pulse timed, so the sampled seconds can be normalised per tick.");
    }
}
