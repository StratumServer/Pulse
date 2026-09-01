using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse;

public sealed class PulseModSystem : ModSystem
{
    public const string MeterName = "Pulse.Server";

    /// <summary>The runtime's own meter, published by the shared framework on .NET 8 and up.</summary>
    private const string RuntimeMeterName = "System.Runtime";

    private const string ConfigFile = "pulse.json";
    private const double SnapshotIntervalSeconds = 1.0;

    /// <summary>The engine rotates its statistics buckets every two seconds, so sampling them any
    /// faster only re-reads the same window.</summary>
    private const int EngineSampleIntervalMs = 2000;

    /// <summary>How many entity codes get a series of their own before the rest are lumped into
    /// one bucket. Ten covers the animals and the drifters on any world worth looking at.</summary>
    private const int EntityCodeLimit = 10;

    private const string DegradedWarning =
        "Pulse could not reach the engine's own statistics ({0}). Tick busy time, the per-window "
        + "packet and byte counts, the connection queue and the UDP byte totals will not be "
        + "served; every other metric is unaffected.";

    /// <summary>Tick period buckets, seconds. Placed around the 33.3 ms default budget so a
    /// healthy server fills the low buckets and every overrun is separable.</summary>
    private static readonly double[] TickBuckets = [0.025, 0.0334, 0.05, 0.075, 0.1, 0.25, 0.5, 1.0];

    private readonly Stopwatch tickClock = new();
    private readonly SuspendBookkeeper suspendWindow = new();
    private readonly EntityBreakdown entityBreakdown = new(EntityCodeLimit);

    // Written by the main thread, read by the ObservableGauge callbacks, which run on whatever
    // thread scrapes. World state in this engine is main-thread-only, so the callbacks read this
    // snapshot and nothing else.
    private volatile Snapshot snapshot = Snapshot.Empty;

    // Same handoff, own cadence: the loaded-chunk count is far too expensive to read every second.
    private volatile int chunksLoaded;

    // And again, at the cadence the engine's own statistics buckets rotate at. Null until the
    // engine probe resolves, and left at its last reading if the probe ever fails afterwards.
    private volatile EngineSample? engine;

    private ICoreServerAPI? sapi;
    private Meter? meter;
    private MetricsAggregator? aggregator;
    private MetricsHttpServer? http;
    private TickBookkeeper? tickBookkeeper;
    private EngineProbe? probe;
    private Counter<long>? columnsGenerated;
    private Counter<long>? logEntries;
    private Counter<long>? engineWarnings;
    private Counter<long>? playerDeaths;
    private Counter<long>? suspends;
    private Counter<double>? suspendSeconds;
    private Gauge<long>? entitiesByCode;
    private long listenerId = -1;
    private long chunksListenerId = -1;
    private long engineListenerId = -1;

    /// <summary>A monotonic clock for the suspend window. Not the server's own uptime: that one
    /// stops ticking for exactly the interval being measured.</summary>
    private static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        PulseConfig config = api.LoadModConfig<PulseConfig>(ConfigFile) ?? StoreDefaults(api);
        if (!config.Enabled)
        {
            api.Logger.Notification("Pulse is disabled in " + ConfigFile + ", nothing registered.");
            return;
        }

