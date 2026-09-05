using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>Switching attribution on and off against a real engine, which is the only place the
/// interesting half can be proven: that a server which booted with attribution off can still turn
/// the engine's frame profiler on part-way through its life without dying, and turn it back off
/// again. The unit suite can check the state machine; only a live server can check that the
/// priming done at startup is what makes the later switch safe.
/// <para>One scenario rather than several, because scenarios in a class share a world and this one
/// is a sequence: off, on, measuring, off again.</para></summary>
[AtlasDataFiles("data/hottoggle/pulse.json", TargetPath = "ModConfig")]
public class HotToggleScenarios : AtlasScenarioBase
{
    private const int Port = 39473;

    [AtlasScenario]
    public async Task Attribution_SwitchesOnAndOff_WithoutARestart()
    {
        await World.Ticks(5);

        // Armed, not running. The four families are registered so the switch has something to
        // record into, and an instrument nothing has recorded into is not a series: a server that
        // never asks for attribution serves the exposition it always did.
        string idle = await Scrape.Metrics(Port);
        Assert.DoesNotContain("pulse_mod_tick_share", idle);
        Assert.DoesNotContain("pulse_attribution_ticks_total", idle);

        CommandResult off = await World.ExecuteCommand("/pulse attribution status");
        Assert.True(off.Ok, off.Message);
        Assert.Equal(
            "Attribution is off. It would run bursts of 5 ticks every 1s; 0 ticks profiled so far.",
            off.Message);

        CommandResult on = await World.ExecuteCommand("/pulse attribution on");
        Assert.True(on.Ok, on.Message);
        Assert.Equal(
            "Attribution is on: bursts of 5 ticks every 1s. pulse.json was not changed, so the "
                + "file decides again after a restart.",
            on.Message);

        // Seeded the moment it is switched on, rather than a burst later: a family that shows up
        // mid-scrape is a family no dashboard plots.
        string armed = await Scrape.Metrics(Port);
        Assert.Contains("pulse_mod_tick_share{modid=\"engine\"} ", armed);
        Assert.Equal(0, Scrape.Value(armed, "pulse_attribution_ticks_total"));

        string body = await Burst();

        // The server survived a profiler switched on mid-life, the marks parsed, and Pulse found
        // itself in its own numbers.
        Assert.InRange(Share(body, "pulse"), double.Epsilon, 1.0);

        CommandResult stop = await World.ExecuteCommand("/pulse attribution off");
        Assert.True(stop.Ok, stop.Message);
        Assert.StartsWith("Attribution is off, and the engine's frame profiler with it.", stop.Message);

        await World.Ticks(5);

        // The flag actually went back down. Leaving it up would charge every later tick a few
        // percent for a tree nobody reads.
        Assert.False(World.Api.World.FrameProfiler.Enabled);

        long profiled = (long)Scrape.Value(await Scrape.Metrics(Port), "pulse_attribution_ticks_total");
        await World.Ticks(300);
        Assert.Equal(profiled, (long)Scrape.Value(await Scrape.Metrics(Port), "pulse_attribution_ticks_total"));

        CommandResult after = await World.ExecuteCommand("/pulse attribution status");
        Assert.Equal(
            $"Attribution is off. It would run bursts of 5 ticks every 1s; {profiled} ticks profiled so far.",
            after.Message);
    }

    /// <summary>Ticks until a burst has completed, or gives up and fails with the body it last
    /// saw. A burst needs its interval, then a discarded sample, then five profiled ticks.</summary>
    private async Task<string> Burst()
    {
        string body = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await World.Ticks(30);
            body = await Scrape.Metrics(Port);
            if (Scrape.Value(body, "pulse_attribution_ticks_total") > 0)
            {
                return body;
            }
        }

        Assert.Fail("no burst ever completed:\n" + body);
        return body;
    }

    private static double Share(string exposition, string modid)
        => Scrape.Value(exposition, $"pulse_mod_tick_share{{modid=\"{modid}\"}}");
}
