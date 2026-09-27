using Atlas.Api;
using Vintagestory.API.Config;

namespace Pulse.Scenarios;

/// <summary>Reads the embedded server's own log file, the way more than one scenario class needs
/// to when what it is proving only shows up there rather than on the metrics endpoint.</summary>
internal static class ServerLog
{
    private const int MaxAttempts = 10;

    /// <summary>Reads server-main.log, pumping ticks until every one of <paramref name="markers"/>
    /// has appeared or the attempts run out.</summary>
    /// <remarks>The engine's logger writes on its own thread, so a line set during boot is not
    /// guaranteed to be on disk on the very first read.</remarks>
    public static async Task<string> WaitFor(IWorldSession world, params string[] markers)
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < MaxAttempts && !markers.All(text.Contains); attempt++)
        {
            await world.Ticks(10);
            text = ReadShared(path);
        }

        return text;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