        meter = new Meter(MeterName);
        tickBookkeeper = new TickBookkeeper(
            meter.CreateCounter<long>(
                "pulse_server_ticks_total", "{tick}", "Server ticks processed since startup."),
            meter.CreateHistogram(
                "pulse_server_tick_seconds", "s", "Wall clock seconds between consecutive server ticks.",
                tags: null, new InstrumentAdvice<double> { HistogramBucketBoundaries = TickBuckets }),
            SnapshotIntervalSeconds);
        meter.CreateObservableGauge(
            "pulse_players_online", () => snapshot.Players, "{player}", "Players currently connected.");
        meter.CreateObservableGauge(
            "pulse_entities_loaded", () => snapshot.Entities, "{entity}", "Entities loaded in the world.");
        meter.CreateObservableGauge(
            "pulse_server_tick_budget_seconds", () => snapshot.TickBudgetSeconds, "s",
            "Configured server tick budget in seconds.");
        meter.CreateObservableGauge(
            "pulse_worldgen_queue_columns", () => snapshot.WorldgenQueue, "{column}",
            "Chunk columns waiting in the generation queue.");
        meter.CreateObservableGauge(
            "pulse_chunks_loaded", () => chunksLoaded, "{chunk}", "Chunks loaded in the world.");
        meter.CreateObservableGauge(
            "pulse_server_uptime_seconds", () => snapshot.UptimeSeconds, "s",
            "Seconds the server has been ticking, not counting time spent suspended.");
        meter.CreateObservableGauge(
            "pulse_player_ping_seconds", () => PingMeasurements(snapshot), "s",
            "Round trip time to the players online, averaged and at its worst.");
        meter.CreateObservableCounter(
            "pulse_network_sent_bytes_total", () => snapshot.SentBytes, "By",
            "Bytes sent since startup on the main TCP channel. Excludes UDP, which the public server API does not expose.");
        meter.CreateObservableCounter(
            "pulse_network_received_bytes_total", () => snapshot.ReceivedBytes, "By",
            "Bytes received since startup on the main TCP channel. Excludes UDP, which the public server API does not expose.");
        entitiesByCode = meter.CreateGauge<long>(
            "pulse_entities_by_code", "{entity}",
            "Loaded entities by entity code, the ten most numerous plus everything else.");
        playerDeaths = meter.CreateCounter<long>(
            "pulse_player_deaths_total", "{death}", "Player deaths since startup.");
        suspends = meter.CreateCounter<long>(
            "pulse_server_suspends_total", "{suspend}",
            "Times the server suspended ticking since startup, autosaves included.");
        suspendSeconds = meter.CreateCounter<double>(
            "pulse_server_suspend_seconds_total", "s",
            "Wall clock seconds spent with server ticking suspended since startup.");
        columnsGenerated = meter.CreateCounter<long>(
            "pulse_worldgen_columns_generated_total", "{column}",
            "Chunk columns generated since startup.");
        logEntries = meter.CreateCounter<long>(
            "pulse_log_entries_total", "{entry}", "Log entries written since startup, by severity.");
        engineWarnings = meter.CreateCounter<long>(
            "pulse_engine_warnings_total", "{warning}",
            "Engine health warnings logged since startup, by kind.");

        // Only if the engine's own accounting is reachable. In degraded mode these families are
        // never published at all, which is more honest than serving a zero that looks like a
        // healthy server with no traffic.
        StartEngineProbe(api, meter);

        // The runtime publishes System.Runtime itself, so listening to it is the whole of the
        // integration: no instrumentation, no dependency, dotted OpenTelemetry names that the
        // writer maps on the way out.
        string[] meters = config.RuntimeMetrics ? [MeterName, RuntimeMeterName] : [MeterName];
        aggregator = new MetricsAggregator(OnUnsupportedInstrument, meters);
        SeedCounters(logEntries, engineWarnings, suspendSeconds, columnsGenerated, playerDeaths, suspends);
        PublishSnapshot();

        // The errorHandler overload is not optional. Without it an exception from this listener
        // aborts the remainder of the whole server tick and logs Fatal, and Fatal entries count
        // toward the engine's DieAboveErrorCount self-shutdown. Metrics must not be able to stop
        // a server: log and swallow.
        listenerId = api.Event.RegisterGameTickListener(OnTick, OnTickError, 0);

        // AllLoadedChunks clones the whole loaded-chunk dictionary under the chunk lock on every
        // call, so it gets its own slow listener rather than riding the per-second snapshot. The
        // entity breakdown rides along with it: walking every loaded entity is nowhere near as
        // expensive, but it is not a per-second read either. The floor is there because a 0 in the
        // config would clone that dictionary every tick.
        chunksListenerId = api.Event.RegisterGameTickListener(
            OnSlowTick, OnTickError, Math.Max(1, config.ChunksRefreshSeconds) * 1000);

        // Worldgen events reach only the handlers registered for the save's own world type, so
        // hardcoding "standard" would silently count nothing on a superflat or custom world.
        api.Event.MapChunkGeneration(OnMapChunkGenerated, api.WorldManager.SaveGame?.WorldType ?? "standard");
        api.Logger.EntryAdded += OnLogEntry;
        api.Event.PlayerDeath += OnPlayerDeath;

