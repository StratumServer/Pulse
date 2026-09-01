using System.Net;
using System.Net.Http;
using Atlas.Api;
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
        "pulse_server_uptime_seconds",
        "pulse_player_ping_seconds",
        "pulse_network_sent_bytes_total",
        "pulse_network_received_bytes_total",
        "pulse_player_deaths_total",
        "pulse_server_suspends_total",
        "pulse_server_suspend_seconds_total",
    ];

    /// <summary>The families that only exist when the cast to the concrete engine type worked.
    /// The embedded server Atlas boots is a real ServerMain, so all of them have to be here; if
    /// one goes missing, the probe degraded and the scenario has caught exactly what it is for.</summary>
    private static readonly string[] EngineFamilies =
    [
        "pulse_server_tick_busy_seconds",
        "pulse_network_packets_per_second",
        "pulse_network_bytes_per_second",
        "pulse_connection_queue_clients",
        "pulse_network_udp_sent_bytes_total",
        "pulse_network_udp_received_bytes_total",
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

    /// <summary>The whole point of the engine probe: these six families do not exist through the
    /// modding API at all, and their presence here is proof the cast resolved against a real
    /// server rather than a mock.</summary>
    [AtlasScenario]
    public async Task EngineProbe_Serves_TheFamiliesThePublicApiCannotProduce()
    {
        await World.Ticks(5);

        string body = await Scrape.Metrics(Port);

        foreach (string family in EngineFamilies)
        {
            Assert.Contains("# TYPE " + family + " ", body);
        }

        Assert.Contains("pulse_network_packets_per_second{channel=\"tcp\"} ", body);
        Assert.Contains("pulse_network_packets_per_second{channel=\"udp\"} ", body);
        Assert.Contains("pulse_network_bytes_per_second{channel=\"tcp\"} ", body);
        Assert.Contains("pulse_network_bytes_per_second{channel=\"udp\"} ", body);
    }

    [AtlasScenario]
    public async Task TickBusyTime_Reports_APlausibleShareOfTheBudget()
    {
        // Two seconds of ticks: the engine only rotates its statistics buckets that often, and
        // Pulse reads the one behind the live bucket.
        await World.Ticks(90);

        string body = await Scrape.Metrics(Port);
        double busy = Scrape.Value(body, "pulse_server_tick_busy_seconds");
        double budget = Scrape.Value(body, "pulse_server_tick_budget_seconds");

        // Not asserted above zero on purpose: the engine accumulates whole milliseconds per tick,
        // so an idle server with sub-millisecond ticks legitimately averages zero. What matters is
        // that the number is there and is not nonsense.
        Assert.InRange(busy, 0, 1.0);
        Assert.True(busy < budget * 10, $"tick busy time {busy}s is implausible against a {budget}s budget");
        Assert.Equal(0, Scrape.Value(body, "pulse_connection_queue_clients"));
    }

    [AtlasScenario]
    public async Task Uptime_Grows_AsTheServerRuns()
    {
        double before = Scrape.Value(await Scrape.Metrics(Port), "pulse_server_uptime_seconds");

        // Seconds resolution, from a clock that only counts unpaused time, so this needs real
        // wall clock to pass rather than a fixed number of ticks.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await World.Ticks(30);
            if (Scrape.Value(await Scrape.Metrics(Port), "pulse_server_uptime_seconds") > before)
            {
                return;
            }
        }

        Assert.Fail($"pulse_server_uptime_seconds never moved past {before}");
    }

    [AtlasScenario]
    public async Task Counters_ThatNothingHasTriggered_Are_SeededAtZero()
    {
        string body = await Scrape.Metrics(Port);

        // Nobody died and nothing suspended the server in this world, and all three still have to
        // be on the wire: a family that first appears the day something goes wrong is a family no
        // dashboard is plotting when it does.
        Assert.Contains("pulse_player_deaths_total 0", body);
        Assert.Contains("pulse_server_suspends_total ", body);
        Assert.Contains("pulse_server_suspend_seconds_total ", body);
    }

    [AtlasScenario]
    public async Task PlayerPing_Reports_BothAggregates()
    {
        string body = await Scrape.Metrics(Port);

        // A headless test player rides a dummy socket the engine never measures, so the value is
        // whatever the engine reports (NaN, skipped, hence zero). The two series existing is the
        // contract; the numbers are the server's to produce.
        Assert.True(Scrape.Value(body, "pulse_player_ping_seconds{stat=\"avg\"}") >= 0);
        Assert.True(Scrape.Value(body, "pulse_player_ping_seconds{stat=\"max\"}") >= 0);
    }

    [AtlasScenario]
    public async Task EntityBreakdown_Reports_TheBusiestCodes_OnTheSlowCadence()
    {
        // Sixteen characters at most, and unique in this class: the world is shared by every
        // scenario in it.
        ITestPlayer player = await World.JoinPlayer("pulse-entities");
        for (int i = 0; i < 12; i++)
        {
            World.SpawnEntity("game:chicken-rooster", player.Position);
        }

        // Twelve of one code guarantees a top-ten place whatever else the world generated, and the
        // breakdown rides the ChunksRefreshSeconds listener, seeded at one second here.
        string body = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await World.Ticks(30);
            body = await Scrape.Metrics(Port);
            if (body.Contains("pulse_entities_by_code{code=\"chicken-rooster\"} 12", StringComparison.Ordinal))
            {
                Assert.Contains("pulse_entities_by_code{code=\"other\"} ", body);
                return;
            }
        }

        Assert.Fail("pulse_entities_by_code never reported the twelve spawned roosters:\n" + body);
    }

    /// <summary>The suspend window is what players actually feel when the world autosaves, and
    /// /autosavenow drives exactly the engine path an unattended autosave takes.</summary>
    [AtlasScenario]
    public async Task SuspendCounters_Move_WhenTheServerAutosaves()
    {
        double before = Scrape.Value(await Scrape.Metrics(Port), "pulse_server_suspends_total");

        // The command declines while the chunk unloader has the world mid-flight, and says so
        // rather than failing, so this retries instead of asserting on the first answer.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            CommandResult result = await World.ExecuteCommand("/autosavenow");
            Assert.True(result.Ok, result.Message);

            string body = await Scrape.Metrics(Port);
            if (Scrape.Value(body, "pulse_server_suspends_total") > before)
            {
                Assert.True(
                    Scrape.Value(body, "pulse_server_suspend_seconds_total") >= 0,
                    "accumulated suspend time went backwards");
                return;
            }

            await World.Ticks(30);
        }

        Assert.Fail("pulse_server_suspends_total never moved across twenty autosaves");
    }
}
