using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>The other side of the RuntimeMetrics flag: with it off the exporter listens to
/// Pulse's meter alone. Its own fixture and port, because the flag is read once at boot.</summary>
[AtlasDataFiles("data/runtimeoff/pulse.json", TargetPath = "ModConfig")]
public class RuntimeMetricsOffScenarios : AtlasScenarioBase
{
    private const int Port = 39467;

    [AtlasScenario]
    public async Task RuntimeMetrics_Off_Serves_PulseFamiliesAndNoDotnetOnes()
    {
        await World.Ticks(5);

        string body = await Scrape.Metrics(Port);

        Assert.Contains("# TYPE pulse_server_ticks_total counter\n", body);
        Assert.DoesNotContain("dotnet_", body);
    }
}
