using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>A pulse-otlp.json an admin's editor left broken: present on disk, but not JSON a parser
/// accepts. Exporting must stay off for this session rather than silently fall back to the mod's
/// own default endpoint, and the file must never be touched. The collector listens on the OTLP
/// default port, 4318: if a regression ever made the fallback config export instead of staying off,
/// this is the address it would reach.</summary>
[AtlasDataFiles("data/unreadable", TargetPath = "ModConfig")]
public class UnreadableConfigScenarios : AtlasScenarioBase, IDisposable
{
    private const string ErrorMarker = "Pulse OTLP could not read";
    private const int DefaultOtlpPort = 4318;

    // Assembly.Location, not AppContext.BaseDirectory: Atlas repoints the latter at the embedded
    // server's own data path once it boots, which by the time a scenario method runs is no longer
    // where this test assembly (and the fixture AtlasDataFiles copied from) actually lives.
    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(UnreadableConfigScenarios).Assembly.Location)!;

    private readonly FakeCollector collector;

    public UnreadableConfigScenarios()
    {
        // xUnit builds the test class before Atlas boots the host, so the collector is already
        // listening on the OTLP default port by the time StartServerSide would export to it.
        collector = new FakeCollector(DefaultOtlpPort);
    }

    public void Dispose()
    {
        collector.Dispose();
        GC.SuppressFinalize(this);
    }

    [AtlasScenario]
    public async Task Server_Runs_WithExportingOff_AndLeavesTheBrokenFileAlone()
    {
        string log = await ReadServerLog();

        string seedPath = Path.Combine(TestAssemblyDirectory, "data", "unreadable", "pulse-otlp.json");
        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        Assert.Equal(File.ReadAllBytes(seedPath), File.ReadAllBytes(configPath));

        Assert.Contains(ErrorMarker, log);
        Assert.Contains(configPath, log);

        // A bounded wait with nothing arriving is the closest thing to proving a negative: no
        // provider was ever built, so nothing on any timer ever reaches the collector.
        await World.Ticks(60);
        Assert.Null(collector.First);
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(ErrorMarker); attempt++)
        {
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
