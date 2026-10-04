using System.Diagnostics;
using Vintagestory.API.Common;
using Xunit;

// The game's API declares a Func delegate of its own in Vintagestory.API.Common, so the one this
// file wants gets a name of its own rather than a namespace qualifier on every signature.
using OwnerLookup = System.Func<string, string?>;

namespace Pulse.Tests;

public class StratumFoldTests
{
    /// <summary>The stopwatch ticks of <paramref name="seconds"/>. The tests use halves and
    /// quarters, which are exact as seconds whatever the platform's frequency is.</summary>
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>One accumulator entry, as the snapshot reports it.</summary>
    private static (string Key, long Ticks, long Calls) Reading(string key, double seconds, long calls = 0)
        => (key, Ticks(seconds), calls);

    private static StratumFold NewFold(int cap = StratumFold.SeriesCap, OwnerLookup? owner = null)
        => new(owner ?? (_ => null), cap);

    private static void AssertSeries<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        where T : notnull
        => Assert.Equal(
            expected.OrderBy(series => series.ToString(), StringComparer.Ordinal),
            actual.OrderBy(series => series.ToString(), StringComparer.Ordinal));

    private static EntityDelta Entity(StratumBurst burst, string type) => burst.Entities.Single(e => e.Type == type);

    private static BehaviorDelta Behavior(StratumBurst burst, string behavior)
        => burst.Behaviors.Single(b => b.Behavior == behavior);

