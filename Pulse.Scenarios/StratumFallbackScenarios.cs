using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>The Stratum timings switched on, on a server that is not Stratum, which is the only kind
/// this suite boots: the game's own, whose API assembly has no accumulator for Pulse to bind. The mod
/// has to say so once and carry on as it always has, with none of the five families served and
/// nothing else changed. The fixture asks for a burst of five ticks a second apart, so a feature that
/// did run would have run many times inside the scenario.
/// <para>What the families look like on a real Stratum is the Stratum lane's to prove, not this
/// suite's.</para></summary>
[AtlasDataFiles("data/stratumfallback/pulse.json", TargetPath = "ModConfig")]
public class StratumFallbackScenarios : AtlasScenarioBase
{
    private const int Port = 29486;

    /// <summary>A phrase of the one notification, which the engine puts the mod's id in front of.</summary>
    private const string Marker = "[pulse] StratumTimings.Enabled is set in pulse.json, but per-behavior timings need a Stratum server";

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

        // A reload with the block still on is not a new fact about the server, so no new line, and its
        // reply is the one it has always been.
        CommandResult reloaded = await World.ExecuteCommand("/pulse reload");

        Assert.True(reloaded.Ok, reloaded.Message);
        Assert.Equal(
            "Reloaded pulse.json. Attribution is off. Nothing else in the file differs from what the server is running.",
            reloaded.Message);

        await World.Ticks(60);

        Assert.Equal(1, Occurrences(await ServerLog.WaitFor(World, Marker), Marker));
        Assert.DoesNotContain("pulse_stratum_", await Scrape.Metrics(Port));
    }
}
