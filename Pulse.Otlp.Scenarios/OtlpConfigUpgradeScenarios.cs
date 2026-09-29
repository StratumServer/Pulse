using System.Text.Json.Nodes;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>The same upgrade on the other mod's file: a pulse-otlp.json written before
/// <c>ServiceName</c> existed gets it, and keeps the endpoint the admin pointed it at. The base
/// mod is seeded complete and turned off, because nothing here is about what it serves; the OTLP
/// mod only needs it present, which its modinfo dependency requires anyway. Nothing listens on the
/// configured endpoint, and nothing needs to: an export that cannot connect is swallowed by the
/// SDK's own export thread.</summary>
[AtlasDataFiles("data/configupgrade", TargetPath = "ModConfig")]
public class OtlpConfigUpgradeScenarios : AtlasScenarioBase
{
    [AtlasScenario]
    public async Task Startup_Fills_ServiceName_IntoAnOlderConfigFile()
    {
        await World.Ticks(5);

        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        JsonObject config = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));

        Assert.Equal("vintagestory", (string?)config["ServiceName"]);
        Assert.Equal("http://127.0.0.1:39473", (string?)config["Endpoint"]);
        Assert.Equal(60, (int)config["IntervalSeconds"]!);
    }
}
