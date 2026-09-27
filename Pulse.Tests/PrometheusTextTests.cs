using System.Globalization;
using Xunit;

namespace Pulse.Tests;

public class PrometheusTextTests
{
    [Fact]
    public void Render_Writes_CounterFamily()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_server_ticks_total", MetricKind.Counter, "Server ticks.", 42),
        ]);

        Assert.Equal(
            "# HELP pulse_server_ticks_total Server ticks.\n" +
            "# TYPE pulse_server_ticks_total counter\n" +
            "pulse_server_ticks_total 42\n",
            text);
    }

    [Fact]
    public void Render_Writes_GaugeFamily()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_players_online", MetricKind.Gauge, "Players connected.", 3),
        ]);

        Assert.Equal(
            "# HELP pulse_players_online Players connected.\n" +
            "# TYPE pulse_players_online gauge\n" +
            "pulse_players_online 3\n",
            text);
    }

    [Fact]
    public void Render_Writes_HistogramFamily_WithCumulativeBucketsAndInf()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_server_tick_seconds", MetricKind.Histogram, "Tick period.", 0)
            {
                Bounds = [0.025, 0.05, 0.1],
                Buckets = [2, 0, 1],
                Sum = 0.44,
                Count = 5,
            },
        ]);

        Assert.Equal(
            "# HELP pulse_server_tick_seconds Tick period.\n" +
            "# TYPE pulse_server_tick_seconds histogram\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.025\"} 2\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.05\"} 2\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.1\"} 3\n" +
            "pulse_server_tick_seconds_bucket{le=\"+Inf\"} 5\n" +
            "pulse_server_tick_seconds_sum 0.44\n" +
            "pulse_server_tick_seconds_count 5\n",
            text);
    }

    [Fact]
    public void Render_Writes_EveryFamilyInOrder()
    {
        string text = PrometheusText.Render([
            new MetricSample("a_total", MetricKind.Counter, "A.", 1),
            new MetricSample("b_gauge", MetricKind.Gauge, "B.", 2),
        ]);

        Assert.Equal(
            "# HELP a_total A.\n# TYPE a_total counter\na_total 1\n" +
            "# HELP b_gauge B.\n# TYPE b_gauge gauge\nb_gauge 2\n",
            text);
    }

    [Fact]
    public void Render_Uses_InvariantCulture_UnderAFrenchLocale()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // The dev machine runs fr-FR, where the default double formatter writes "0,025".
            // A comma decimal separator makes the whole exposition unparseable.
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            string text = PrometheusText.Render([
                new MetricSample("pulse_server_tick_seconds", MetricKind.Histogram, "Tick period.", 0)
                {
                    Bounds = [0.025, 1.5],
                    Buckets = [1, 1],
                    Sum = 1.525,
                    Count = 2,
                },
                new MetricSample("pulse_server_tick_budget_seconds", MetricKind.Gauge, "Budget.", 0.0334),
            ]);

            Assert.DoesNotContain(",", text);
            Assert.Contains("le=\"0.025\"", text);
            Assert.Contains("le=\"1.5\"", text);
            Assert.Contains("pulse_server_tick_seconds_sum 1.525\n", text);
            Assert.Contains("pulse_server_tick_budget_seconds 0.0334\n", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Render_Writes_NonFiniteValues_TheWayPrometheusSpellsThem()
    {
        string text = PrometheusText.Render([
            new MetricSample("nan_gauge", MetricKind.Gauge, "N.", double.NaN),
            new MetricSample("pos_gauge", MetricKind.Gauge, "P.", double.PositiveInfinity),
            new MetricSample("neg_gauge", MetricKind.Gauge, "M.", double.NegativeInfinity),
        ]);

        Assert.Contains("nan_gauge NaN\n", text);
        Assert.Contains("pos_gauge +Inf\n", text);
        Assert.Contains("neg_gauge -Inf\n", text);
    }

    [Fact]
    public void Render_Writes_Nothing_ForNoSamples()
    {
        Assert.Equal(string.Empty, PrometheusText.Render([]));
    }

    [Fact]
    public void Render_Writes_Labels_InTheOrderGiven_WithTheThreeEscapes()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_engine_warnings_total", MetricKind.Counter, "Warnings.", 2)
            {
                Labels = [new("kind", "back\\slash"), new("note", "a \"quote\" and a\nnewline")],
            },
        ]);

        Assert.Equal(
            "# HELP pulse_engine_warnings_total Warnings.\n" +
            "# TYPE pulse_engine_warnings_total counter\n" +
            "pulse_engine_warnings_total{kind=\"back\\\\slash\",note=\"a \\\"quote\\\" and a\\nnewline\"} 2\n",
            text);
    }

    [Fact]
    public void Render_Writes_OneHelpAndTypeHeader_ForEveryTagSetOfAFamily()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_log_entries_total", MetricKind.Counter, "Entries.", 3)
            {
                Labels = [new("level", "warning")],
            },
            new MetricSample("pulse_log_entries_total", MetricKind.Counter, "Entries.", 1)
            {
                Labels = [new("level", "error")],
            },
        ]);

        // Prometheus rejects a scrape that repeats HELP or TYPE for a metric name, so the header
        // belongs to the family and the braces separate the series.
        Assert.Equal(
            "# HELP pulse_log_entries_total Entries.\n" +
            "# TYPE pulse_log_entries_total counter\n" +
            "pulse_log_entries_total{level=\"warning\"} 3\n" +
            "pulse_log_entries_total{level=\"error\"} 1\n",
            text);
    }

    /// <summary>Series of one family do not necessarily reach the writer together. A labelled
    /// gauge opens a series the first time a tag set is measured, so the entity breakdown starts
    /// one series at boot and another an hour later, with whatever else was published in between
    /// sitting between them.</summary>
    [Fact]
    public void Render_Gathers_TheSeriesOfAFamily_EvenWhenTheyArriveApart()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_entities_by_code", MetricKind.Gauge, "By code.", 4)
            {
                Labels = [new("code", "other")],
            },
            new MetricSample("pulse_players_online", MetricKind.Gauge, "Players.", 2),
            new MetricSample("pulse_entities_by_code", MetricKind.Gauge, "By code.", 9)
            {
                Labels = [new("code", "drifter")],
            },
        ]);

        Assert.Equal(
            "# HELP pulse_entities_by_code By code.\n" +
            "# TYPE pulse_entities_by_code gauge\n" +
            "pulse_entities_by_code{code=\"other\"} 4\n" +
            "pulse_entities_by_code{code=\"drifter\"} 9\n" +
            "# HELP pulse_players_online Players.\n" +
            "# TYPE pulse_players_online gauge\n" +
            "pulse_players_online 2\n",
            text);
    }

    [Theory]
    [InlineData("dotnet.gc.collections", MetricKind.Counter, "{collection}", "dotnet_gc_collections_total")]
    [InlineData("dotnet.gc.pause.time", MetricKind.Counter, "s", "dotnet_gc_pause_time_seconds_total")]
    [InlineData("dotnet.process.memory.working_set", MetricKind.Gauge, "By", "dotnet_process_memory_working_set_bytes")]
    [InlineData("dotnet.gc.heap.size", MetricKind.Histogram, "", "dotnet_gc_heap_size")]
    [InlineData("pulse_server_ticks_total", MetricKind.Counter, "{tick}", "pulse_server_ticks_total")]
    [InlineData("pulse_players_online", MetricKind.Gauge, "{player}", "pulse_players_online")]
    [InlineData("pulse_mod_tick_share", MetricKind.Gauge, "{share}", "pulse_mod_tick_share")]
    [InlineData("thing_gauge", MetricKind.Gauge, "1", "thing_gauge_ratio")]
    public void MetricName_Underscores_Dots_AppliesUnitWords_AndSuffixesMonotonicCountersOnly(
        string name, MetricKind kind, string unit, string expected)
    {
        Assert.Equal(expected, PrometheusText.MetricName(name, kind, unit));
    }

    /// <summary>Every instrument Pulse.Server and the runtime's System.Runtime meter publish, with
    /// the exact name prometheus/otlptranslator v1.0.0 derives from its instrument name, unit and
    /// kind (UnderscoreEscapingWithSuffixes, no namespace): the same translation Grafana applies to
    /// the OTLP export and opentelemetry-dotnet's own Prometheus exporter applies in process. This
    /// table is the proof that carrying the unit into the writer and applying the translator's
    /// rules generally, rather than special-casing the nine renamed runtime families, still leaves
    /// every one of the 28 Pulse.Server names exactly as it already was.</summary>
    [Theory]
    [MemberData(nameof(AllInstruments))]
    public void MetricName_MatchesTheOtlpTranslation_ForEveryInstrumentPulsePublishes(
        string instrument, MetricKind kind, string unit, string expected)
    {
        Assert.Equal(expected, PrometheusText.MetricName(instrument, kind, unit));
    }

    /// <summary>One row per instrument in harness/instruments.tsv from the naming investigation,
    /// with the three fixed units and the expected name each produces through
    /// github.com/prometheus/otlptranslator v1.0.0 (UnderscoreEscapingWithSuffixes, no namespace).</summary>
    public static IEnumerable<object[]> AllInstruments()
    {
        (string Instrument, MetricKind Kind, string Unit, string Expected)[] rows =
        [
            ("pulse_server_ticks_total", MetricKind.Counter, "{tick}", "pulse_server_ticks_total"),
            ("pulse_server_tick_seconds", MetricKind.Histogram, "s", "pulse_server_tick_seconds"),
            ("pulse_players_online", MetricKind.Gauge, "{player}", "pulse_players_online"),
            ("pulse_entities_loaded", MetricKind.Gauge, "{entity}", "pulse_entities_loaded"),
            ("pulse_server_tick_budget_seconds", MetricKind.Gauge, "s", "pulse_server_tick_budget_seconds"),
            ("pulse_worldgen_queue_columns", MetricKind.Gauge, "{column}", "pulse_worldgen_queue_columns"),
            ("pulse_chunks_loaded", MetricKind.Gauge, "{chunk}", "pulse_chunks_loaded"),
            ("pulse_server_uptime_seconds", MetricKind.Gauge, "s", "pulse_server_uptime_seconds"),
            ("pulse_player_ping_seconds", MetricKind.Gauge, "s", "pulse_player_ping_seconds"),
            ("pulse_network_sent_bytes_total", MetricKind.Counter, "By", "pulse_network_sent_bytes_total"),
            ("pulse_network_received_bytes_total", MetricKind.Counter, "By", "pulse_network_received_bytes_total"),
            ("pulse_entities_by_code", MetricKind.Gauge, "{entity}", "pulse_entities_by_code"),
            ("pulse_player_deaths_total", MetricKind.Counter, "{death}", "pulse_player_deaths_total"),
            ("pulse_server_suspends_total", MetricKind.Counter, "{suspend}", "pulse_server_suspends_total"),
            ("pulse_server_suspend_seconds_total", MetricKind.Counter, "s", "pulse_server_suspend_seconds_total"),
            ("pulse_worldgen_columns_generated_total", MetricKind.Counter, "{column}", "pulse_worldgen_columns_generated_total"),
            ("pulse_log_entries_total", MetricKind.Counter, "{entry}", "pulse_log_entries_total"),
            ("pulse_engine_warnings_total", MetricKind.Counter, "{warning}", "pulse_engine_warnings_total"),
            ("pulse_server_tick_busy_seconds", MetricKind.Gauge, "s", "pulse_server_tick_busy_seconds"),
            ("pulse_network_packets_per_second", MetricKind.Gauge, "{packet/s}", "pulse_network_packets_per_second"),
            ("pulse_network_bytes_per_second", MetricKind.Gauge, "{byte/s}", "pulse_network_bytes_per_second"),
            ("pulse_connection_queue_clients", MetricKind.Gauge, "{client}", "pulse_connection_queue_clients"),
            ("pulse_network_udp_sent_bytes_total", MetricKind.Counter, "By", "pulse_network_udp_sent_bytes_total"),
            ("pulse_network_udp_received_bytes_total", MetricKind.Counter, "By", "pulse_network_udp_received_bytes_total"),
            ("pulse_mod_tick_share", MetricKind.Gauge, "{share}", "pulse_mod_tick_share"),
            ("pulse_mod_tick_seconds_total", MetricKind.Counter, "s", "pulse_mod_tick_seconds_total"),
            ("pulse_attribution_ticks_total", MetricKind.Counter, "{tick}", "pulse_attribution_ticks_total"),
            ("pulse_attribution_dropped_samples_total", MetricKind.Counter, "{sample}", "pulse_attribution_dropped_samples_total"),
            ("dotnet.gc.collections", MetricKind.Counter, "{collection}", "dotnet_gc_collections_total"),
            ("dotnet.process.memory.working_set", MetricKind.Gauge, "By", "dotnet_process_memory_working_set_bytes"),
            ("dotnet.gc.heap.total_allocated", MetricKind.Counter, "By", "dotnet_gc_heap_allocated_bytes_total"),
            ("dotnet.gc.last_collection.memory.committed_size", MetricKind.Gauge, "By", "dotnet_gc_last_collection_memory_committed_size_bytes"),
            ("dotnet.gc.last_collection.heap.size", MetricKind.Gauge, "By", "dotnet_gc_last_collection_heap_size_bytes"),
            ("dotnet.gc.last_collection.heap.fragmentation.size", MetricKind.Gauge, "By", "dotnet_gc_last_collection_heap_fragmentation_size_bytes"),
            ("dotnet.gc.pause.time", MetricKind.Counter, "s", "dotnet_gc_pause_time_seconds_total"),
            ("dotnet.jit.compiled_il.size", MetricKind.Counter, "By", "dotnet_jit_compiled_il_size_bytes_total"),
            ("dotnet.jit.compiled_methods", MetricKind.Counter, "{method}", "dotnet_jit_compiled_methods_total"),
            ("dotnet.jit.compilation.time", MetricKind.Counter, "s", "dotnet_jit_compilation_time_seconds_total"),
            ("dotnet.monitor.lock_contentions", MetricKind.Counter, "{contention}", "dotnet_monitor_lock_contentions_total"),
            ("dotnet.thread_pool.thread.count", MetricKind.Counter, "{thread}", "dotnet_thread_pool_thread_count_total"),
            ("dotnet.thread_pool.work_item.count", MetricKind.Counter, "{work_item}", "dotnet_thread_pool_work_item_count_total"),
            ("dotnet.thread_pool.queue.length", MetricKind.Counter, "{work_item}", "dotnet_thread_pool_queue_length_total"),
            ("dotnet.timer.count", MetricKind.Gauge, "{timer}", "dotnet_timer_count"),
            ("dotnet.assembly.count", MetricKind.Gauge, "{assembly}", "dotnet_assembly_count"),
            ("dotnet.exceptions", MetricKind.Counter, "{exception}", "dotnet_exceptions_total"),
            ("dotnet.process.cpu.count", MetricKind.Gauge, "{cpu}", "dotnet_process_cpu_count"),
            ("dotnet.process.cpu.time", MetricKind.Counter, "s", "dotnet_process_cpu_time_seconds_total"),
        ];

        Assert.Equal(47, rows.Length);
        foreach ((string instrument, MetricKind kind, string unit, string expected) in rows)
        {
            yield return [instrument, kind, unit, expected];
        }
    }

    [Fact]
    public void Render_Maps_RuntimeNamesAndLabelKeys_ToPrometheusSpelling()
    {
        string text = PrometheusText.Render([
            new MetricSample("dotnet.gc.collections", MetricKind.Counter, "Collections.", 12)
            {
                Labels = [new("gc.heap.generation", "gen0")],
            },
        ]);

        Assert.Equal(
            "# HELP dotnet_gc_collections_total Collections.\n" +
            "# TYPE dotnet_gc_collections_total counter\n" +
            "dotnet_gc_collections_total{gc_heap_generation=\"gen0\"} 12\n",
            text);
    }

    [Fact]
    public void Render_Writes_ABoundlessHistogram_AsSumAndCountAlone()
    {
        string text = PrometheusText.Render([
            new MetricSample("h_seconds", MetricKind.Histogram, "H.", 0) { Sum = 1.5, Count = 3 },
        ]);

        Assert.Equal(
            "# HELP h_seconds H.\n# TYPE h_seconds histogram\nh_seconds_sum 1.5\nh_seconds_count 3\n",
            text);
    }

    [Fact]
    public void Render_Puts_TheBucketBound_AfterTheSeriesLabels()
    {
        string text = PrometheusText.Render([
            new MetricSample("h_seconds", MetricKind.Histogram, "H.", 0)
            {
                Labels = [new("pass", "terrain")],
                Bounds = [0.5],
                Buckets = [1],
                Sum = 0.4,
                Count = 1,
            },
        ]);

        Assert.Contains("h_seconds_bucket{pass=\"terrain\",le=\"0.5\"} 1\n", text);
        Assert.Contains("h_seconds_bucket{pass=\"terrain\",le=\"+Inf\"} 1\n", text);
        Assert.Contains("h_seconds_sum{pass=\"terrain\"} 0.4\n", text);
        Assert.Contains("h_seconds_count{pass=\"terrain\"} 1\n", text);
    }
}
