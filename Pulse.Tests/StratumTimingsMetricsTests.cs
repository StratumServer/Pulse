using System.Diagnostics;
using System.Diagnostics.Metrics;
using Pulse.Tests.Fakes;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Xunit;

namespace Pulse.Tests;

/// <summary>The whole feature against a fake accumulator: the instruments, the lease a burst takes and
/// every way it lets go of it, the ticks a burst is divided by, the table of owners it shares, and what
/// it does when Stratum throws. The accumulator is an instance behind the two delegates of a
/// <see cref="StratumTimingsSource"/>, built per test, and never the static fake the binder's own tests
/// drive: xUnit runs classes in parallel, and two of them moving one static accumulator would see each
/// other's totals.</summary>
public class StratumTimingsMetricsTests
{
    private const string AbsentNotification =
        "StratumTimings.Enabled is set in pulse.json, but per-behavior timings need a Stratum server "
        + "and this is not one, so Pulse does not serve them. Every other metric is unaffected.";

    private const string GiveUpWarning =
        "Pulse could not read Stratum's entity timings ({0}). They are off for the rest of this run and "
        + "their families stop updating; every other metric is unaffected.";

    private const string Physics = "entity.behavior.threadsafe.creatures.done-behavior-entitycontrolledphysics";

    private static StratumTimingsConfig Config(bool enabled = true, int burstTicks = 5)
        => new() { Enabled = enabled, BurstTicks = burstTicks, IntervalSeconds = 1 };

    /// <summary>The totals of Stratum's accumulator behind the two delegates of a source, which a test
    /// moves between the ticks, and a count of what the feature asked of it. A lease counts every
    /// release it is given, so letting go of one twice shows.</summary>
    private sealed class FakeStratum
    {
        private readonly Dictionary<string, (long Ticks, long Calls)> totals = [];

        public int Requested { get; private set; }

        public int Released { get; private set; }

        public int Snapshots { get; private set; }

        public Exception? RequestThrows { get; set; }

        public Exception? SnapshotThrows { get; set; }

        public Exception? ReleaseThrows { get; set; }

        /// <summary>Runs as each snapshot is taken, before it is read.</summary>
        public Action? OnSnapshot { get; set; }

        public StratumTimingsSource Source => new(Request, Snapshot);

        /// <summary>Sets one key's cumulative totals, in whole seconds, which are exact whatever the
        /// stopwatch's frequency is.</summary>
        public void Set(string key, long seconds, long calls) => totals[key] = (seconds * Stopwatch.Frequency, calls);

        private IDisposable Request()
        {
            if (RequestThrows != null)
            {
                throw RequestThrows;
            }

            Requested++;
            return new Lease(this);
        }

        private void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
            if (SnapshotThrows != null)
            {
                throw SnapshotThrows;
            }

            Snapshots++;
            OnSnapshot?.Invoke();
            into.Clear();
            foreach (KeyValuePair<string, (long Ticks, long Calls)> entry in totals)
            {
                into.Add((entry.Key, entry.Value.Ticks, entry.Value.Calls));
            }
        }

