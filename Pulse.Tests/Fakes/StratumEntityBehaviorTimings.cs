// A stand-in for Stratum's accumulator, declared under the full name Stratum gives it in the game's
// API assembly. It lives in the test assembly, which compiles against the vanilla API, so the
// by-name lookup the mod makes at startup has something to find on a machine with no Stratum.
//
// It has the reading contract's exact shape (version 1) and keeps the counters a test reads back.
// The state is static, as the real accumulator's is, so only StratumTimingsSourceTests drives it.
namespace Vintagestory.API.Common.Entities;

internal static class StratumEntityBehaviorTimings
{
    public const int ContractVersion = 1;

    /// <summary>What <see cref="Snapshot"/> reports, by key: cumulative, which is what a test moves
    /// from one tick to the next.</summary>
    public static readonly Dictionary<string, (long Ticks, long Calls)> Totals = [];

    public static int LeasesRequested { get; private set; }

    public static int LeasesReleased { get; private set; }

    public static void Reset()
    {
        Totals.Clear();
        LeasesRequested = 0;
        LeasesReleased = 0;
    }

    public static IDisposable RequestRecording()
    {
        LeasesRequested++;
        return new Lease();
    }

    /// <summary>Clears the list first, like the real one, so a stale entry never survives a read.</summary>
    public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
    {
        into.Clear();
        foreach (KeyValuePair<string, (long Ticks, long Calls)> entry in Totals)
        {
            into.Add((entry.Key, entry.Value.Ticks, entry.Value.Calls));
        }
    }

    /// <summary>Dispose releases once, however often it is called.</summary>
    private sealed class Lease : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (!released)
            {
                released = true;
                LeasesReleased++;
            }
        }
    }
}
