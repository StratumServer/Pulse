using System.Text.Json.Nodes;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>An admin's own pulse.json, brought up to date by a real server booting on it. The
/// seeded file is what an upgrade actually looks like: one key the admin set, nothing else the
/// current version declares, and one key that answers to nothing.</summary>
[AtlasDataFiles("data/configupgrade/pulse.json", TargetPath = "ModConfig")]
public class ConfigUpgradeScenarios : AtlasScenarioBase
{
    private const int Port = 39472;

    [AtlasScenario]
    public async Task Startup_Fills_AnOlderConfigFile_WithoutLosingWhatTheAdminSet()
    {
        await World.Ticks(5);

        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");
        JsonObject config = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));

        // The admin's one setting, still theirs on disk and still what the endpoint bound: a
        // rewrite that reset it to the default would be the worst possible outcome here.
        Assert.Equal(Port, (int)config["Port"]!);
        Assert.Contains("pulse_server_ticks_total", await Scrape.Metrics(Port));

        // The block that arrived after 0.1.0, written out with the defaults the class declares.
        JsonObject attribution = Assert.IsType<JsonObject>(config["Attribution"]);
        Assert.False((bool)attribution["Enabled"]!);
        Assert.Equal(30, (int)attribution["BurstTicks"]!);
        Assert.Equal(10, (int)attribution["IntervalSeconds"]!);

        // And the rest of the keys the file never had.
        Assert.True((bool)config["Enabled"]!);
        Assert.Equal("127.0.0.1", (string?)config["Bind"]);
        Assert.True((bool)config["RuntimeMetrics"]!);
        Assert.Equal(30, (int)config["ChunksRefreshSeconds"]!);

        // The key nothing in PulseConfig answers to does not survive the rewrite, which is why the
        // mod warns about it rather than dropping it quietly.
        Assert.Null(config["Colour"]);
    }
}