        // Both fire on the main thread, and the suspend handler has to answer Ready straight away:
        // the engine polls it in a loop and treats anything else as a reason to keep waiting,
        // which would delay every autosave on the server by however long Pulse stalls.
        api.Event.ServerSuspend += OnServerSuspend;
        api.Event.ServerResume += OnServerResume;

        StartEndpoint(api, config);
    }

    public override void Dispose()
    {
        http?.Dispose();
        if (sapi != null)
        {
            sapi.Logger.EntryAdded -= OnLogEntry;
            sapi.Event.PlayerDeath -= OnPlayerDeath;
            sapi.Event.ServerSuspend -= OnServerSuspend;
            sapi.Event.ServerResume -= OnServerResume;
        }

        UnregisterListener(ref listenerId);
        UnregisterListener(ref chunksListenerId);
        UnregisterListener(ref engineListenerId);

        // MapChunkGeneration has no unregister counterpart. The handler stays on the engine's
        // worldgen list until shutdown wipes it, and does nothing once the meter below is gone.
        aggregator?.Dispose();
        meter?.Dispose();
    }

    private static PulseConfig StoreDefaults(ICoreServerAPI api)
    {
        PulseConfig config = new();
        api.StoreModConfig(config, ConfigFile);
        return config;
    }

    private void StartEndpoint(ICoreServerAPI api, PulseConfig config)
    {
        MetricsAggregator collector = aggregator!;
        MetricsHttpServer server = new(
            config.Bind, config.Port, () => PrometheusText.Render(collector.Collect()), api.Logger);
        try
        {
            server.Start();
            http = server;
            api.Logger.Notification("Pulse serving metrics on http://{0}:{1}/metrics", config.Bind, config.Port);
        }
        catch (Exception e)
        {
            server.Dispose();
            api.Logger.Error(
                "Pulse could not bind http://{0}:{1}/ ({2}). No metrics will be served; the game server is unaffected.",
                config.Bind, config.Port, e.Message);
        }
    }

    private void OnTick(float _)
    {
        // The mod's own stopwatch, not the float the engine passes: that one is derived from
        // Stopwatch.ElapsedMilliseconds and is quantised to whole milliseconds. Elapsed reads
        // zero before the clock is first started, which the bookkeeper treats as "no prior tick"
        // and ignores rather than records.
        double elapsedSeconds = tickClock.Elapsed.TotalSeconds;
        tickClock.Restart();

        if (tickBookkeeper!.OnTick(elapsedSeconds))
        {
            PublishSnapshot();
        }
    }

    private void OnTickError(Exception e) => sapi?.Logger.Error(e);

    /// <summary>A meter published an instrument shape the exporter has no rendering for. Say so
    /// once, at publish time, and serve everything else.</summary>
    private void OnUnsupportedInstrument(string name)
        => sapi?.Logger.Debug("Pulse skips metric {0}: unsupported instrument shape.", name);

    /// <summary>Resolves the engine probe and, if it worked, publishes the families that depend on
    /// it and starts sampling them.</summary>
    /// <remarks>The try/catch is the load-bearing part. A game version that renames or moves
    /// ServerMain makes the very first call into EngineProbe throw a TypeLoadException; catching it
    /// here is the difference between losing six families and failing to load the mod.</remarks>
    private void StartEngineProbe(ICoreServerAPI api, Meter engineMeter)
    {
        try
        {
            probe = EngineProbe.TryResolve(api);
            if (probe == null)
            {
                api.Logger.Warning(DegradedWarning, "the server world is not the type Pulse expects");
                return;
            }

            engine = probe.Read();
        }
        catch (Exception e)
        {
            probe = null;
            api.Logger.Warning(DegradedWarning, e.Message);
            return;
        }

        engineMeter.CreateObservableGauge(
            "pulse_server_tick_busy_seconds_avg", () => engine?.TickBusySeconds ?? 0, "s",
            "Average time one tick spent working over the engine's last completed two second window, sleep excluded.");
        engineMeter.CreateObservableGauge(
            "pulse_network_packets_in_window", PacketMeasurements,
            "{packet}", "Packets handled during the engine's last completed two second window.");
        engineMeter.CreateObservableGauge(
            "pulse_network_bytes_in_window", ByteMeasurements,
            "By", "Bytes handled during the engine's last completed two second window.");
        engineMeter.CreateObservableGauge(
            "pulse_connection_queue_clients", () => engine?.ConnectionQueue ?? 0, "{client}",
            "Clients waiting in the connection queue because the server is full.");
        engineMeter.CreateObservableCounter(
            "pulse_network_udp_sent_bytes_total", () => engine?.UdpSentBytes ?? 0, "By",
            "Bytes sent since startup over UDP, which the public server API does not report.");
        engineMeter.CreateObservableCounter(
            "pulse_network_udp_received_bytes_total", () => engine?.UdpReceivedBytes ?? 0, "By",
            "Bytes received since startup over UDP, which the public server API does not report.");

        engineListenerId = api.Event.RegisterGameTickListener(OnEngineTick, OnTickError, EngineSampleIntervalMs);
    }

    /// <summary>Samples the engine's statistics bucket, and gives up on it for good if that ever
    /// throws.</summary>
    /// <remarks>Nothing in the read allocates or blocks, so this is one warning and then silence
    /// rather than a warning every two seconds. The families it feeds keep their last reading; a
    /// probe that survived startup and then broke is a case nobody has seen, and freezing six
    /// gauges is a smaller sin than a log line every two seconds for the life of the server.</remarks>
    private void OnEngineTick(float _)
    {
        if (probe == null)
        {
            return;
        }

        try
        {
            engine = probe.Read();
        }
        catch (Exception e)
        {
            probe = null;
            UnregisterListener(ref engineListenerId);
            sapi!.Logger.Warning(DegradedWarning, e.Message);
        }
    }

    private void OnSlowTick(float _)
    {
        ICoreServerAPI api = sapi!;
        chunksLoaded = api.WorldManager.AllLoadedChunks.Count;

        // Enumerated directly rather than through .Values or .Count: enumeration of the engine's
        // entity table is lock free, while both of those take every lock stripe in it.
        IReadOnlyList<KeyValuePair<string, long>> byCode = entityBreakdown.Refresh(
            api.World.LoadedEntities.Select(entry => entry.Value.Code?.Path ?? "unknown"));

        foreach (KeyValuePair<string, long> entry in byCode)
        {
            entitiesByCode!.Record(entry.Value, new KeyValuePair<string, object?>("code", entry.Key));
        }
    }

    private void OnPlayerDeath(IServerPlayer byPlayer, DamageSource? damageSource) => playerDeaths!.Add(1);

    /// <summary>Opens the pause window and gets out of the engine's way.</summary>
    /// <remarks>Returning anything but Ready blocks the suspend, and the suspend is how the server
    /// autosaves. A metrics mod has no business having an opinion about that.</remarks>
    private EnumSuspendState OnServerSuspend()
    {
        suspendWindow.Open(NowSeconds);
        return EnumSuspendState.Ready;
    }

    private void OnServerResume()
    {
        if (suspendWindow.Close(NowSeconds) is not double seconds)
        {
            return;
        }

        suspends!.Add(1);
        suspendSeconds!.Add(seconds);
    }

    /// <summary>Both windowed network families read the same sample once, so their two channels
    /// always describe the same two seconds.</summary>
    private IEnumerable<Measurement<long>> PacketMeasurements()
    {
        EngineSample? sample = engine;
        return ChannelMeasurements(sample?.TcpPackets ?? 0, sample?.UdpPackets ?? 0);
    }

    private IEnumerable<Measurement<long>> ByteMeasurements()
    {
        EngineSample? sample = engine;
        return ChannelMeasurements(sample?.TcpBytes ?? 0, sample?.UdpBytes ?? 0);
    }

    private static IEnumerable<Measurement<long>> ChannelMeasurements(long tcp, long udp) =>
    [
        new Measurement<long>(tcp, new KeyValuePair<string, object?>("channel", "tcp")),
        new Measurement<long>(udp, new KeyValuePair<string, object?>("channel", "udp")),
    ];

    private static IEnumerable<Measurement<double>> PingMeasurements(Snapshot from) =>
    [
        new Measurement<double>(from.PingAverageSeconds, new KeyValuePair<string, object?>("stat", "avg")),
        new Measurement<double>(from.PingMaxSeconds, new KeyValuePair<string, object?>("stat", "max")),
    ];

    /// <summary>Counts one newly generated chunk column.</summary>
    /// <remarks>This fires on the worldgen thread, not the main thread. Counting is the only thing
    /// it may do: no world reads, no snapshot writes, nothing that is not thread-safe on its
    /// own.</remarks>
    private void OnMapChunkGenerated(IMapChunk mapChunk, int chunkX, int chunkZ)
        => columnsGenerated!.Add(1);

    /// <summary>Counts one log entry by severity, and by engine warning when it is one.</summary>
    /// <remarks>This fires on whatever thread wrote the entry, engine threads included. Classify,
    /// count, return: the handler must never log, because ILogger.Error catches a throwing handler
    /// by logging another error straight back through here, and must never read the world, because
    /// it is not on the main thread.</remarks>
    private void OnLogEntry(EnumLogType type, string message, params object[] args)
    {
        string? level = LogClassifier.Level(type);
        if (level != null)
        {
            logEntries!.Add(1, new KeyValuePair<string, object?>("level", level));
        }

        string? kind = LogClassifier.EngineWarning(type, message);
        if (kind != null)
        {
            engineWarnings!.Add(1, new KeyValuePair<string, object?>("kind", kind));
        }
    }

    /// <summary>Records a zero for every label value Pulse can emit, so the counters exist from
    /// boot instead of appearing the first time something goes wrong. A series starts at its first
    /// measurement, and a family that shows up mid-scrape is a family no dashboard plots.</summary>
    /// <remarks>Static and parameterised on the counters it seeds, rather than reading the
    /// instance fields directly, so the seeding logic is drivable from a test with a plain Meter
    /// and no server. The two labelled ones seed a series per label value; everything in
    /// <paramref name="untagged"/> is a single series.</remarks>
    private static void SeedCounters(
        Counter<long> logEntries,
        Counter<long> engineWarnings,
        Counter<double> suspendSeconds,
        params Counter<long>[] untagged)
    {
        suspendSeconds.Add(0);
        foreach (Counter<long> counter in untagged)
        {
            counter.Add(0);
        }

        foreach (string level in LogClassifier.Levels)
        {
            logEntries.Add(0, new KeyValuePair<string, object?>("level", level));
        }

        foreach (string kind in LogClassifier.Kinds)
        {
            engineWarnings.Add(0, new KeyValuePair<string, object?>("kind", kind));
        }
    }

    private void UnregisterListener(ref long id)
    {
        if (id >= 0)
        {
            sapi?.Event.UnregisterGameTickListener(id);
            id = -1;
        }
    }

    private void PublishSnapshot()
    {
        ICoreServerAPI api = sapi!;

        // AllOnlinePlayers over Server.Players: the former is backed by the engine's concurrent
        // client table, the latter by a plain dictionary written without a lock.
        IPlayer[] players = api.World.AllOnlinePlayers;
        PingSummary ping = PingSummary.Of(players.OfType<IServerPlayer>().Select(player => player.Ping));

        snapshot = new Snapshot(
            players.Length,
            api.World.LoadedEntities.Count,
            api.Server.Config.TickTime / 1000.0,
            api.WorldManager.CurrentGeneratingChunkCount,
            api.Server.TotalSentBytes,
            api.Server.TotalReceivedBytes,

            // Seconds, never ServerUptimeMilliseconds: the engine truncates that one through an
            // int, so it goes negative after 24.9 days of uptime.
            api.Server.ServerUptimeSeconds,
            ping.AverageSeconds,
            ping.MaxSeconds);
    }

    /// <summary>Immutable handoff from the main thread to the scrape thread.</summary>
    private sealed record Snapshot(
        int Players,
        int Entities,
        double TickBudgetSeconds,
        int WorldgenQueue,
        long SentBytes,
        long ReceivedBytes,
        int UptimeSeconds,
        double PingAverageSeconds,
        double PingMaxSeconds)
    {
        public static Snapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
