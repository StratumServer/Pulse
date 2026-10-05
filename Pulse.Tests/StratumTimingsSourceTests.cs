using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace Pulse.Tests;

/// <summary>The binder against fakes: one with the contract's exact shape, declared under the real
/// full name in this assembly, and one for every way a Stratum can fail to have it.</summary>
public class StratumTimingsSourceTests
{
    private const string Predates = "this Stratum predates the reading contract; Stratum ";
    private const string MissingMembers = "Stratum's reading contract does not have the members its version promises";

    /// <summary>The accumulator's state is static, as the real one's is, and every test of this class
    /// starts from a clean one.</summary>
    public StratumTimingsSourceTests() => StratumEntityBehaviorTimings.Reset();

    private static Type? ByName()
        => typeof(StratumTimingsSourceTests).Assembly.GetType(StratumTimingsSource.TypeName, throwOnError: false);

    private static StratumTimingsSource Bound()
    {
        StratumTimingsSource? source = StratumTimingsSource.TryBind(ByName(), out string? reason);
        Assert.Null(reason);
        return Assert.IsType<StratumTimingsSource>(source);
    }

    private static Type Fake(string name)
        => typeof(StratumTimingsSourceTests).GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(name + " is not a fake of this class");

    /// <summary>The type is absent on vanilla, on Lithos and on any other fork: not a failure, so
    /// there is no reason to log.</summary>
    [Fact]
    public void TryBind_Returns_NothingAndNoReason_WhenTheTypeIsAbsent()
    {
        Assert.Null(StratumTimingsSource.TryBind(null, out string? reason));
        Assert.Null(reason);
    }

    /// <summary>What the lookup at startup does: the type is found by its full name in an assembly,
    /// so the name has to be the one the type really has.</summary>
    [Fact]
    public void TypeName_Is_TheFullNameTheTypeIsDeclaredUnder()
    {
        Assert.Equal(StratumTimingsSource.TypeName, typeof(StratumEntityBehaviorTimings).FullName);
        Assert.Same(typeof(StratumEntityBehaviorTimings), ByName());
    }