        private sealed class Lease(FakeStratum owner) : IDisposable
        {
            public void Dispose()
            {
                owner.Released++;
                if (owner.ReleaseThrows != null)
                {
                    throw owner.ReleaseThrows;
                }
            }
        }
    }

    /// <summary>A feature, the aggregator that serves it, and everything around it a test reads back.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly int burstTicks;

        public Rig(
            StratumTimingsConfig? config = null,
            bool bound = true,
            string? unavailable = null,
            Action<ModOwners>? walk = null,
            ModOwners? owners = null)
        {
            config ??= Config();
            burstTicks = config.BurstTicks;
            Meter = new Meter($"Pulse.Test.StratumTimingsMetrics.{Guid.NewGuid():N}");
            Aggregator = new MetricsAggregator(Meter.Name);
            Owners = owners ?? new ModOwners(_ => null);
            Metrics = new StratumTimingsMetrics(
                Meter, config, bound ? Stratum.Source : null, unavailable, Owners, walk ?? (_ => { }), Logger);
        }

        public Meter Meter { get; }

        public MetricsAggregator Aggregator { get; }

        public FakeStratum Stratum { get; } = new();

        public FakeLogger Logger { get; } = new();

        public ModOwners Owners { get; }

        public StratumTimingsMetrics Metrics { get; }

        public void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Metrics.Tick(1.0);
            }
        }

        /// <summary>One whole burst of the config's length, with the interval of one second the
        /// helper's ticks pass at once: a start tick, the warm-up, the samples, and the last sample.
        /// <paramref name="atBaseline"/> runs after the start tick and before the warm-up tick that
        /// takes the first snapshot, <paramref name="atEnd"/> before the last sample that takes the
        /// second.</summary>
        public void RunBurst(Action? atBaseline = null, Action? atEnd = null)
        {
            Tick(1);
            atBaseline?.Invoke();
            Tick(1);
            Tick(burstTicks - 1);
            atEnd?.Invoke();
            Tick(1);
        }

        public IReadOnlyList<MetricSample> Samples() => Aggregator.Collect();

        public void Dispose()
        {
            Aggregator.Dispose();
            Meter.Dispose();
        }
    }

    /// <summary>The value of the one series of a family that carries exactly these labels.</summary>
    private static double Value(IReadOnlyList<MetricSample> samples, string name, params (string Key, string Value)[] labels)
        => Assert.Single(
            samples,
            sample => sample.Name == name
                && sample.Labels.Select(label => (label.Key, label.Value)).Order().SequenceEqual(labels.Order())).Value;

    private static List<MetricSample> Family(IReadOnlyList<MetricSample> samples, string name)
        => [.. samples.Where(sample => sample.Name == name)];

    /// <summary>What a meter has registered, which is not what it has served: an instrument nothing
    /// has recorded into is not a series, so only the listener can tell one that was never created
    /// from one that was.</summary>
    private static List<(string Name, string? Unit, string? Description)> Registered(Meter meter)
    {
        List<(string Name, string? Unit, string? Description)> found = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter == meter)
            {
                found.Add((instrument.Name, instrument.Unit, instrument.Description));
            }
        };
        listener.Start();
        return found;
    }

    private static (EnumLogType Type, string Message) Only(FakeLogger logger) => Assert.Single(logger.Entries);

    // The instruments.

    /// <summary>Names, units and help text are not decoration: the unit feeds the writer's own
    /// translation into a name suffix, and the help becomes the HELP line a panel author reads.</summary>
    [Fact]
    public void Constructor_Registers_TheFiveFamilies_UnderTheirNamesUnitsAndHelp_WhenStratumIsBound()
    {
        using Rig rig = new();

        Assert.Equal(
            [
                (
                    "pulse_stratum_behavior_tick_seconds_total", "s",
                    "Seconds spent in one entity behavior's tick on a Stratum server while Pulse was timing. Sampled. Thread-safe behaviors are summed across the physics threads."
                ),
                (
                    "pulse_stratum_ai_task_tick_seconds_total", "s",
                    "Seconds spent running one AI task while Pulse was timing. Sampled."
                ),
                (
                    "pulse_stratum_entity_tick_seconds_total", "s",
                    "Seconds spent in the whole tick of entities of one type while Pulse was timing. Sampled. Includes their main-thread behaviors and AI, not their thread-safe behaviors."
                ),
                (
                    "pulse_stratum_entity_ticks_total", "{tick}",
                    "Entity ticks run for entities of one type while Pulse was timing, so seconds per entity tick is one division."
                ),
                (
                    "pulse_stratum_timed_ticks_total", "{tick}",
                    "Server ticks Pulse timed, so the sampled seconds can be normalised per tick."
                ),
            ],
            Registered(rig.Meter));
    }

    /// <summary>On a server that is not Stratum, or is one Pulse cannot read, not one of the five is
    /// registered, which is what keeps it serving exactly what it served before. Serving nothing is
    /// not enough to show it: an instrument that was created and never recorded into serves nothing
    /// either.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("this Stratum predates the reading contract")]
    public void Constructor_Registers_NoFamily_WhenStratumIsNotBound(string? unavailable)
    {
        using Rig rig = new(bound: false, unavailable: unavailable);

        Assert.Empty(Registered(rig.Meter));
    }

    // A whole burst.

    /// <summary>What a burst adds, end to end through the lease, the two snapshots, the fold and the
    /// instruments: a thread-safe behavior with the mod the walk learned it for, a key that only
    /// showed up during the burst, two codes of one entity type summed into it, and a key the burst
    /// added nothing to, which is not a series.</summary>
    [Fact]
    public void ABurst_Publishes_WhatItAdded_ToTheFiveFamilies()
    {
        ModOwners owners = new(_ => null);
        owners.AddSystem("mymod", typeof(StratumTimingsMetricsTests));
        using Rig rig = new(
            owners: owners,
            walk: table => table.LearnBehavior(
                TickAttribution.BehaviorPrefix + "entitycontrolledphysics", typeof(StratumTimingsMetricsTests)));

        rig.RunBurst(
            atBaseline: () =>
            {
                rig.Stratum.Set(Physics, 2, 20);
                rig.Stratum.Set("entity.behavior.players.health", 1, 10);
                rig.Stratum.Set("entity.ai.creatures.task.idle", 1, 9);
                rig.Stratum.Set("entity.type.wolf-eurasian-adult-male", 4, 40);
            },
            atEnd: () =>
            {
                rig.Stratum.Set(Physics, 5, 50);
                rig.Stratum.Set("entity.ai.creatures.task.idle", 3, 19);
                rig.Stratum.Set("entity.type.wolf-eurasian-adult-male", 10, 100);
                rig.Stratum.Set("entity.type.wolf-eurasian-adult-female", 2, 8);
                rig.Stratum.Set("entity.type.hare-eurasian-adult-male", 1, 5);
            });

        IReadOnlyList<MetricSample> samples = rig.Samples();

        Assert.Equal(
            3,
            Value(
                samples, "pulse_stratum_behavior_tick_seconds_total",
                ("category", "creatures"), ("behavior", "entitycontrolledphysics"), ("threadsafe", "true"), ("modid", "mymod")));
        Assert.Single(Family(samples, "pulse_stratum_behavior_tick_seconds_total"));
        Assert.Equal(
            2, Value(samples, "pulse_stratum_ai_task_tick_seconds_total", ("category", "creatures"), ("task", "idle")));
        Assert.Equal(8, Value(samples, "pulse_stratum_entity_tick_seconds_total", ("type", "wolf")));
        Assert.Equal(68, Value(samples, "pulse_stratum_entity_ticks_total", ("type", "wolf")));
        Assert.Equal(1, Value(samples, "pulse_stratum_entity_tick_seconds_total", ("type", "hare")));
        Assert.Equal(5, Value(samples, "pulse_stratum_entity_ticks_total", ("type", "hare")));
        Assert.Equal(5, Value(samples, "pulse_stratum_timed_ticks_total"));
        Assert.All(samples, sample => Assert.Equal(MetricKind.Counter, sample.Kind));
    }

    /// <summary>The warm-up tick is the one that takes the baseline: what the start tick recorded,
    /// partly, after the lease was taken, is not what the burst timed.</summary>
    [Fact]
    public void ABurst_TakesItsBaseline_OnTheWarmUpTick_NotOnTheStartTick()
    {
        using Rig rig = new();

        rig.RunBurst(
            atBaseline: () => rig.Stratum.Set("entity.type.wolf-a", 5, 50),
            atEnd: () => rig.Stratum.Set("entity.type.wolf-a", 8, 80));

        IReadOnlyList<MetricSample> samples = rig.Samples();
        Assert.Equal(3, Value(samples, "pulse_stratum_entity_tick_seconds_total", ("type", "wolf")));
        Assert.Equal(30, Value(samples, "pulse_stratum_entity_ticks_total", ("type", "wolf")));
    }

    /// <summary>The ticks a burst is divided by are the tick cycles between its two snapshots, counted
    /// here from the server ticks that went by, and not from the config that asked for them. With a
    /// lease, recording starts when the request returns, so a baseline taken on the start tick would
    /// have made the window one longer, and every per-tick query read ten percent low at the
    /// default of ten.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void ABurst_Counts_ExactlyTheTickCyclesBetweenItsTwoSnapshots(int burstTicks)
    {
        using Rig rig = new(Config(burstTicks: burstTicks));
        int ticks = 0;
        List<int> snapshotsAt = [];
        rig.Stratum.OnSnapshot = () => snapshotsAt.Add(ticks);

        for (int i = 0; i < burstTicks + 2; i++)
        {
            ticks++;
            rig.Metrics.Tick(1.0);
        }

        Assert.Equal(2, snapshotsAt.Count);
        int between = snapshotsAt[1] - snapshotsAt[0];
        Assert.Equal(burstTicks, between);
        Assert.Equal(between, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
    }

    [Fact]
    public void ABurst_AddsItsTicks_ToWhatTheLastOneCounted()
    {
        using Rig rig = new();

        rig.RunBurst();
        rig.RunBurst();

        Assert.Equal(10, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
    }

    // The lease.

    [Fact]
    public void Tick_RequestsOneLeaseWhereABurstStarts_AndReleasesItWhereTheBurstEnds()
    {
        using Rig rig = new();

        rig.Tick(1);
        Assert.Equal((1, 0, 0), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));

        // The warm-up and the samples: the baseline is read, and nothing else is asked of Stratum.
        rig.Tick(5);
        Assert.Equal((1, 0, 1), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));

        rig.Tick(1);
        Assert.Equal((1, 1, 2), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));

        // Idle again: not one more lease until the next interval has passed.
        rig.Metrics.Tick(0.1);
        Assert.Equal((1, 1, 2), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));
    }

    [Fact]
    public void Tick_AsksStratumForNothing_WhileTheBlockIsOff()
    {
        using Rig rig = new(Config(enabled: false));

        rig.Tick(50);

        Assert.Equal((0, 0, 0), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));
        Assert.Empty(rig.Samples());
    }

    [Fact]
    public void Tick_AsksStratumForNothing_WhenItIsNotBound()
    {
        using Rig rig = new(bound: false);

        rig.Tick(50);

        Assert.Equal(0, rig.Stratum.Requested);
        Assert.Empty(rig.Samples());
    }

    /// <summary>A reload that switches the block off lands part-way through a burst: the lease goes
    /// back, the burst is dropped and nothing of it is published, and the feature stays off.</summary>
    [Fact]
    public void Apply_Off_MidBurst_ReleasesTheLease_AndDropsTheBurst()
    {
        using Rig rig = new();
        rig.Tick(2);
        rig.Stratum.Set("entity.type.wolf-a", 3, 30);
        Assert.Equal((1, 0), (rig.Stratum.Requested, rig.Stratum.Released));

        rig.Metrics.Apply(Config(enabled: false));

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        rig.Tick(20);
        Assert.Equal((1, 1, 1), (rig.Stratum.Requested, rig.Stratum.Released, rig.Stratum.Snapshots));
        Assert.Empty(rig.Samples());
    }

    /// <summary>A cycle restarted with a lease still out would take a second one at its next burst
    /// and never give the first back, so Stratum would record for the rest of its run. The burst that
    /// was running is dropped, whatever the new block says, and counts for nothing.</summary>
    [Fact]
    public void Apply_WithANewCycle_MidBurst_ReleasesTheLease_AndTheNextBurstTakesOneOfItsOwn()
    {
        using Rig rig = new();
        rig.Tick(3);

        rig.Metrics.Apply(Config(burstTicks: 3));

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        rig.Tick(5);
        Assert.Equal((2, 2), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Equal(3, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
    }

    /// <summary>The block is read at boot and again on a reload, so a server that booted with it off
    /// can have it switched on without a restart, and starts timing from then.</summary>
    [Fact]
    public void Apply_On_StartsTheFeature_OnAServerThatBootedWithItOff()
    {
        using Rig rig = new(Config(enabled: false));
        rig.Tick(10);
        Assert.Equal(0, rig.Stratum.Requested);

        rig.Metrics.Apply(Config(burstTicks: 4));
        rig.Tick(6);

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Equal(4, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
    }

    /// <summary>Applying a block to a server that cannot serve it switches nothing on.</summary>
    [Fact]
    public void Apply_On_StartsNothing_WhenStratumIsNotBound()
    {
        using Rig rig = new(Config(enabled: false), bound: false);

        rig.Metrics.Apply(Config());
        rig.Tick(20);

        Assert.Equal(0, rig.Stratum.Requested);
        Assert.Empty(rig.Samples());
    }

    [Fact]
    public void Stop_ReleasesTheLease_OfABurstInProgress()
    {
        using Rig rig = new();
        rig.Tick(3);

        rig.Metrics.Stop();

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
    }

    [Fact]
    public void Stop_ReleasesNothing_WhenThereIsNoBurstInProgress()
    {
        using Rig rig = new();

        rig.Metrics.Stop();

        Assert.Equal((0, 0), (rig.Stratum.Requested, rig.Stratum.Released));
    }

    /// <summary>The mod is stopping, so a lease that cannot be released has nowhere to be reported
    /// to, and must not stop the rest of the shutdown.</summary>
    [Fact]
    public void Stop_DoesNotThrow_WhenStratumCannotReleaseTheLease()
    {
        using Rig rig = new();
        rig.Tick(3);
        rig.Stratum.ReleaseThrows = new InvalidOperationException("the lease is gone");

        Assert.Null(Record.Exception(rig.Metrics.Stop));
        Assert.DoesNotContain(rig.Logger.Entries, entry => entry.Type == EnumLogType.Warning);
    }

    // Stratum throws.

    /// <summary>Whatever Stratum does at run time, the tick listener never sees it: one warning, the
    /// feature off for the rest of the run, the families left at their last value.</summary>
    [Fact]
    public void Tick_GivesUpForGood_WhenTheRequestThrows()
    {
        using Rig rig = new();
        rig.Stratum.RequestThrows = new InvalidOperationException("no accumulator");

        Exception? escaped = Record.Exception(() => rig.Tick(1));

        Assert.Null(escaped);
        (EnumLogType type, string message) = Assert.Single(
            rig.Logger.Entries, entry => entry.Message.Contains("could not read Stratum's entity timings"));
        Assert.Equal(EnumLogType.Warning, type);
        Assert.Equal(string.Format(GiveUpWarning, "no accumulator"), message);

        // Off for the run, however Stratum behaves from here on and whatever a reload says, which does
        // not even claim to have started it.
        rig.Stratum.RequestThrows = null;
        int said = rig.Logger.Entries.Count;
        rig.Metrics.Apply(Config());
        rig.Tick(50);
        Assert.Equal(0, rig.Stratum.Requested);
        Assert.Equal(said, rig.Logger.Entries.Count);
        Assert.Single(rig.Logger.Entries, entry => entry.Type == EnumLogType.Warning);
    }

    [Fact]
    public void Tick_GivesUp_AndReleasesTheLease_WhenTheBaselineSnapshotThrows()
    {
        using Rig rig = new();
        rig.Tick(1);
        rig.Stratum.SnapshotThrows = new InvalidOperationException("no snapshot");

        rig.Tick(1);

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Single(rig.Logger.Entries, entry => entry.Message == string.Format(GiveUpWarning, "no snapshot"));
    }

    [Fact]
    public void Tick_GivesUp_AndReleasesTheLease_WhenTheFinalSnapshotThrows()
    {
        using Rig rig = new();

        rig.RunBurst(atEnd: () => rig.Stratum.SnapshotThrows = new InvalidOperationException("no snapshot"));

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Single(rig.Logger.Entries, entry => entry.Message == string.Format(GiveUpWarning, "no snapshot"));
        Assert.Empty(rig.Samples());
    }

    /// <summary>A lease that cannot be released is Stratum failing too: the burst gives up with a
    /// warning instead of publishing numbers over a recording that is still running.</summary>
    [Fact]
    public void Tick_GivesUp_WhenTheLeaseCannotBeReleased()
    {
        using Rig rig = new();
        rig.Stratum.ReleaseThrows = new InvalidOperationException("stuck");

        rig.RunBurst();

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Single(rig.Logger.Entries, entry => entry.Message == string.Format(GiveUpWarning, "stuck"));
        Assert.Empty(rig.Samples());
    }

    /// <summary>The lease is already back by the time the fold runs, so a fold that throws (here the
    /// class registry, which the owner lookup falls back on) does not release it a second time.</summary>
    [Fact]
    public void Tick_GivesUp_WithoutReleasingTwice_WhenTheFoldThrows()
    {
        ModOwners owners = new(_ => throw new InvalidOperationException("the registry is gone"));
        using Rig rig = new(owners: owners);

        rig.RunBurst(atEnd: () => rig.Stratum.Set("entity.behavior.players.health", 1, 10));

        Assert.Equal((1, 1), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Single(
            rig.Logger.Entries, entry => entry.Message == string.Format(GiveUpWarning, "the registry is gone"));
    }

    // The table of owners.

    /// <summary>The walk teaches the table which mod ships the behaviors the entities carry, and it
    /// runs before the burst is folded, not at the start of it: a behavior that only showed up on an
    /// entity spawned during the burst is credited to its mod in that burst. Once per burst, and with
    /// the table the feature was handed, which is the one attribution reads.</summary>
    [Fact]
    public void Tick_WalksTheBehaviors_OncePerBurst_BeforeFolding_WithTheTableItWasGiven()
    {
        ModOwners owners = new(_ => null);
        owners.AddSystem("mymod", typeof(StratumTimingsMetricsTests));
        List<ModOwners> walked = [];
        using Rig rig = new(
            owners: owners,
            walk: table =>
            {
                walked.Add(table);
                table.LearnBehavior(TickAttribution.BehaviorPrefix + "displayname", typeof(StratumTimingsMetricsTests));
            });

        rig.RunBurst(atEnd: () => rig.Stratum.Set("entity.behavior.players.displayname", 1, 10));

        Assert.Same(owners, Assert.Single(walked));
        Assert.Equal(
            1,
            Value(
                rig.Samples(), "pulse_stratum_behavior_tick_seconds_total",
                ("category", "players"), ("behavior", "displayname"), ("threadsafe", "false"), ("modid", "mymod")));

        rig.RunBurst();
        Assert.Equal(2, walked.Count);
    }

    /// <summary>The walk visits every loaded entity, so it is made where it pays: once, at the end of
    /// a burst, and not on the idle ticks or the samples in between. Nothing about it depends on
    /// attribution being on, or on its having started: the table is the feature's own as much as it is
    /// attribution's.</summary>
    [Fact]
    public void Tick_DoesNotWalk_OutsideTheEndOfABurst()
    {
        int walks = 0;
        using Rig rig = new(walk: _ => walks++);

        rig.Tick(6);
        Assert.Equal(0, walks);

        rig.Tick(1);
        Assert.Equal(1, walks);

        rig.Metrics.Tick(0.1);
        Assert.Equal(1, walks);
    }

    [Fact]
    public void Tick_CarriesOn_AndWarnsOnce_WhenTheBehaviorWalkThrows()
    {
        int walks = 0;
        using Rig rig = new(walk: _ =>
        {
            walks++;
            throw new InvalidOperationException("the entity table is gone");
        });

        rig.RunBurst(atEnd: () => rig.Stratum.Set("entity.behavior.players.health", 1, 10));
        rig.RunBurst(atEnd: () => rig.Stratum.Set("entity.behavior.players.health", 2, 20));

        (EnumLogType type, string message) = Assert.Single(rig.Logger.Entries, entry => entry.Type == EnumLogType.Warning);
        Assert.Equal(EnumLogType.Warning, type);
        Assert.Equal(
            "Pulse could not read the behaviors of the loaded entities (the entity table is gone). The "
                + "Stratum timings carry on from the class registry, which maps only the behaviors whose "
                + "name is their registration code: the modid of the rest reads unattributed.",
            message);

        // Not tried again, and nothing else lost: the feature is still running and still serving.
        Assert.Equal(1, walks);
        Assert.Equal((2, 2), (rig.Stratum.Requested, rig.Stratum.Released));
        Assert.Equal(
            2,
            Value(
                rig.Samples(), "pulse_stratum_behavior_tick_seconds_total",
                ("category", "players"), ("behavior", "health"), ("threadsafe", "false"), ("modid", "unattributed")));
    }

    /// <summary>One table, handed to both features: a behavior name attribution's walk learned is
    /// credited to its mod by the Stratum timings as well, with no walk of their own that knows it.</summary>
    [Fact]
    public void AttributionAndTheStratumTimings_ShareOneTableOfOwners()
    {
        ModOwners owners = new(_ => null);
        owners.AddSystem("mymod", typeof(StratumTimingsMetricsTests));
        FrameProfilerUtil profiler = new("test") { Enabled = true, PrevRootEntry = new ProfileEntryRange { ElapsedTicks = 1000 } };
        using Rig rig = new(owners: owners);
        AttributionMetrics attribution = new(
            rig.Meter,
            new AttributionConfig { Enabled = true, BurstTicks = 5, IntervalSeconds = 1 },
            () => profiler,
            _ => { },
            (_, _) => { },
            walkBehaviors: table => table.LearnBehavior(
                TickAttribution.BehaviorPrefix + "timeddespawn", typeof(StratumTimingsMetricsTests)),
            owners: owners);

        // The tick that starts attribution's burst is the one that makes it walk the behaviors.
        attribution.Tick(1.0);
        rig.RunBurst(atEnd: () => rig.Stratum.Set("entity.behavior.players.timeddespawn", 1, 10));

        Assert.Equal(
            1,
            Value(
                rig.Samples(), "pulse_stratum_behavior_tick_seconds_total",
                ("category", "players"), ("behavior", "timeddespawn"), ("threadsafe", "false"), ("modid", "mymod")));
    }

    // The cap.

    /// <summary>The cap only bounds what is served because the fold remembers what it admitted, and the
    /// aggregator never retires a series of a counter. A fold made again by a reload would admit another
    /// hundred while the old ones stayed on the wire.</summary>
    [Fact]
    public void Apply_KeepsTheFold_SoTheCapReachedBeforeAReloadStillHoldsAfterIt()
    {
        using Rig rig = new();

        // A hundred and one types in the first burst: the hundred that sort first get a series, and
        // the last one spills into other.
        rig.RunBurst(atEnd: () =>
        {
            for (int i = 0; i <= 100; i++)
            {
                rig.Stratum.Set($"entity.type.t{i:D3}-adult", 1, 1);
            }
        });
        rig.Metrics.Apply(Config());

        // A hundred more, all new, after the reload.
        rig.RunBurst(atEnd: () =>
        {
            for (int i = 0; i < 100; i++)
            {
                rig.Stratum.Set($"entity.type.u{i:D3}-adult", 1, 1);
            }
        });

        IReadOnlyList<MetricSample> samples = rig.Samples();
        List<MetricSample> types = Family(samples, "pulse_stratum_entity_tick_seconds_total");
        Assert.Equal(101, types.Count);
        Assert.Equal(101, Value(samples, "pulse_stratum_entity_tick_seconds_total", ("type", "other")));
        Assert.Equal(101, Value(samples, "pulse_stratum_entity_ticks_total", ("type", "other")));
        Assert.DoesNotContain(types, sample => sample.Labels.Any(label => label.Value == "u000"));
    }

    /// <summary>One line for the family, the first time it spills, and not another for the bursts that
    /// spill after it. Each of the three says which label it lumps into.</summary>
    [Fact]
    public void Tick_LogsOnce_ForEachFamilyThatSpillsOverItsCap()
    {
        using Rig rig = new();

        rig.RunBurst(atEnd: () =>
        {
            for (int i = 0; i <= 100; i++)
            {
                rig.Stratum.Set($"entity.type.t{i:D3}-adult", 1, 1);
                rig.Stratum.Set($"entity.ai.creatures.task.t{i:D3}", 1, 1);
                rig.Stratum.Set($"entity.behavior.players.t{i:D3}", 1, 1);
            }
        });
        rig.RunBurst(atEnd: () =>
        {
            for (int i = 0; i <= 100; i++)
            {
                rig.Stratum.Set($"entity.type.t{i:D3}-adult", 2, 2);
                rig.Stratum.Set($"entity.type.v{i:D3}-adult", 1, 1);
            }
        });

        List<(EnumLogType Type, string Message)> entries = [.. rig.Logger.Entries.Where(entry => entry.Type == EnumLogType.Warning)];
        Assert.Equal(3, entries.Count);
        Assert.Contains(
            "Pulse already serves 100 series of Stratum behavior timings, the most it keeps for that family. "
                + "What comes after is added to series whose behavior label reads other.",
            entries.Select(entry => entry.Message));
        Assert.Contains(
            "Pulse already serves 100 series of Stratum entity type timings, the most it keeps for that family. "
                + "What comes after is added to series whose type label reads other.",
            entries.Select(entry => entry.Message));
        Assert.Contains(
            "Pulse already serves 100 series of Stratum AI task timings, the most it keeps for that family. "
                + "What comes after is added to series whose task label reads other.",
            entries.Select(entry => entry.Message));
    }

    // What it says.

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Constructor_SaysNothing_WhenTheBlockIsOff(bool bound, bool absent)
    {
        using Rig rig = new(Config(enabled: false), bound: bound, unavailable: absent ? null : "unreadable");

        rig.Tick(30);

        Assert.Empty(rig.Logger.Entries);
    }

    /// <summary>A server that is not Stratum, with the block on: the one notification of the failure
    /// table. It is a fact about the server, so it is said once however often the block is
    /// reloaded.</summary>
    [Fact]
    public void Constructor_Notifies_Once_WhenTheBlockIsOn_AndThisIsNotAStratumServer()
    {
        using Rig rig = new(bound: false);

        (EnumLogType type, string message) = Only(rig.Logger);
        Assert.Equal(EnumLogType.Notification, type);
        Assert.Equal(AbsentNotification, message);

        rig.Metrics.Apply(Config());
        rig.Metrics.Apply(Config(burstTicks: 7));
        rig.Tick(30);
        Assert.Single(rig.Logger.Entries);
    }

    /// <summary>A reload that switches the block on is the other way the line is earned.</summary>
    [Fact]
    public void Apply_Notifies_WhenAReloadSwitchesTheBlockOn_OnAServerThatIsNotStratum()
    {
        using Rig rig = new(Config(enabled: false), bound: false);
        Assert.Empty(rig.Logger.Entries);

        rig.Metrics.Apply(Config());
        rig.Metrics.Apply(Config());

        (EnumLogType type, string message) = Only(rig.Logger);
        Assert.Equal(EnumLogType.Notification, type);
        Assert.Equal(AbsentNotification, message);
    }

    /// <summary>A Stratum Pulse cannot read: one warning, which carries the binder's reason, since
    /// that is the part an operator acts on.</summary>
    [Fact]
    public void Constructor_Warns_Once_WithTheBindersReason_WhenStratumCannotBeRead()
    {
        using Rig rig = new(bound: false, unavailable: "this Stratum predates the reading contract; Stratum 9.9 or later is needed");

        (EnumLogType type, string message) = Only(rig.Logger);
        Assert.Equal(EnumLogType.Warning, type);
        Assert.Equal(
            "Pulse cannot read Stratum's entity timings: this Stratum predates the reading contract; Stratum 9.9 "
                + "or later is needed. They are not served on this server; every other metric is unaffected.",
            message);

        rig.Metrics.Apply(Config());
        Assert.Single(rig.Logger.Entries);
    }

    [Fact]
    public void Constructor_SaysItIsTiming_WhenStratumIsBound_AndTheBlockIsOn()
    {
        using Rig rig = new();

        (EnumLogType type, string message) = Only(rig.Logger);
        Assert.Equal(EnumLogType.Notification, type);
        Assert.Equal("Pulse times Stratum's entity behaviors: bursts of 5 ticks every 1s.", message);

        rig.Metrics.Apply(Config(burstTicks: 7));
        Assert.Equal(
            "Pulse times Stratum's entity behaviors: bursts of 7 ticks every 1s.", rig.Logger.Entries[^1].Message);
    }

    // The one series that is seeded.

    [Fact]
    public void Seed_PutsTheTimedTicksOnTheWireAtZero_WhileTheFeatureRuns()
    {
        using Rig rig = new();

        rig.Metrics.Seed();

        Assert.Equal(0, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
        Assert.Single(rig.Samples());
    }

    /// <summary>A server that does not use the feature serves what it always did: not even the zero.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Seed_DoesNothing_WhenTheFeatureIsOffOrNotBound(bool enabled, bool bound)
    {
        using Rig rig = new(Config(enabled: enabled), bound: bound);

        rig.Metrics.Seed();

        Assert.Empty(rig.Samples());
    }

    [Fact]
    public void Apply_On_SeedsTheTimedTicks_SoThatAReloadPutsTheFamilyOnTheWire()
    {
        using Rig rig = new(Config(enabled: false));
        Assert.Empty(rig.Samples());

        rig.Metrics.Apply(Config());

        Assert.Equal(0, Value(rig.Samples(), "pulse_stratum_timed_ticks_total"));
    }

    // The live-server half.

    /// <summary>The by-name lookup against the game's own API assembly, which on a vanilla server has
    /// no such type: the block on earns the one notification, and a reload does not repeat it.</summary>
    [Fact]
    public void Create_BindsNothing_AgainstTheGamesOwnApi_AndSaysSoOnce_WhenTheBlockIsOn()
    {
        (ICoreServerAPI api, _) = AutoFakeProxy.Create<ICoreServerAPI>();
        FakeLogger logger = new();
        using Meter meter = new($"Pulse.Test.StratumTimingsMetrics.{Guid.NewGuid():N}");
        PulseConfig booted = new() { StratumTimings = Config() };

        StratumTimingsMetrics metrics = StratumTimingsMetrics.Create(api, logger, meter, booted, new ModOwners(_ => null));

        Assert.Equal(AbsentNotification, Only(logger).Message);
        Assert.Empty(Registered(meter));

        metrics.Reload(booted);
        metrics.Reload(new PulseConfig { StratumTimings = null! });

        Assert.Single(logger.Entries);
    }
}
