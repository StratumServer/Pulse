using Vintagestory.API.Common;

namespace Pulse;

/// <summary>Turns one log entry into the labels Pulse counts it under, and nothing else.</summary>
/// <remarks>Pure and world-free on purpose. The <c>ILogger.EntryAdded</c> handler that calls this
/// runs on whatever thread wrote the entry, engine threads included, so the classification has to
/// be cheap and thread-safe; keeping it here also makes it testable without booting a server.</remarks>
public static class LogClassifier
{
    /// <summary>Severities counted by <c>pulse_log_entries_total</c>, in exposition order.</summary>
    public static readonly IReadOnlyList<string> Levels = ["warning", "error", "fatal"];

    /// <summary>Kinds counted by <c>pulse_engine_warnings_total</c>, in exposition order.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["overload", "memory", "suspend_timeout", "autosave_io", "physics_skip"];

    /// <summary>The engine's own warning strings, byte-identical at 1.22.3 and 1.22.7, each matched
    /// on the stable head of the sentence. EntryAdded hands over the format string before argument
    /// substitution, so the {0} in the overload warning, the megabyte figure in the memory one and
    /// the tick count in the physics skip one never reach this table. Verified by decompiling
    /// ServerMain.Process, ServerMain.Suspend, ServerSystemMonitor.OnEvery60sec,
    /// ServerSystemAutoSaveGame.doAutoSave and PhysicsManager.ServerTick.</summary>
    private static readonly (string Prefix, string Kind)[] EngineWarnings =
    [
        ("Server overloaded. A tick took", "overload"),
        ("The server is currently using more than 90% of its maximum allowed memory", "memory"),
        ("Server suspend requested, but reached max wait time", "suspend_timeout"),
        ("Call to autosave, but server is already saving", "autosave_io"),
        ("Over 400ms tick. Skipping", "physics_skip"),
    ];

    /// <summary>The severity label for an entry Pulse counts, or null for the levels it ignores
    /// (chat, debug, notification and the rest, which say nothing about server health).</summary>
    public static string? Level(EnumLogType type) => type switch
    {
        EnumLogType.Warning => "warning",
        EnumLogType.Error => "error",
        EnumLogType.Fatal => "fatal",
        _ => null,
    };

    /// <summary>The engine health warning this entry is, or null for any other line.</summary>
    public static string? EngineWarning(EnumLogType type, string? format)
    {
        if (type != EnumLogType.Warning || format == null)
        {
            return null;
        }

        foreach ((string prefix, string kind) in EngineWarnings)
        {
            if (format.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }
}