    [Fact]
    public void TryBind_Binds_AContractWithTheExactShape()
    {
        Assert.NotNull(StratumTimingsSource.TryBind(ByName(), out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ContractVersion_Is_TheOneTheFakeDeclares()
        => Assert.Equal(StratumEntityBehaviorTimings.ContractVersion, StratumTimingsSource.ContractVersion);

    /// <summary>Every row of the failure table that has to be settled before anything is wired. The
    /// type is there and has no public <c>ContractVersion</c>: a Stratum that has not released the
    /// contract looks like that, and the reason names the release that has one. One that is not
    /// public is as absent from the contract's surface as one that is missing.</summary>
    [Theory]
    [InlineData(nameof(NoContractVersion))]
    [InlineData(nameof(NonPublicContractVersion))]
    public void TryBind_Refuses_AStratumWithoutAPublicContractVersion_AndNamesTheVersionItNeeds(string fake)
    {
        Assert.Null(StratumTimingsSource.TryBind(Fake(fake), out string? reason));

        Assert.Equal(Predates + StratumTimingsSource.MinimumStratumVersion + " or later is needed", reason);
    }

    /// <summary>A <c>ContractVersion</c> that is there but is not an integer literal (a static field,
    /// which Stratum's initializer would have to run to read, a long, a property) is not a Stratum
    /// that predates the contract, and must not be told to upgrade a Stratum that is current: it has
    /// the name and not the shape.</summary>
    [Theory]
    [InlineData(nameof(ReadonlyContractVersion))]
    [InlineData(nameof(LongContractVersion))]
    [InlineData(nameof(PropertyContractVersion))]
    public void TryBind_Refuses_AContractVersionThatIsNotAnIntegerLiteral_AsTheWrongShape_NotAsAnOldStratum(string fake)
    {
        Assert.Null(StratumTimingsSource.TryBind(Fake(fake), out string? reason));

        Assert.Equal(MissingMembers, reason);
        Assert.DoesNotContain("predates", reason);
    }

    /// <summary>The version a Stratum reports is compared for equality, not for order: an older
    /// number is a contract this Pulse does not know either, and nothing is guessed from either
    /// side.</summary>
    [Theory]
    [InlineData(nameof(OlderContract), 0)]
    [InlineData(nameof(NewerContract), 2)]
    public void TryBind_Refuses_AContractVersionItDoesNotKnow_AndSaysWhichItFoundAndWhichItReads(string fake, int found)
    {
        Assert.Null(StratumTimingsSource.TryBind(Fake(fake), out string? reason));

        Assert.Equal(
            $"Stratum offers reading contract version {found}, this Pulse reads version {StratumTimingsSource.ContractVersion}",
            reason);
    }

    /// <summary>The right version number on a type that does not have what the number promises.
    /// The shapes are the contract's exactly, so a member that merely works (a lease of a subtype, a
    /// parameter that is a base of the list) is refused as well.</summary>
    [Theory]
    [InlineData(nameof(NoRequestRecording))]
    [InlineData(nameof(NoSnapshot))]
    [InlineData(nameof(RequestReturnsObject))]
    [InlineData(nameof(RequestReturnsASubtype))]
    [InlineData(nameof(RequestTakesAnArgument))]
    [InlineData(nameof(SnapshotTakesAnotherList))]
    [InlineData(nameof(SnapshotTakesABaseOfTheList))]
    [InlineData(nameof(SnapshotReturnsAValue))]
    [InlineData(nameof(InstanceMembers))]
    [InlineData(nameof(NonPublicMembers))]
    public void TryBind_Refuses_AMemberThatIsMissingOrNotTheShapeTheContractPromises(string fake)
    {
        Assert.Null(StratumTimingsSource.TryBind(Fake(fake), out string? reason));

        Assert.Equal(MissingMembers, reason);
    }

    [Fact]
    public void Request_Asks_TheAccumulatorForOneLease_AndHandsItBack()
    {
        StratumTimingsSource source = Bound();

        IDisposable lease = source.Request();

        Assert.Equal(1, StratumEntityBehaviorTimings.LeasesRequested);
        Assert.Equal(0, StratumEntityBehaviorTimings.LeasesReleased);
        lease.Dispose();
        Assert.Equal(1, StratumEntityBehaviorTimings.LeasesReleased);
    }

    [Fact]
    public void Snapshot_Fills_TheListItWasGiven()
    {
        StratumTimingsSource source = Bound();
        StratumEntityBehaviorTimings.Totals["entity.type.wolf-eurasian-adult-male"] = (120, 3);
        StratumEntityBehaviorTimings.Totals["entity.behavior.players.health"] = (45, 9);
        List<(string Key, long Ticks, long Calls)> into = [("stale", 1, 1)];

        source.Snapshot(into);

        Assert.Equal(
            [("entity.type.wolf-eurasian-adult-male", 120L, 3L), ("entity.behavior.players.health", 45L, 9L)],
            into);
    }

    /// <summary>The three delegate calls a burst makes, end to end: a lease, a snapshot at each end,
    /// the totals moving in between, and the fold turning the two snapshots into what the burst
    /// added. The baseline is taken on the warm-up tick, once recording is on, so it already holds
    /// the keys that ran in that tick; a key that did not run until the burst is only in the second
    /// snapshot. Whole seconds, which come out exact whatever the stopwatch's frequency is.</summary>
    [Fact]
    public void ABurst_ReadThroughTheBinder_FoldsIntoWhatItAdded()
    {
        StratumTimingsSource source = Bound();
        StratumFold fold = new(_ => null);
        List<(string Key, long Ticks, long Calls)> start = [];
        List<(string Key, long Ticks, long Calls)> end = [];

        using (source.Request())
        {
            // The warm-up tick.
            StratumEntityBehaviorTimings.Totals["entity.type.wolf-eurasian-adult-male"] = (Stopwatch.Frequency, 5);
            StratumEntityBehaviorTimings.Totals["entity.type.wolf-eurasian-adult-female"] = (Stopwatch.Frequency, 2);
            source.Snapshot(start);

            // The measured ticks.
            StratumEntityBehaviorTimings.Totals["entity.type.wolf-eurasian-adult-male"] = (Stopwatch.Frequency * 3, 9);
            StratumEntityBehaviorTimings.Totals["entity.type.wolf-eurasian-adult-female"] = (Stopwatch.Frequency * 2, 3);
            StratumEntityBehaviorTimings.Totals["entity.ai.creatures.task.idle"] = (Stopwatch.Frequency, 8);
            source.Snapshot(end);
        }

        StratumBurst burst = fold.Fold(start, end);

        Assert.Equal(2, start.Count);
        Assert.Equal(1, StratumEntityBehaviorTimings.LeasesRequested);
        Assert.Equal(1, StratumEntityBehaviorTimings.LeasesReleased);
        EntityDelta wolf = Assert.Single(burst.Entities);
        Assert.Equal("wolf", wolf.Type);
        Assert.Equal(3.0, wolf.Seconds);
        Assert.Equal(5, wolf.Calls);
        TaskDelta idle = Assert.Single(burst.Tasks);
        Assert.Equal(1.0, idle.Seconds);
    }

    // The contract's members, in the shapes the fakes below break one at a time. Nothing here is
    // ever called: only its shape is read.

    private static IDisposable Lease() => throw new NotSupportedException();

    // No ContractVersion at all: what every Stratum released so far looks like.
    private static class NoContractVersion
    {
        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    // A static field is not a literal: reading it would run the type's initializer.
    private static class ReadonlyContractVersion
    {
        public static readonly int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    // A literal, but not an int.
    private static class LongContractVersion
    {
        public const long ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    // Not part of the public surface the contract is read from.
    private static class NonPublicContractVersion
    {
        internal const int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    // Not a field at all.
    private static class PropertyContractVersion
    {
        public static int ContractVersion => 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class OlderContract
    {
        public const int ContractVersion = 0;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class NewerContract
    {
        public const int ContractVersion = 2;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class NoRequestRecording
    {
        public const int ContractVersion = 1;

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class NoSnapshot
    {
        public const int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();
    }

    private static class RequestReturnsObject
    {
        public const int ContractVersion = 1;

        public static object RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class RequestReturnsASubtype
    {
        public const int ContractVersion = 1;

        public static MemoryStream RequestRecording() => new();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class RequestTakesAnArgument
    {
        public const int ContractVersion = 1;

        public static IDisposable RequestRecording(bool readers) => Lease();

        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class SnapshotTakesAnotherList
    {
        public const int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(List<(string Key, long Ticks)> into)
        {
        }
    }

    private static class SnapshotTakesABaseOfTheList
    {
        public const int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static void Snapshot(object into)
        {
        }
    }

    private static class SnapshotReturnsAValue
    {
        public const int ContractVersion = 1;

        public static IDisposable RequestRecording() => Lease();

        public static int Snapshot(List<(string Key, long Ticks, long Calls)> into) => into.Count;
    }

    private sealed class InstanceMembers
    {
        public const int ContractVersion = 1;

        public IDisposable RequestRecording() => Lease();

        public void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }

    private static class NonPublicMembers
    {
        public const int ContractVersion = 1;

        internal static IDisposable RequestRecording() => Lease();

        internal static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
        }
    }
}