    [Fact]
    public void Fold_Reports_WhatTheBurstAdded_NotTheTotals()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-eurasian-adult-male", 1.0, 10)],
            [Reading("entity.type.wolf-eurasian-adult-male", 3.0, 30)]);

        EntityDelta wolf = Assert.Single(burst.Entities);
        Assert.Equal(2.0, wolf.Seconds, 9);
        Assert.Equal(20, wolf.Calls);
    }

    [Fact]
    public void Fold_Converts_StopwatchTicksToSeconds_ByTheStopwatchFrequency()
    {
        StratumBurst burst = NewFold().Fold([], [("entity.ai.creatures.task.idle", Stopwatch.Frequency * 5, 1)]);

        Assert.Equal(5.0, Assert.Single(burst.Tasks).Seconds, 9);
    }

    /// <summary>The usual case: the accumulator is emptied when its last reader lets go, so a burst
    /// opens on nothing and every key it sees appeared during it.</summary>
    [Fact]
    public void Fold_Takes_EverythingAKeyHolds_WhenItAppearedDuringTheBurst()
    {
        StratumBurst burst = NewFold().Fold([], [Reading("entity.type.wolf-a", 2.0, 7)]);

        EntityDelta wolf = Assert.Single(burst.Entities);
        Assert.Equal(2.0, wolf.Seconds, 9);
        Assert.Equal(7, wolf.Calls);
    }

    [Fact]
    public void Fold_Says_NothingAboutAKey_ThatIsOnlyInTheFirstSnapshot()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-a", 2.0, 7), Reading("entity.type.hare-a", 1.0, 3)],
            [Reading("entity.type.hare-a", 1.5, 4)]);

        Assert.Equal("hare", Assert.Single(burst.Entities).Type);
    }

    /// <summary>A total that went down was reset in between, so everything it holds now is what the
    /// burst added: the seconds and the calls alike.</summary>
    [Fact]
    public void Fold_Takes_TheNewTotals_WhenATotalWentDown()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-a", 4.0, 40)],
            [Reading("entity.type.wolf-a", 0.5, 5)]);

        EntityDelta wolf = Assert.Single(burst.Entities);
        Assert.Equal(0.5, wolf.Seconds, 9);
        Assert.Equal(5, wolf.Calls);
    }

    /// <summary>A total never goes below zero, and a counter must not run backwards over one that
    /// does.</summary>
    [Fact]
    public void Fold_Never_RunsASeriesBackwards()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-a", 1.0, 10), Reading("entity.type.hare-a", 1.0, 10)],
            [Reading("entity.type.wolf-a", -3.0, -2), Reading("entity.type.hare-a", 2.0, 12)]);

        EntityDelta hare = Assert.Single(burst.Entities);
        Assert.Equal("hare", hare.Type);
        Assert.Equal(1.0, hare.Seconds, 9);
    }

    [Fact]
    public void Fold_Skips_AKeyTheBurstAddedNothingTo()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-a", 2.0, 7), Reading("entity.behavior.players.health", 1.0)],
            [Reading("entity.type.wolf-a", 2.0, 7), Reading("entity.behavior.players.health", 1.0)]);

        Assert.Empty(burst.Entities);
        Assert.Empty(burst.Behaviors);
    }

    /// <summary>A key with nothing added is not a series yet, so it must not use up one of the places
    /// the cap hands out: the key that did something takes it.</summary>
    [Fact]
    public void Fold_Gives_NoPlaceUnderTheCap_ToAKeyTheBurstAddedNothingTo()
    {
        StratumBurst burst = NewFold(cap: 1).Fold(
            [Reading("entity.type.idle-a", 1.0, 1)],
            [Reading("entity.type.idle-a", 1.0, 1), Reading("entity.type.busy-a", 1.0, 1)]);

        Assert.Equal("busy", Assert.Single(burst.Entities).Type);
    }

    /// <summary>The first snapshot belongs to its own burst. A fold that kept the last one's would
    /// subtract it from the next burst's totals.</summary>
    [Fact]
    public void Fold_Forgets_TheFirstSnapshot_BetweenBursts()
    {
        StratumFold fold = NewFold();
        fold.Fold([Reading("entity.type.wolf-a", 0.5, 2)], [Reading("entity.type.wolf-a", 4.0, 40)]);

        StratumBurst burst = fold.Fold([], [Reading("entity.type.wolf-a", 1.0, 5)]);

        EntityDelta wolf = Assert.Single(burst.Entities);
        Assert.Equal(1.0, wolf.Seconds, 9);
        Assert.Equal(5, wolf.Calls);
    }

    [Fact]
    public void Fold_Returns_NothingForTwoEmptySnapshots()
    {
        StratumBurst burst = NewFold().Fold([], []);

        Assert.Empty(burst.Behaviors);
        Assert.Empty(burst.Tasks);
        Assert.Empty(burst.Entities);
        Assert.Empty(burst.NewlyOverflowed);
    }

    /// <summary>One key of each shape the accumulator has, with the keys Pulse does not read among
    /// them. The thread-safe label is the text <c>true</c>, which is what the aggregator would not
    /// give a bool.</summary>
    [Fact]
    public void Fold_Reads_EveryFamily_AndNothingElse()
    {
        StratumBurst burst = NewFold().Fold(
            [],
            [
                Reading("entity.behavior.players.health", 0.5),
                Reading("entity.behavior.threadsafe.creatures.done-behavior-entitypassivephysics", 2.0),
                Reading("entity.ai.creatures.task.idle", 0.25, 40),
                Reading("entity.ai.creatures.taskai.taskManager", 1.0, 99),
                Reading("entity.ai.creatures.taskStartScan", 1.0, 99),
                Reading("entity.type.wolf-eurasian-adult-male", 4.0, 12),
                Reading("physics.tickWork", 1.0, 99),
            ]);

        AssertSeries(
            [
                new BehaviorDelta("players", "health", "false", TickAttribution.Unattributed, 0.5),
                new BehaviorDelta("creatures", "entitypassivephysics", "true", TickAttribution.Unattributed, 2.0),
            ],
            burst.Behaviors);
        AssertSeries([new TaskDelta("creatures", "idle", 0.25)], burst.Tasks);
        AssertSeries([new EntityDelta("wolf", 4.0, 12)], burst.Entities);
    }

    /// <summary>The type is the first part of the code, so every variant of an entity folds into one
    /// series whose seconds and ticks are the sums.</summary>
    [Fact]
    public void Fold_Sums_EveryEntityCodeWithTheSameFirstPart_IntoOneType()
    {
        StratumBurst burst = NewFold().Fold(
            [Reading("entity.type.wolf-eurasian-adult-male", 1.0, 4)],
            [
                Reading("entity.type.wolf-eurasian-adult-male", 1.5, 6),
                Reading("entity.type.wolf-arctic-baby-female", 0.25, 3),
                Reading("entity.type.chicken-hen", 1.0, 10),
            ]);

        AssertSeries([new EntityDelta("wolf", 0.75, 5), new EntityDelta("chicken", 1.0, 10)], burst.Entities);
    }

    /// <summary>The same behavior name on the two threads is two series: the thread-safe time is
    /// summed across physics threads and is not comparable to the main thread's.</summary>
    [Fact]
    public void Fold_Keeps_ABehaviorOnTheMainThread_AndOnThePhysicsThreads_Apart()
    {
        StratumBurst burst = NewFold().Fold(
            [],
            [
                Reading("entity.behavior.players.entitycontrolledphysics", 0.5),
                Reading("entity.behavior.threadsafe.players.done-behavior-entitycontrolledphysics", 1.0),
            ]);

        AssertSeries(
            [
                new BehaviorDelta("players", "entitycontrolledphysics", "false", TickAttribution.Unattributed, 0.5),
                new BehaviorDelta("players", "entitycontrolledphysics", "true", TickAttribution.Unattributed, 1.0),
            ],
            burst.Behaviors);
    }

    [Fact]
    public void Fold_Credits_ABehavior_ToTheModTheOwnerLookupNames()
    {
        StratumBurst burst = NewFold(owner: name => name == "health" ? "survival" : null)
            .Fold([], [Reading("entity.behavior.players.health", 1.0)]);

        Assert.Equal("survival", Assert.Single(burst.Behaviors).Modid);
    }

    [Fact]
    public void Fold_Reports_ABehaviorNoModClaims_AsUnattributed()
    {
        StratumBurst burst = NewFold().Fold([], [Reading("entity.behavior.players.health", 1.0)]);

        Assert.Equal(TickAttribution.Unattributed, Assert.Single(burst.Behaviors).Modid);
    }

    /// <summary>Only a behavior has a mod to look up, and the table knows a behavior by the bare
    /// name the engine's mark carries after its prefix, which is what the accumulator records.</summary>
    [Fact]
    public void Fold_Asks_TheOwnerLookup_ForBehaviorsOnly_ByTheirBareName()
    {
        List<string> asked = [];
        StratumFold fold = NewFold(owner: name =>
        {
            asked.Add(name);
            return null;
        });

        fold.Fold(
            [],
            [
                Reading("entity.behavior.players.health", 1.0),
                Reading("entity.behavior.threadsafe.creatures.done-behavior-entitypassivephysics", 1.0),
                Reading("entity.ai.creatures.task.idle", 1.0),
                Reading("entity.type.wolf-a", 1.0),
            ]);

        Assert.Equal(["entitypassivephysics", "health"], asked.Order(StringComparer.Ordinal));
    }

    /// <summary>The path attribution's behaviors take, end to end: a live instance taught the table
    /// what a class marks with, and the fold reads the mod off it. A name no class has shown is
    /// nobody's, a class of the game's own API is the engine's.</summary>
    [Fact]
    public void Fold_Resolves_TheMod_ThroughTheTableAttributionUses()
    {
        ModOwners owners = new(_ => null);
        owners.AddSystem("mymod", typeof(StratumFoldTests));
        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "timeddespawn", typeof(StratumFoldTests));
        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "entitypassivephysics", typeof(EntityBehaviorPassivePhysics));

        StratumBurst burst = new StratumFold(owners.Owner).Fold(
            [],
            [
                Reading("entity.behavior.threadsafe.creatures.done-behavior-timeddespawn", 1.0),
                Reading("entity.behavior.threadsafe.creatures.done-behavior-entitypassivephysics", 1.0),
                Reading("entity.behavior.players.nobodys", 1.0),
            ]);

        Assert.Equal("mymod", Behavior(burst, "timeddespawn").Modid);
        Assert.Equal(TickAttribution.Engine, Behavior(burst, "entitypassivephysics").Modid);
        Assert.Equal(TickAttribution.Unattributed, Behavior(burst, "nobodys").Modid);
    }

    /// <summary>A name only a later walk learns (its entity spawned during the burst) changes owner
    /// once, and its series follows: the label is looked up every burst, not remembered from the
    /// first. The series it leaves simply stops growing.</summary>
    [Fact]
    public void Fold_Follows_ABehaviorToItsMod_OnceTheTableHasLearnedIt()
    {
        Dictionary<string, string?> table = [];
        StratumFold fold = NewFold(owner: name => table.GetValueOrDefault(name));
        StratumBurst first = fold.Fold([], [Reading("entity.behavior.players.fancy", 1.0)]);
        table["fancy"] = "mymod";

        StratumBurst second = fold.Fold(
            [Reading("entity.behavior.players.fancy", 1.0)],
            [Reading("entity.behavior.players.fancy", 3.0)]);

        Assert.Equal(TickAttribution.Unattributed, Assert.Single(first.Behaviors).Modid);
        BehaviorDelta moved = Assert.Single(second.Behaviors);
        Assert.Equal("mymod", moved.Modid);
        Assert.Equal(2.0, moved.Seconds, 9);
    }

    /// <summary>The cap counts label sets that have a name, not the owner they are filed under: a
    /// behavior that changes hands is the same series' place, not a second one.</summary>
    [Fact]
    public void Fold_Gives_ABehaviorWhoseOwnerChanged_NoSecondPlaceUnderTheCap()
    {
        Dictionary<string, string?> table = [];
        StratumFold fold = NewFold(cap: 1, owner: name => table.GetValueOrDefault(name));
        fold.Fold([], [Reading("entity.behavior.players.fancy", 1.0)]);
        table["fancy"] = "mymod";

        StratumBurst burst = fold.Fold([], [Reading("entity.behavior.players.fancy", 1.0)]);

        Assert.Equal("fancy", Assert.Single(burst.Behaviors).Behavior);
    }

    /// <summary>A hundred label sets of each family get a series, and the hundred and first is lumped
    /// into other with whatever comes after it. A vanilla server is nowhere near it.</summary>
    [Fact]
    public void Fold_Keeps_AHundredSeriesOfEachFamily_ByDefault()
    {
        List<(string Key, long Ticks, long Calls)> end =
            [.. Enumerable.Range(0, 101).Select(i => Reading($"entity.type.t{i:000}-x", 1.0 + i, 1))];

        StratumBurst burst = NewFold().Fold([], end);

        Assert.Equal(100, StratumFold.SeriesCap);
        Assert.Equal(101, burst.Entities.Count);
        Assert.Equal(new EntityDelta("other", 1.0, 1), Entity(burst, "other"));
    }

    [Fact]
    public void Fold_Gives_TheFirstSeriesOfAFamilyTheirOwn_AndLumpsTheRestIntoOther()
    {
        StratumBurst burst = NewFold(cap: 2).Fold(
            [],
            [
                Reading("entity.type.a-x", 4.0, 40),
                Reading("entity.type.b-x", 2.0, 20),
                Reading("entity.type.c-x", 1.0, 10),
                Reading("entity.type.d-x", 0.5, 5),
            ]);

        AssertSeries(
            [
                new EntityDelta("a", 4.0, 40),
                new EntityDelta("b", 2.0, 20),
                new EntityDelta("other", 1.5, 15),
            ],
            burst.Entities);
    }

    /// <summary>A burst that brings more new label sets than there are places, which the first one
    /// can, admits the heaviest, whatever order the accumulator listed them in: what ends up lumped
    /// together is the cheap tail.</summary>
    [Fact]
    public void Fold_Admits_TheHeaviestNewSeriesFirst()
    {
        StratumBurst burst = NewFold(cap: 2).Fold(
            [],
            [
                Reading("entity.type.c-x", 1.0, 1),
                Reading("entity.type.d-x", 0.5, 1),
                Reading("entity.type.b-x", 2.0, 1),
                Reading("entity.type.a-x", 4.0, 1),
            ]);

        Assert.Equal(["a", "b", "other"], burst.Entities.Select(entity => entity.Type).Order(StringComparer.Ordinal));
    }

    /// <summary>Two new series as heavy as each other are admitted in label order, so the same
    /// burst gives the same series on every run, whatever order the accumulator listed them in.</summary>
    [Fact]
    public void Fold_Admits_EquallyHeavySeries_ByTheirName()
    {
        StratumBurst burst = NewFold(cap: 1).Fold([], [Reading("entity.type.b-x", 1.0), Reading("entity.type.a-x", 1.0)]);

        Assert.Contains(burst.Entities, entity => entity.Type == "a");
    }

    [Fact]
    public void Fold_Admits_EquallyHeavySeries_ByTheirCategory()
    {
        StratumBurst burst = NewFold(cap: 1).Fold(
            [],
            [Reading("entity.behavior.players.fancy", 1.0), Reading("entity.behavior.creatures.fancy", 1.0)]);

        Assert.Contains(burst.Behaviors, behavior => behavior.Category == "creatures");
    }

    [Fact]
    public void Fold_Admits_EquallyHeavySeries_MainThreadFirst()
    {
        StratumBurst burst = NewFold(cap: 1).Fold(
            [],
            [
                Reading("entity.behavior.threadsafe.players.done-behavior-fancy", 1.0),
                Reading("entity.behavior.players.fancy", 1.0),
            ]);

        Assert.Contains(burst.Behaviors, behavior => behavior.ThreadSafe == "false");
    }

    /// <summary>A series that has a place keeps it for the life of the server, even against a newcomer
    /// that is far heavier: a series that moved in and out of other would make other go down, which
    /// Prometheus reads as a counter reset.</summary>
    [Fact]
    public void Fold_Never_EvictsASeries_ToMakeRoomForAHeavierNewcomer()
    {
        StratumFold fold = NewFold(cap: 2);
        fold.Fold([], [Reading("entity.type.a-x", 1.0, 1), Reading("entity.type.b-x", 1.0, 1)]);

        StratumBurst burst = fold.Fold(
            [],
            [Reading("entity.type.a-x", 1.0, 1), Reading("entity.type.b-x", 1.0, 1), Reading("entity.type.heavy-x", 9.0, 9)]);

        AssertSeries(
            [new EntityDelta("a", 1.0, 1), new EntityDelta("b", 1.0, 1), new EntityDelta("other", 9.0, 9)],
            burst.Entities);
    }

    /// <summary>What spilled once keeps spilling: a key that went into other does not come out of it
    /// into a series of its own, which would leave a gap in the one and a jump in the other.</summary>
    [Fact]
    public void Fold_Keeps_AddingToOther_ForAKeyThatSpilledBefore()
    {
        StratumFold fold = NewFold(cap: 1);
        fold.Fold([], [Reading("entity.type.a-x", 2.0, 1), Reading("entity.type.b-x", 1.0, 1)]);

        StratumBurst burst = fold.Fold([], [Reading("entity.type.b-x", 3.0, 3)]);

        Assert.Equal(new EntityDelta("other", 3.0, 3), Assert.Single(burst.Entities));
    }

    /// <summary>A series that already has its place is not counted against the cap again at the
    /// next burst, or the cap would spill the keys it had admitted.</summary>
    [Fact]
    public void Fold_Keeps_TheSeriesItHasAdmitted_AtEveryBurst()
    {
        StratumFold fold = NewFold(cap: 2);
        fold.Fold([], [Reading("entity.type.a-x", 1.0, 1), Reading("entity.type.b-x", 1.0, 1)]);

        StratumBurst burst = fold.Fold(
            [],
            [Reading("entity.type.a-x", 2.0, 2), Reading("entity.type.b-x", 2.0, 2)]);

        AssertSeries([new EntityDelta("a", 2.0, 2), new EntityDelta("b", 2.0, 2)], burst.Entities);
    }

    /// <summary>Each family counts its own places: a full family of entity types does not push a
    /// behavior into other.</summary>
    [Fact]
    public void Fold_Caps_EachFamilyOnItsOwn()
    {
        StratumBurst burst = NewFold(cap: 1).Fold(
            [],
            [
                Reading("entity.type.a-x", 2.0, 2),
                Reading("entity.type.b-x", 1.0, 1),
                Reading("entity.ai.creatures.task.idle", 2.0),
                Reading("entity.ai.creatures.task.flee", 1.0),
                Reading("entity.behavior.players.health", 2.0),
                Reading("entity.behavior.players.fancy", 1.0),
            ]);

        AssertSeries(
            [new BehaviorDelta("players", "health", "false", TickAttribution.Unattributed, 2.0), new BehaviorDelta("other", "other", "other", "other", 1.0)],
            burst.Behaviors);
        AssertSeries([new TaskDelta("creatures", "idle", 2.0), new TaskDelta("other", "other", 1.0)], burst.Tasks);
        AssertSeries([new EntityDelta("a", 2.0, 2), new EntityDelta("other", 1.0, 1)], burst.Entities);
    }

    /// <summary>Every label of the overflow series reads other, so it is one series per family and
    /// not one per category and thread.</summary>
    [Fact]
    public void Fold_Folds_EveryKindOfBehaviorThatSpills_IntoOneOtherSeries()
    {
        StratumBurst burst = NewFold(cap: 1).Fold(
            [],
            [
                Reading("entity.behavior.players.health", 8.0),
                Reading("entity.behavior.players.fancy", 1.0),
                Reading("entity.behavior.threadsafe.creatures.done-behavior-physics", 0.5),
                Reading("entity.behavior.threadsafe.inanimate.done-behavior-physics", 0.25),
            ]);

        BehaviorDelta other = Assert.Single(burst.Behaviors, behavior => behavior.Behavior == "other");
        Assert.Equal(new BehaviorDelta("other", "other", "other", "other", 1.75), other);
    }

    [Fact]
    public void Fold_Serves_NoOtherSeries_WhileNothingSpills()
    {
        StratumBurst burst = NewFold(cap: 2).Fold([], [Reading("entity.type.a-x", 1.0), Reading("entity.type.b-x", 1.0)]);

        Assert.DoesNotContain(burst.Entities, entity => entity.Type == "other");
        Assert.Empty(burst.NewlyOverflowed);
    }

    /// <summary>The burst that first pushes a family into its overflow series says so, once, so that
    /// what the caller logs is one line the first time and nothing after.</summary>
    [Fact]
    public void Fold_Reports_AFamilySpillingForTheFirstTime_OnceOnly()
    {
        StratumFold fold = NewFold(cap: 1);
        StratumBurst first = fold.Fold([], [Reading("entity.type.a-x", 2.0), Reading("entity.type.b-x", 1.0)]);
        StratumBurst second = fold.Fold([], [Reading("entity.type.a-x", 2.0), Reading("entity.type.c-x", 1.0)]);
        StratumBurst third = fold.Fold(
            [],
            [Reading("entity.type.a-x", 2.0), Reading("entity.ai.creatures.task.idle", 2.0), Reading("entity.ai.creatures.task.flee", 1.0)]);

        Assert.Equal([StratumFamily.Entity], first.NewlyOverflowed);
        Assert.Empty(second.NewlyOverflowed);
        Assert.Equal([StratumFamily.AiTask], third.NewlyOverflowed);
    }

    /// <summary>The cap is on label sets, so the same name in two categories is two of them.</summary>
    [Fact]
    public void Fold_Counts_ALabelSetByCategoryAndThread_NotByName()
    {
        StratumBurst burst = NewFold(cap: 2).Fold(
            [],
            [
                Reading("entity.ai.creatures.task.idle", 3.0),
                Reading("entity.ai.inanimate.task.idle", 2.0),
                Reading("entity.ai.players.task.idle", 1.0),
            ]);

        AssertSeries(
            [new TaskDelta("creatures", "idle", 3.0), new TaskDelta("inanimate", "idle", 2.0), new TaskDelta("other", "other", 1.0)],
            burst.Tasks);
    }

    /// <summary>Whatever the accumulator does between bursts (grows, is emptied, hands back a total
    /// that went down), no series is ever told to go down, and nothing it is told is lost: with no
    /// reset the sum of everything a series was told is the total the accumulator ended on.</summary>
    [Fact]
    public void Fold_Conserves_TheTotals_AcrossManyBursts()
    {
        Random random = new(2026);
        string[] codes = ["wolf-a", "wolf-b", "hare-a", "chicken-hen", "drifter-normal"];
        Dictionary<string, (long Ticks, long Calls)> totals = [];
        Dictionary<string, (double Seconds, long Calls)> told = [];
        StratumFold fold = NewFold();
        List<(string Key, long Ticks, long Calls)> start = [];

        for (int burst = 0; burst < 200; burst++)
        {
            foreach (string code in codes.Where(_ => random.Next(3) > 0))
            {
                (long ticks, long calls) = totals.GetValueOrDefault("entity.type." + code);
                totals["entity.type." + code] = (ticks + random.Next(1, 1000), calls + random.Next(1, 20));
            }

            List<(string Key, long Ticks, long Calls)> end = [.. totals.Select(t => (t.Key, t.Value.Ticks, t.Value.Calls))];
            foreach (EntityDelta delta in fold.Fold(start, end).Entities)
            {
                Assert.True(delta.Seconds >= 0 && delta.Calls >= 0);
                (double seconds, long calls) = told.GetValueOrDefault(delta.Type);
                told[delta.Type] = (seconds + delta.Seconds, calls + delta.Calls);
            }

            start = end;
        }

        foreach (string type in codes.Select(code => code[..code.IndexOf('-')]).Distinct())
        {
            long ticks = totals.Where(t => t.Key.StartsWith("entity.type." + type + "-")).Sum(t => t.Value.Ticks);
            long calls = totals.Where(t => t.Key.StartsWith("entity.type." + type + "-")).Sum(t => t.Value.Calls);
            Assert.Equal(ticks / (double)Stopwatch.Frequency, told[type].Seconds, 12);
            Assert.Equal(calls, told[type].Calls);
        }
    }

    [Fact]
    public void Fold_Never_RunsASeriesBackwards_WhateverTheTotalsDo()
    {
        Random random = new(7);
        StratumFold fold = NewFold(cap: 3);
        List<(string Key, long Ticks, long Calls)> start = [];

        for (int burst = 0; burst < 300; burst++)
        {
            List<(string Key, long Ticks, long Calls)> end =
            [
                .. Enumerable.Range(0, 8).Select(i => (
                    $"entity.type.t{i}-x",
                    (long)random.Next(-50, 100_000),
                    (long)random.Next(-5, 500))),
                .. Enumerable.Range(0, 8).Select(i => (
                    $"entity.behavior.players.b{i}",
                    (long)random.Next(-50, 100_000),
                    (long)random.Next(-5, 500))),
            ];

            StratumBurst folded = fold.Fold(start, end);

            Assert.All(folded.Entities, delta => Assert.True(delta.Seconds >= 0 && delta.Calls >= 0));
            Assert.All(folded.Behaviors, delta => Assert.True(delta.Seconds >= 0));
            start = end;
        }
    }
}
