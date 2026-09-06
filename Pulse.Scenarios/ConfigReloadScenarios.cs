using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary><c>/pulse reload</c> against a real server: the file on disk is rewritten under a
/// running server and read back through the same loader startup uses.</summary>
[AtlasDataFiles("data/reload/pulse.json", TargetPath = "ModConfig")]
public class ConfigReloadScenarios : AtlasScenarioBase
{
    private const int Port = 39474;

    private static string Config(int port, bool attribution, int burstTicks) =>
        $$"""
        {
          "Enabled": true,
          "Bind": "127.0.0.1",
          "Port": {{port}},
          "RuntimeMetrics": false,
          "ChunksRefreshSeconds": 30,
          "Attribution": {
            "Enabled": {{(attribution ? "true" : "false")}},
            "BurstTicks": {{burstTicks}},
            "IntervalSeconds": 1
          }
        }
        """;

    [AtlasScenario]
    public async Task Reload_Applies_TheAttributionBlock_AndNamesWhatItCannot()
    {
        await World.Ticks(5);
        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");

        File.WriteAllText(path, Config(Port, attribution: true, burstTicks: 7));
        CommandResult applied = await World.ExecuteCommand("/pulse reload");

        Assert.True(applied.Ok, applied.Message);
        Assert.Equal(
            "Reloaded pulse.json. Attribution is on: bursts of 7 ticks every 1s. "
                + "Nothing else in the file differs from what the server is running.",
            applied.Message);

        // Not the tick count: the server is ticking while this reads, so a burst may well have
        // landed between the reload and the question.
        CommandResult status = await World.ExecuteCommand("/pulse attribution status");
        Assert.StartsWith("Attribution is on: bursts of 7 ticks every 1s,", status.Message);

        // A key that was wired into a socket at startup. Reload cannot move it, and the reply has
        // to say which one rather than leave the operator wondering why nothing happened.
        File.WriteAllText(path, Config(19999, attribution: true, burstTicks: 7));
        CommandResult needsRestart = await World.ExecuteCommand("/pulse reload");

        Assert.EndsWith("Port differs from what the server is running and needs a restart.", needsRestart.Message);
        Assert.Contains("pulse_server_ticks_total", await Scrape.Metrics(Port));

        // And a file nobody can read changes nothing at all.
        File.WriteAllText(path, "this is not json");
        CommandResult broken = await World.ExecuteCommand("/pulse reload");

        Assert.False(broken.Ok);
        Assert.StartsWith("Pulse could not read pulse.json (", broken.Message);
        Assert.EndsWith("Nothing changed: the server is still running the config it booted with.", broken.Message);

        CommandResult unchanged = await World.ExecuteCommand("/pulse attribution status");
        Assert.StartsWith("Attribution is on: bursts of 7 ticks every 1s,", unchanged.Message);
    }
}
