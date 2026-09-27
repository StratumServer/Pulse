using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>A pulse.json an admin's editor left broken: present on disk, but not JSON a parser
/// accepts. The mod must run this session on its built-in defaults rather than fail to start, and
/// it must never touch a file it could not make sense of.</summary>
[AtlasDataFiles("data/unreadable/pulse.json", TargetPath = "ModConfig")]
public class UnreadableConfigScenarios : AtlasScenarioBase
{
    private const string ErrorMarker = "Pulse could not read";

    // The default: proof that a malformed file gets no configured values at all, defaults
    // included. Nothing else in either scenario suite binds this port.
    private const int DefaultPort = 9464;

    // Assembly.Location, not AppContext.BaseDirectory: Atlas repoints the latter at the embedded
    // server's own data path once it boots, which by the time a scenario method runs is no longer
    // where this test assembly (and the fixture AtlasDataFiles copied from) actually lives.
    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(UnreadableConfigScenarios).Assembly.Location)!;

    [AtlasScenario]
    public async Task Server_Runs_OnDefaults_AndLeavesTheBrokenFileAlone()
    {
        string log = await ReadServerLog();

        string seedPath = Path.Combine(TestAssemblyDirectory, "data", "unreadable", "pulse.json");
        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");
        Assert.Equal(File.ReadAllBytes(seedPath), File.ReadAllBytes(configPath));

        Assert.Contains(ErrorMarker, log);
        Assert.Contains(configPath, log);

        // Defaults took over regardless: the endpoint is up on the port nothing in the broken file
        // could have named, because none of it was ever read.
        Assert.Contains("pulse_server_ticks_total", await Scrape.Metrics(DefaultPort));
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(ErrorMarker); attempt++)
        {
            // The engine's logger writes on its own thread; pump the world instead of sleeping.
            await World.Ticks(10);
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
