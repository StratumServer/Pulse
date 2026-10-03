using System.Text.Json.Nodes;
using Atlas.XUnit;
using Pulse.Scenarios;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>The same upgrade on the other mod's file: a pulse-otlp.json written before
/// <c>ServiceName</c> and <c>ServiceInstanceId</c> existed gets both, and keeps the endpoint the
/// admin pointed it at. The base mod is seeded complete and turned off, because nothing here is
/// about what it serves; the OTLP mod only needs it present, which its modinfo dependency requires
/// anyway. Nothing listens on the configured endpoint, and nothing needs to: an export that cannot
/// connect is swallowed by the SDK's own export thread.</summary>
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
        Assert.Equal("http://127.0.0.1:29473", (string?)config["Endpoint"]);
        Assert.Equal(60, (int)config["IntervalSeconds"]!);
    }

    /// <summary>The key that came after, written with the id the server generated for it rather than
    /// left blank: a GUID in the file is what the next start finds there and exports again, which is
    /// the whole point of having it written at all. That the id written is the id exported is
    /// <see cref="OtlpGrpcExportScenarios.Export_Carries_TheGeneratedServiceInstanceId"/>'s
    /// question.</summary>
    [AtlasScenario]
    public async Task Startup_Writes_AGeneratedServiceInstanceId_IntoAnOlderConfigFile()
    {
        await World.Ticks(5);

        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        JsonObject config = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));

        Assert.True(
            Guid.TryParse((string?)config["ServiceInstanceId"], out Guid id) && id != Guid.Empty,
            $"ServiceInstanceId is not a generated GUID: {config["ServiceInstanceId"]}");

        // Reported the way any key an upgrade adds is, as the changelog says.
        string log = await ServerLog.WaitFor(World, "added these keys");
        Assert.Contains(
            "Pulse OTLP added these keys to pulse-otlp.json with their defaults: ServiceName, ServiceInstanceId.", log);
    }
}
