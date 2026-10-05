namespace Pulse;

/// <summary>What <c>/pulse</c> says back, and which config keys a reload cannot apply to a running
/// server.</summary>
/// <remarks>Text and comparison only: no state, no server, no file. That is what makes every reply
/// an operator will read assertable from a unit test.</remarks>
internal static class PulseCommands
{
    /// <summary>The reply to a reload that could not read the file. The error arrives as a message
    /// parameter rather than baked into the sentence: the engine runs a reply through
    /// <c>string.Format</c> on its way to whoever asked, and a JSON error quoting a stray brace
    /// would blow that up.</summary>
    public const string ReloadFailed =
        "Pulse could not read pulse.json ({0}). Nothing changed: the server is still running the "
        + "config it booted with.";

    public const string Unavailable =
        "Attribution is not available this run: Pulse could not read the engine's frame profiler.";

    /// <summary>Said after every switch, because the whole point is that it does not persist.</summary>
    private const string NotWritten =
        "pulse.json was not changed, so the file decides again after a restart.";

    /// <summary>Keys whose value on disk differs from the value the server is running on.</summary>
    /// <remarks>Everything outside the <c>Attribution</c> and <c>StratumTimings</c> blocks is read once
    /// in StartServerSide and wired into a socket, a meter or a listener interval, so a difference
    /// here is something the operator has to restart for. Compared against the config the server
    /// booted with rather than the last file read, so a second reload still names a port that is
    /// still wrong.</remarks>
    public static IReadOnlyList<string> RestartKeys(PulseConfig running, PulseConfig loaded) =>
        new (string Key, bool Changed)[]
        {
            (nameof(PulseConfig.Enabled), running.Enabled != loaded.Enabled),
            (nameof(PulseConfig.Bind), running.Bind != loaded.Bind),
            (nameof(PulseConfig.Port), running.Port != loaded.Port),
            (nameof(PulseConfig.RuntimeMetrics), running.RuntimeMetrics != loaded.RuntimeMetrics),
            (nameof(PulseConfig.ChunksRefreshSeconds), running.ChunksRefreshSeconds != loaded.ChunksRefreshSeconds),
        }
        .Where(key => key.Changed)
        .Select(key => key.Key)
        .ToList();

    public static string Switched(bool on, int burstTicks, int intervalSeconds) =>
        on
            ? $"Attribution is on: {Cycle(burstTicks, intervalSeconds)}. {NotWritten}"
            : "Attribution is off, and the engine's frame profiler with it, unless the engine's "
                + $"own /debug logticks still wants it running. {NotWritten}";

    public static string Status(bool on, int burstTicks, int intervalSeconds, long ticksProfiled, bool inBurst) =>
        on
            ? $"Attribution is on: {Cycle(burstTicks, intervalSeconds)}, {ticksProfiled} ticks profiled so far, "
                + Phase(inBurst)
            : $"Attribution is off. It would run {Cycle(burstTicks, intervalSeconds)}; "
                + $"{ticksProfiled} ticks profiled so far.";

    /// <param name="stratum">What the reload did to the Stratum timings, one of the sentences below, or
    /// null when there is nothing to say: the block is off and was not running, which is every server
    /// that never used it, whose reply stays what it always was.</param>
    public static string Reloaded(
        bool on, int burstTicks, int intervalSeconds, IReadOnlyList<string> restartKeys, string? stratum = null)
    {
        string attribution = on ? $"Attribution is on: {Cycle(burstTicks, intervalSeconds)}." : "Attribution is off.";
        string rest = restartKeys.Count switch
        {
            0 => "Nothing else in the file differs from what the server is running.",
            1 => $"{restartKeys[0]} differs from what the server is running and needs a restart.",
            _ => $"{string.Join(", ", restartKeys)} differ from what the server is running and need a restart.",
        };

        // Before the rest, whose "nothing else" is about everything said so far.
        if (stratum != null)
        {
            rest = $"{stratum} {rest}";
        }

        return $"Reloaded pulse.json. {attribution} {rest}";
    }

    // What a reload says about the Stratum timings, when the block is on or was running. One sentence
    // each, in the voice of the attribution ones: the state it is in now, and for a block that is on
    // and not running, why not.

    /// <summary>The block is on and timing.</summary>
    public static string StratumTimingsOn(int burstTicks, int intervalSeconds)
        => $"Stratum entity timings are on: {Cycle(burstTicks, intervalSeconds)}.";

    /// <summary>The block was running and the file has switched it off.</summary>
    public const string StratumTimingsOff = "Stratum entity timings are off.";

    /// <summary>The block is on, and this is not a Stratum server.</summary>
    public const string StratumTimingsAbsent = "Stratum entity timings need a Stratum server; this is not one.";

    /// <summary>The block is on, and this is a Stratum that Pulse cannot read. The reason is the
    /// binder's own, a clause with no full stop of its own.</summary>
    public static string StratumTimingsUnreadable(string reason)
        => $"Stratum entity timings cannot be read: {reason}.";

    /// <summary>The block is on, and Stratum threw earlier in this run, which switched the feature
    /// off until the server restarts.</summary>
    public const string StratumTimingsGivenUp = "Stratum entity timings are off for the rest of this run.";

    private static string Cycle(int burstTicks, int intervalSeconds)
        => $"bursts of {burstTicks} ticks every {intervalSeconds}s";

    private static string Phase(bool inBurst) => inBurst ? "profiling right now." : "waiting for the next burst.";
}
