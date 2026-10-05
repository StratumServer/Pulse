using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>The Stratum timings switched on, on a server that is not Stratum, which is the only kind
/// this suite boots: the game's own, whose API assembly has no accumulator for Pulse to bind. The mod
/// has to say so once in the log and carry on as it always has, with none of the five families served
/// and nothing else changed, and a reload has to say so in its reply instead of claiming that nothing
/// else in the file differs from what runs. The fixture asks for a burst of five ticks a second
/// apart, so a feature that did run would have run many times inside the scenario.
/// <para>What the families look like on a real Stratum is the Stratum lane's to prove, not this
/// suite's.</para></summary>
[AtlasDataFiles("data/stratumfallback/pulse.json", TargetPath = "ModConfig")]
public class StratumFallbackScenarios : AtlasScenarioBase
{
    private const int Port = 29486;

    /// <summary>A phrase of the one notification, which the engine puts the mod's id in front of.</summary>
    private const string Marker = "[pulse] Pulse cannot serve Stratum's entity timings here: StratumTimings.Enabled is set in pulse.json, but they need a Stratum server";

    /// <summary>The fixture as an admin would write it again, with the block on or off.</summary>
    private static string Config(bool block) =>
        $$"""
        {
          "Enabled": true,
          "Bind": "127.0.0.1",
          "Port": {{Port}},
          "RuntimeMetrics": false,
          "ChunksRefreshSeconds": 30,
          "Attribution": {
            "Enabled": false,
            "BurstTicks": 5,
            "IntervalSeconds": 1
          },
          "StratumTimings": {
            "Enabled": {{(block ? "true" : "false")}},
            "BurstTicks": 5,
            "IntervalSeconds": 1
          }
        }
        """;

    private static int Occurrences(string text, string marker)
    {
        int count = 0;
        for (int at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [AtlasScenario]
    public async Task StratumTimings_OnAServerThatIsNotStratum_SayOnceThatTheyAreNotServed_AndChangeNothingElse()
    {
        // About ten seconds at the default tick rate: nine bursts of the fixture's cycle, had one run.
        await World.Ticks(300);

        string body = await Scrape.Metrics(Port);

        Assert.DoesNotContain("pulse_stratum_", body);

        // Everything else is what a server without the block serves: the endpoint, the engine's own
        // accounting, and not a trace of attribution, which this fixture leaves off.
        Assert.Contains("# TYPE pulse_server_ticks_total ", body);
        Assert.Contains("# TYPE pulse_server_tick_busy_seconds ", body);
        Assert.DoesNotContain("pulse_mod_tick_share", body);

        string log = await ServerLog.WaitFor(World, Marker);
        Assert.Equal(1, Occurrences(log, Marker));

        // A reload with the block still on is not a new fact about the server, so no new line in the
        // log. Its reply says what the block is doing, which is nothing, before it says that nothing
        // else differs: the file asks for something the server is not running.
        CommandResult reloaded = await World.ExecuteCommand("/pulse reload");

        Assert.True(reloaded.Ok, reloaded.Message);
        Assert.Equal(
            "Reloaded pulse.json. Attribution is off. Stratum entity timings need a Stratum server; this is not one. "
                + "Nothing else in the file differs from what the server is running.",
            reloaded.Message);

        await World.Ticks(60);

        Assert.Equal(1, Occurrences(await ServerLog.WaitFor(World, Marker), Marker));
        Assert.DoesNotContain("pulse_stratum_", await Scrape.Metrics(Port));

        // The block switched off again was never running, so the reply is the one every server that
        // never used it has always got.
        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");
        File.WriteAllText(path, Config(block: false));
        CommandResult switchedOff = await World.ExecuteCommand("/pulse reload");

        Assert.True(switchedOff.Ok, switchedOff.Message);
        Assert.Equal(
            "Reloaded pulse.json. Attribution is off. Nothing else in the file differs from what the server is running.",
            switchedOff.Message);
    }
}
