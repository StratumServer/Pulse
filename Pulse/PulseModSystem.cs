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

    /// <summary>Tick period buckets, seconds. Placed around the 33.3 ms default budget so a
    /// healthy server fills the low buckets and every overrun is separable.</summary>
    private static readonly double[] TickBuckets = [0.025, 0.0334, 0.05, 0.075, 0.1, 0.25, 0.5, 1.0];

    private readonly Stopwatch tickClock = new();

    // Written by the main thread, read by the ObservableGauge callbacks, which run on whatever
    // thread scrapes. World state in this engine is main-thread-only, so the callbacks read this
    // snapshot and nothing else.
    private volatile Snapshot snapshot = new(0, 0, 0, 0);

    // Same handoff, own cadence: the loaded-chunk count is far too expensive to read every second.
    private volatile int chunksLoaded;

    private ICoreServerAPI? sapi;
    private Meter? meter;
    private MetricsAggregator? aggregator;
    private MetricsHttpServer? http;
    private TickBookkeeper? tickBookkeeper;
    private Counter<long>? columnsGenerated;
    private Counter<long>? logEntries;
    private Counter<long>? engineWarnings;
    private long listenerId = -1;
    private long chunksListenerId = -1;

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
        columnsGenerated = meter.CreateCounter<long>(
            "pulse_worldgen_columns_generated_total", "{column}",
            "Chunk columns generated since startup.");
        logEntries = meter.CreateCounter<long>(
            "pulse_log_entries_total", "{entry}", "Log entries written since startup, by severity.");
        engineWarnings = meter.CreateCounter<long>(
            "pulse_engine_warnings_total", "{warning}",
            "Engine health warnings logged since startup, by kind.");

        // The runtime publishes System.Runtime itself, so listening to it is the whole of the
        // integration: no instrumentation, no dependency, dotted OpenTelemetry names that the
        // writer maps on the way out.
        string[] meters = config.RuntimeMetrics ? [MeterName, RuntimeMeterName] : [MeterName];
        aggregator = new MetricsAggregator(OnUnsupportedInstrument, meters);
        SeedCounters(columnsGenerated, logEntries, engineWarnings);
        PublishSnapshot();

        // The errorHandler overload is not optional. Without it an exception from this listener
        // aborts the remainder of the whole server tick and logs Fatal, and Fatal entries count
        // toward the engine's DieAboveErrorCount self-shutdown. Metrics must not be able to stop
        // a server: log and swallow.
        listenerId = api.Event.RegisterGameTickListener(OnTick, OnTickError, 0);

        // AllLoadedChunks clones the whole loaded-chunk dictionary under the chunk lock on every
        // call, so it gets its own slow listener rather than riding the per-second snapshot. The
        // floor is there because a 0 in the config would clone that dictionary every tick.
        chunksListenerId = api.Event.RegisterGameTickListener(
            OnChunksTick, OnTickError, Math.Max(1, config.ChunksRefreshSeconds) * 1000);

        // Worldgen events reach only the handlers registered for the save's own world type, so
        // hardcoding "standard" would silently count nothing on a superflat or custom world.
        api.Event.MapChunkGeneration(OnMapChunkGenerated, api.WorldManager.SaveGame?.WorldType ?? "standard");
        api.Logger.EntryAdded += OnLogEntry;

        StartEndpoint(api, config);
    }

    public override void Dispose()
    {
        http?.Dispose();
        if (sapi != null)
        {
            sapi.Logger.EntryAdded -= OnLogEntry;
        }

        UnregisterListener(ref listenerId);
        UnregisterListener(ref chunksListenerId);

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

    private void OnChunksTick(float _) => chunksLoaded = sapi!.WorldManager.AllLoadedChunks.Count;

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
    /// <remarks>Static and parameterised on the three counters it seeds, rather than reading the
    /// instance fields directly, so the seeding logic is drivable from a test with a plain Meter
    /// and no server.</remarks>
    private static void SeedCounters(Counter<long> columnsGenerated, Counter<long> logEntries, Counter<long> engineWarnings)
    {
        columnsGenerated.Add(0);
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
        snapshot = new Snapshot(
            api.World.AllOnlinePlayers.Length,
            api.World.LoadedEntities.Count,
            api.Server.Config.TickTime / 1000.0,
            api.WorldManager.CurrentGeneratingChunkCount);
    }

    /// <summary>Immutable handoff from the main thread to the scrape thread.</summary>
    private sealed record Snapshot(int Players, int Entities, double TickBudgetSeconds, int WorldgenQueue);
}
