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
    private const int Port = 29473;

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
        Assert.StartsWith(
            "Attribution is off, and the engine's frame profiler with it, unless the engine's "
                + "own /debug logticks still wants it running.",
            stop.Message);

        await World.Ticks(5);

        // The flag actually went back down. Leaving it up would charge every later tick close to
        // a quarter of the budget for a tree nobody reads.
        Assert.False(World.Api.World.FrameProfiler.Enabled);

        // The share family is gone, not frozen at the last burst it measured: a dashboard plotting
        // it would otherwise keep showing a mod cost that stopped being true the moment this
        // command replied. The tick and dropped-sample counters are untouched: they are cumulative
        // and simply stop moving, which the loop below checks.
        string stopped = await Scrape.Metrics(Port);
        Assert.DoesNotContain("pulse_mod_tick_share", stopped);

        long profiled = (long)Scrape.Value(stopped, "pulse_attribution_ticks_total");
        await World.Ticks(300);
        Assert.Equal(profiled, (long)Scrape.Value(await Scrape.Metrics(Port), "pulse_attribution_ticks_total"));

        CommandResult after = await World.ExecuteCommand("/pulse attribution status");
        Assert.Equal(
            $"Attribution is off. It would run bursts of 5 ticks every 1s; {profiled} ticks profiled so far.",
            after.Message);
    }

    /// <summary>The release review's new defect: <c>RunProfiledTick</c> used to write the engine's
    /// frame profiler flag only on a transition it saw within the same tick, so a duty cycle
    /// restarted between ticks (by <c>Switch</c> or a reload, both of which call
    /// <c>TickAttribution.Apply</c> outside <c>Tick</c>) went unnoticed and the flag never came
    /// back down. A large burst, not the five ticks the rest of this class shares, so there is a
    /// comfortable window to land <c>/pulse attribution off</c> before the burst would have ended
    /// on its own regardless.</summary>
    private static string LargeBurstConfig() =>
        $$"""
        {
          "Enabled": true,
          "Bind": "127.0.0.1",
          "Port": {{Port}},
          "RuntimeMetrics": false,
          "ChunksRefreshSeconds": 30,
          "Attribution": {
            "Enabled": true,
            "BurstTicks": 300,
            "IntervalSeconds": 1
          }
        }
        """;

    [AtlasScenario]
    public async Task Attribution_Off_DuringABurst_TurnsTheProfilerOff_RatherThanForTheRestOfTheRun()
    {
        await World.Ticks(5);
        string path = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");
        File.WriteAllText(path, LargeBurstConfig());

        CommandResult reloaded = await World.ExecuteCommand("/pulse reload");
        Assert.True(reloaded.Ok, reloaded.Message);

        // Past the one second interval and the discarded warm-up sample, comfortably short of the
        // 300-tick burst this fixture configures: genuinely mid-burst, not caught right at either
        // end of it.
        await World.Ticks(45);

        CommandResult off = await World.ExecuteCommand("/pulse attribution off");
        Assert.True(off.Ok, off.Message);
        Assert.StartsWith(
            "Attribution is off, and the engine's frame profiler with it, unless the engine's "
                + "own /debug logticks still wants it running.",
            off.Message);

        await World.Ticks(5);

        // The regression: a write keyed on comparing Profiling only before and after one tick
        // never saw the restart Switch(false) makes outside Tick, so this stayed true for the
        // rest of the run instead of coming down the moment the reply above claimed it had.
        Assert.False(World.Api.World.FrameProfiler.Enabled);
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
