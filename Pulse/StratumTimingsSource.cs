using System.Reflection;

namespace Pulse;

/// <summary>The only place in Pulse that knows Stratum exists: it looks for the reading contract on
/// Stratum's entity behavior timings and binds the two members Pulse reads through.</summary>
/// <remarks>Pulse compiles against the vanilla <c>VintagestoryAPI.dll</c>, so no Stratum type can
/// appear in its IL: a build that named one would load on Stratum and fail at JIT time on every
/// other server. The type is therefore named only as a string, looked up by the caller in the one
/// assembly that matters (<c>typeof(Entity).Assembly.GetType(TypeName, throwOnError: false)</c>: the
/// API the server actually loaded, so a mod that declares a type of the same name in its own
/// assembly cannot be mistaken for Stratum), and bound once to two delegates. A burst is then three
/// plain delegate calls and a dispose, with no <c>MethodInfo.Invoke</c> and no boxing.
/// <para>What is bound is a contract, not a version. The assembly versions of a Stratum install
/// read the same as vanilla's and Stratum releases often, so a list of known versions would always
/// be stale. <c>ContractVersion</c> is a literal that Stratum bumps only when a member changes
/// shape or meaning, never on additions: a Stratum without it predates the contract, one with
/// another number has a contract this Pulse does not know, and neither is guessed at.</para>
/// <para>Every member of the contract is public, so nothing here asks for
/// <c>BindingFlags.NonPublic</c>, and only framework types cross the boundary (<see cref="IDisposable"/>
/// and a list of value tuples), so the delegates can be typed without the Stratum type at compile
/// time. <see cref="TryBind"/> takes a <see cref="Type"/> rather than an assembly, which is what
/// lets a unit test hand it fakes.</para></remarks>
internal sealed class StratumTimingsSource(
    Func<IDisposable> request, Action<List<(string Key, long Ticks, long Calls)>> snapshot)
{
    /// <summary>The full name of Stratum's accumulator, in the <c>VintagestoryAPI</c> assembly it
    /// patches.</summary>
    internal const string TypeName = "Vintagestory.API.Common.Entities.StratumEntityBehaviorTimings";

    /// <summary>The one contract version this Pulse reads.</summary>
    internal const int ContractVersion = 1;

    /// <summary>The first Stratum release that carries the reading contract, named in the warning a
    /// Stratum without it earns.</summary>
    /// <remarks>PLACEHOLDER. Stratum has not released the contract yet, so there is no version to
    /// name. Replace the value with the first release that has it before the feature ships; nothing
    /// else changes, the tests read this constant rather than a copy of it.</remarks>
    internal const string MinimumStratumVersion = "<first release with the reading contract>";

    private static readonly Type SnapshotInto = typeof(List<(string, long, long)>);

    /// <summary>Asks Stratum to record, without touching the admin's <c>/stratum timings</c> switch
    /// and without the rest of what that switch turns on. The recording lasts until the returned
    /// lease is disposed, which Stratum makes safe to do more than once.</summary>
    public IDisposable Request() => request();

    /// <summary>Fills <paramref name="into"/> with the accumulator's cumulative totals: Stopwatch
    /// ticks and a call count per key, never reset while anyone records. Stratum clears the list
    /// first, and the read is thread-safe and takes nothing from the admin's own report.</summary>
    public void Snapshot(List<(string Key, long Ticks, long Calls)> into) => snapshot(into);

    /// <summary>Binds the contract on <paramref name="type"/>. Null with a null reason when the type
    /// is absent (vanilla, Lithos or another fork); null with a reason when it is there but not in a
    /// shape this Pulse reads.</summary>
    /// <remarks>The reason is the part of a log line a server owner reads, so it says what is wrong
    /// and what would fix it rather than which reflection call failed. Every check here is
    /// exact: a member that is merely compatible (a method that returns a type implementing
    /// <see cref="IDisposable"/>, a parameter that is a base of the list) is not the member the
    /// contract promises, and nothing is guessed. Reflection can still throw on a type that is broken
    /// in a way of its own, so call this from inside the startup try/catch, like the other probes.</remarks>
    public static StratumTimingsSource? TryBind(Type? type, out string? reason)
    {
        reason = null;
        if (type == null)
        {
            return null;
        }

        // A literal, read from metadata: a static field would have to run the type's initializer
        // before its value could be read, which is Stratum's code running before Pulse knows it is
        // looking at a shape it understands.
        if (type.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static) is not { IsLiteral: true } field
            || field.GetRawConstantValue() is not int version)
        {
            reason = $"this Stratum predates the reading contract; Stratum {MinimumStratumVersion} or later is needed";
            return null;
        }

        if (version != ContractVersion)
        {
            reason = $"Stratum offers reading contract version {version}, this Pulse reads version {ContractVersion}";
            return null;
        }

        MethodInfo? request = type.GetMethod("RequestRecording", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        MethodInfo? snapshot = type.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static, [SnapshotInto]);
        if (request?.ReturnType != typeof(IDisposable)
            || snapshot?.ReturnType != typeof(void)
            || snapshot.GetParameters()[0].ParameterType != SnapshotInto)
        {
            reason = "Stratum's reading contract does not have the members its version promises";
            return null;
        }

        return new StratumTimingsSource(
            request.CreateDelegate<Func<IDisposable>>(),
            snapshot.CreateDelegate<Action<List<(string Key, long Ticks, long Calls)>>>());
    }
}
