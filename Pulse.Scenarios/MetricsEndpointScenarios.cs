using System.Net;
using System.Net.Http;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>The endpoint on a live server: it answers, it counts real ticks, and it sees real
/// players, real worldgen and the runtime underneath. The seeded pulse.json pins the port these
/// scenarios scrape and drops ChunksRefreshSeconds to a second, so the slow gauge reports inside
/// a scenario rather than half a minute later.</summary>
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
        "pulse_worldgen_queue_columns",
        "pulse_worldgen_columns_generated_total",
        "pulse_chunks_loaded",
        "pulse_log_entries_total",
        "pulse_engine_warnings_total",
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

        // A French locale on the host must not leak a comma decimal separator into the wire. The
        // value is everything after the last space; commas inside a {label="..."} set are the
        // format's own separator and legitimate.
        foreach (string line in body.Split('\n'))
        {
            if (!line.StartsWith('#') && line.Length > 0)
            {
                Assert.DoesNotContain(",", line[(line.LastIndexOf(' ') + 1)..]);
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

    [AtlasScenario]
    public async Task WorldgenCounter_Counts_TheColumnsAFreshWorldGenerated()
    {
        await World.Ticks(5);

        // The scenario world is generated, not loaded from a fixture, so MapChunkGeneration has
        // fired for every spawn column before the first scrape.
        double columns = Scrape.Value(await Scrape.Metrics(Port), "pulse_worldgen_columns_generated_total");

        Assert.True(columns > 0, $"a freshly generated world reported no generated columns: {columns}");
    }

    [AtlasScenario]
    public async Task ChunksLoaded_Reports_OnItsOwnSlowCadence()
    {
        // The gauge honestly reads 0 until the slow listener first fires, one second in here.
        string body = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await World.Ticks(30);
            body = await Scrape.Metrics(Port);
            if (Scrape.Value(body, "pulse_chunks_loaded") > 0)
            {
                return;
            }
        }

        Assert.Fail("pulse_chunks_loaded never left zero:\n" + body);
    }

    [AtlasScenario]
    public async Task LogCounters_Carry_ASeriesPerSeverityAndPerEngineWarning()
    {
        string body = await Scrape.Metrics(Port);

        // Seeded at zero on startup, so a healthy server still exposes every series.
        foreach (string level in new[] { "warning", "error", "fatal" })
        {
            Assert.Contains($"pulse_log_entries_total{{level=\"{level}\"}} ", body);
        }

        foreach (string kind in new[] { "overload", "memory", "suspend_timeout", "autosave_io" })
        {
            Assert.Contains($"pulse_engine_warnings_total{{kind=\"{kind}\"}} ", body);
        }
    }

    [AtlasScenario]
    public async Task RuntimeMetrics_Serve_TheDotnetFamilies_WhenTheConfigAsksForThem()
    {
        string body = await Scrape.Metrics(Port);

        Assert.Contains("# TYPE dotnet_gc_collections_total counter\n", body);
        Assert.Contains("dotnet_gc_collections_total{gc_heap_generation=\"gen0\"} ", body);
        Assert.Contains("# TYPE dotnet_process_memory_working_set gauge\n", body);
    }
}
