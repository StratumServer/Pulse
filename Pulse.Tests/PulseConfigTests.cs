using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Pulse.Tests;

/// <summary>The defaults ModConfig/pulse.json ships with before an admin ever edits it: enabled,
/// loopback-only, runtime metrics on.</summary>
public class PulseConfigTests
{
    [Fact]
    public void Defaults_Enable_TheEndpoint_OnLoopback_WithRuntimeMetricsOn()
    {
        PulseConfig config = new();

        Assert.True(config.Enabled);
        Assert.Equal("127.0.0.1", config.Bind);
        Assert.True(config.RuntimeMetrics);
    }

    /// <summary>Stratum's recording is the costly part, so the block is off until an admin asks, and
    /// then it runs on the same duty cycle attribution does.</summary>
    [Fact]
    public void Defaults_Leave_TheStratumTimingsOff_OnTheDutyCycleAttributionUses()
    {
        PulseConfig config = new();

        Assert.False(config.StratumTimings.Enabled);
        Assert.Equal(10, config.StratumTimings.BurstTicks);
        Assert.Equal(10, config.StratumTimings.IntervalSeconds);
        Assert.Equal(config.Attribution.BurstTicks, config.StratumTimings.BurstTicks);
        Assert.Equal(config.Attribution.IntervalSeconds, config.StratumTimings.IntervalSeconds);
    }

    /// <summary>The keys an admin reads in a fresh file. StoreModConfig writes what the game's own
    /// serializer makes of the config, which needs a Newtonsoft this test project does not carry, so
    /// the framework's stands in: for a plain class the two agree on the names and on their order, the
    /// order the properties are declared in.</summary>
    [Fact]
    public void AFreshFile_Carries_TheStratumTimingsBlock_AfterAttribution_WithItsDefaults()
    {
        JsonObject written = Assert.IsType<JsonObject>(JsonNode.Parse(JsonSerializer.Serialize(new PulseConfig())));

        Assert.Equal(
            ["Enabled", "Bind", "Port", "RuntimeMetrics", "ChunksRefreshSeconds", "Attribution", "StratumTimings"],
            written.Select(entry => entry.Key));
        Assert.Equal(
            """{"Enabled":false,"BurstTicks":10,"IntervalSeconds":10}""",
            written["StratumTimings"]!.ToJsonString());
    }
}
