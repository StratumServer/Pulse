using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>A pulse-otlp.json an admin's editor left broken: present on disk, but not JSON a parser
/// accepts. Exporting must stay off for this session rather than silently fall back to the mod's
/// own default endpoint, and the file must never be touched.</summary>
[AtlasDataFiles("data/unreadable", TargetPath = "ModConfig")]
public class UnreadableConfigScenarios : AtlasScenarioBase
{
    private const string ErrorMarker = "Pulse OTLP could not read";

    // Logged once, after every mod's StartServerSide has returned (RunModPhase in VintagestoryLib
    // logs it right after the loop over every ModSystem). Waiting for it, rather than for a fixed
    // number of ticks, means the OTLP mod has already decided whether to export or not by the time
    // the log is read: "Pulse OTLP exporting ..." is logged synchronously from inside
    // StartServerSide when a provider is built, never on the periodic export timer, so its absence
    // here is not a race against the (60 second, by default) export interval.
    private const string BootCompleteMarker = "systems on Server:";

    // Assembly.Location, not AppContext.BaseDirectory: Atlas repoints the latter at the embedded
    // server's own data path once it boots, which by the time a scenario method runs is no longer
    // where this test assembly (and the fixture AtlasDataFiles copied from) actually lives.
    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(UnreadableConfigScenarios).Assembly.Location)!;

    [AtlasScenario]
    public async Task Server_Runs_WithExportingOff_AndLeavesTheBrokenFileAlone()
    {
        string log = await ReadServerLog();

        string seedPath = Path.Combine(TestAssemblyDirectory, "data", "unreadable", "pulse-otlp.json");
        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        Assert.Equal(File.ReadAllBytes(seedPath), File.ReadAllBytes(configPath));

        Assert.Contains(ErrorMarker, log);
        Assert.Contains(configPath, log);

        // The mod only ever logs this line once it has actually built a provider, so its absence
        // after boot has finished is proof exporting never started, not a timing guess.
        Assert.DoesNotContain("Pulse OTLP exporting", log);
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(BootCompleteMarker); attempt++)
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
