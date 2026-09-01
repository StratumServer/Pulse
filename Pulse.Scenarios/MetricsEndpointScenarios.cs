using System.Net;
using System.Net.Http;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>The endpoint on a live server: it answers, it counts real ticks, and it sees real
/// players. The seeded pulse.json pins the port these scenarios scrape.</summary>
[AtlasDataFiles("data/endpoint/pulse.json", TargetPath = "ModConfig")]
public class MetricsEndpointScenarios : AtlasScenarioBase
{
    private const int Port = 39464;

    private static readonly string[] Families =
    [
        "pulse_server_ticks_total",
        "pulse_server_tick_seconds",
        "pulse_players_online",
        "pulse_entities_loaded",
        "pulse_server_tick_budget_seconds",
    ];

    [AtlasScenario]
    public async Task Metrics_Serves_EveryFamily_OnALiveServer()
    {
        await World.Ticks(5);

        using HttpResponseMessage response = await Scrape.Get(Port, "/metrics");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        foreach (string family in Families)
        {
            Assert.Contains("# TYPE " + family + " ", body);
        }

        Assert.Contains("pulse_server_tick_seconds_bucket{le=\"+Inf\"}", body);
        Assert.Contains("pulse_server_tick_seconds_count", body);

        // A French locale on the host must not leak a comma decimal separator into the wire.
        foreach (string line in body.Split('\n'))
        {
            if (!line.StartsWith('#'))
            {
                Assert.DoesNotContain(",", line);
            }
        }
    }

    [AtlasScenario]
    public async Task Metrics_Returns404_ForAnythingElse()
    {
        using HttpResponseMessage response = await Scrape.Get(Port, "/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AtlasScenario]
    public async Task TickCounter_Grows_AsTheWorldRuns()
    {
        double before = Scrape.Value(await Scrape.Metrics(Port), "pulse_server_ticks_total");
        await World.Ticks(30);
        double after = Scrape.Value(await Scrape.Metrics(Port), "pulse_server_ticks_total");

        Assert.True(after > before, $"ticks did not advance: {before} then {after}");
    }

    [AtlasScenario]
    public async Task TickBudget_Reports_TheServerConfiguredBudget()
    {
        double budget = Scrape.Value(await Scrape.Metrics(Port), "pulse_server_tick_budget_seconds");

        // 30 TPS nominal by default; assert a plausible range rather than the exact float, since
        // an operator (or a fork) can retune the tick rate.
        Assert.InRange(budget, 0.001, 1.0);
    }

    [AtlasScenario]
    public async Task PlayersOnline_Counts_AJoinedPlayer()
    {
        await World.JoinPlayer("pulse-tester");

        // The gauge reads a snapshot the tick listener refreshes about once a second, so the
        // join needs a second of ticks to show up. 30 ticks is roughly that second.
        string body = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await World.Ticks(30);
            body = await Scrape.Metrics(Port);
            if (Scrape.Value(body, "pulse_players_online") == 1)
            {
                return;
            }
        }

        Assert.Fail("pulse_players_online never reported the joined player:\n" + body);
    }
}
