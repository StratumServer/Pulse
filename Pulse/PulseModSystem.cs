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

    private const string AttributionWarning =
        "Pulse could not read the engine's frame profiler ({0}). Per-mod tick attribution is off "
        + "for the rest of this run and its families stop updating; every other metric is "
        + "unaffected.";

    private const string ListenerWalkWarning =
        "Pulse could not read the engine's tick listener lists ({0}). Per-mod attribution carries "
        + "on from the mod loader's own type list, which maps fewer marks: the rest report as "
        + "unattributed.";

    /// <summary>How many ticks attribution waits for the primed profiler to complete one, before
    /// concluding that priming never took. Roughly half a minute at the default tick rate.</summary>
    private const int UnprimedTickLimit = 1000;

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
    private TickAttribution? attribution;
    private ModOwners? owners;
    private AttributionProbe? attributionProbe;
    private Gauge<double>? modTickShare;
    private Counter<double>? modTickSeconds;
    private Counter<long>? attributionTicks;
    private Counter<long>? attributionDropped;
    private Counter<long>? columnsGenerated;
    private Counter<long>? logEntries;
    private Counter<long>? engineWarnings;
    private Counter<long>? playerDeaths;
    private Counter<long>? suspends;
    private Counter<double>? suspendSeconds;
    private Gauge<long>? entitiesByCode;
    private int unprimedTicks;
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

        // Only if the operator asked for it: this one costs tick time while it runs.
        StartAttribution(api, meter, config.Attribution ?? new AttributionConfig());

        // The runtime publishes System.Runtime itself, so listening to it is the whole of the
        // integration: no instrumentation, no dependency, dotted OpenTelemetry names that the
        // writer maps on the way out.
        string[] meters = config.RuntimeMetrics ? [MeterName, RuntimeMeterName] : [MeterName];
        aggregator = new MetricsAggregator(OnUnsupportedInstrument, meters);
        SeedCounters(logEntries, engineWarnings, suspendSeconds, columnsGenerated, playerDeaths, suspends);
        SeedAttribution();
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

            // Whatever else is shutting down, the engine does not keep paying for a profiler that
            // Pulse turned on and no longer reads.
            if (attribution != null && sapi.World.FrameProfiler is { } profiler)
            {
                profiler.Enabled = false;
            }
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

        if (attribution != null)
        {
            OnAttributionTick(elapsedSeconds);
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
            "pulse_server_tick_busy_seconds", () => engine?.TickBusySeconds ?? 0, "s",
            "Average time one tick spent working over the engine's last completed two second window, sleep excluded.");
        engineMeter.CreateObservableGauge(
            "pulse_network_packets_per_second", PacketMeasurements,
            "{packet}/s", "Packet rate over the engine's last completed statistics window, nominally two seconds.");
        engineMeter.CreateObservableGauge(
            "pulse_network_bytes_per_second", ByteMeasurements,
            "By/s", "Byte rate over the engine's last completed statistics window, nominally two seconds.");
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

    /// <summary>Publishes the attribution families and arms the duty cycle, when the config asks
    /// for it.</summary>
    /// <remarks>Nothing here is registered when <c>Attribution.Enabled</c> is false, priming
    /// included, so a server that has not asked for attribution never touches the engine's frame
    /// profiler at all.</remarks>
    private void StartAttribution(ICoreServerAPI api, Meter attributionMeter, AttributionConfig config)
    {
        if (!config.Enabled)
        {
            return;
        }

        owners = new ModOwners(api.ClassRegistry.GetEntityBehaviorClass);
        foreach (Mod mod in api.ModLoader.Mods)
        {
            foreach (ModSystem system in mod.Systems)
            {
                owners.AddSystem(mod.Info.ModID, system.GetType());
            }
        }

        try
        {
            attributionProbe = AttributionProbe.TryResolve(api);
        }
        catch (Exception e)
        {
            attributionProbe = null;
            api.Logger.Warning(ListenerWalkWarning, e.Message);
        }

        attribution = new TickAttribution(config.BurstTicks, config.IntervalSeconds);
        modTickShare = attributionMeter.CreateGauge<double>(
            "pulse_mod_tick_share", "1",
            "Fraction of the profiled main-thread busy time attributed to one mod over the last completed burst.");
        modTickSeconds = attributionMeter.CreateCounter<double>(
            "pulse_mod_tick_seconds_total", "s",
            "Main-thread seconds attributed to one mod while attribution was profiling. Sampled: this is time inside the bursts, not since startup.");
        attributionTicks = attributionMeter.CreateCounter<long>(
            "pulse_attribution_ticks_total", "{tick}",
            "Ticks actually profiled, so the sampled seconds can be normalised against the ticks they came from.");
        attributionDropped = attributionMeter.CreateCounter<long>(
            "pulse_attribution_dropped_samples_total", "{sample}",
            "Profiler marks discarded because their elapsed time had overflowed the engine's 32 bit counter.");

        // Before the tick loop exists, and not one moment later. See PrimeFrameProfiler.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, PrimeFrameProfiler);
        api.Logger.Notification(
            "Pulse attributes the tick per mod: bursts of {0} ticks every {1}s.",
            attribution.BurstTicks, attribution.IntervalSeconds);
    }

    /// <summary>Turns the engine's frame profiler on once, before the server starts ticking.</summary>
    /// <remarks>This is not a nicety, it is the difference between a working feature and a server
    /// that dies the first time Pulse starts a burst. <c>FrameProfilerUtil.End</c> dereferences the
    /// root range that the matching <c>Begin</c> creates, and <c>ServerMain.Process</c> calls
    /// <c>End</c> outside the try/catch guarding the tick (1.22.7:1556-1562), from a loop with no
    /// guard of its own (<c>ServerProgram.cs:133-137</c>). On a server whose profiler has never
    /// run, flipping the flag part-way through a tick means <c>End</c> runs with no <c>Begin</c>
    /// before it and the NullReferenceException takes the process down. Enabling here, while
    /// <c>Launch</c> is still running, guarantees the first <c>Begin</c> establishes that root.
    /// Afterwards the duty cycle flips the flag from Pulse's own tick listener, where the profiler
    /// sits at depth zero and both directions are safe.</remarks>
    private void PrimeFrameProfiler()
    {
        // The profiler is thread-static and this runs on the thread that will do the ticking, so
        // it is there. Guarded anyway: nothing wraps a run phase handler, and throwing out of one
        // would take the server's startup with it.
        if (sapi?.World.FrameProfiler is { } profiler)
        {
            profiler.Enabled = true;
        }
    }

    /// <summary>Advances the attribution duty cycle by one tick, and gives up on it for good if
    /// that ever throws.</summary>
    /// <remarks>Same bargain as the engine probe, with one addition: the profiler flag is put back
    /// before giving up, because leaving it on would charge every later tick a few percent for data
    /// nobody is reading any more.</remarks>
    private void OnAttributionTick(double elapsedSeconds)
    {
        if (sapi!.World.FrameProfiler is not { } profiler)
        {
            return;
        }

        // The guard that makes the crash in PrimeFrameProfiler structurally impossible rather than
        // merely avoided. Only End() sets PrevRootEntry, and it sets it after dereferencing the
        // root range that Begin() creates, so a non-null value here is proof that the profiler has
        // completed a tick and that the same dereference will not throw next time. The flag is
        // never flipped on before that proof exists.
        if (profiler.PrevRootEntry == null)
        {
            // Priming runs once, before the tick loop, and the very next completed tick sets this.
            // Still null half a minute later means the flag never took, on a thread Pulse cannot
            // reach: stop rather than report zeros that look like a server nothing is running on.
            if (++unprimedTicks > UnprimedTickLimit)
            {
                attribution = null;
                sapi.Logger.Warning(AttributionWarning, "the engine's profiler never completed a primed tick");
            }

            return;
        }

        try
        {
            bool starting = !attribution!.Profiling;
            AttributionBurst? burst = attribution.OnTick(elapsedSeconds, profiler.PrevRootEntry, owners!.Owner);
            if (starting && attribution.Profiling)
            {
                RefreshOwners();
            }

            if (burst != null)
            {
                PublishBurst(burst);
            }

            profiler.Enabled = attribution.Profiling;
        }
        catch (Exception e)
        {
            attribution = null;
            profiler.Enabled = false;
            sapi.Logger.Warning(AttributionWarning, e.Message);
        }
    }

    /// <summary>Re-reads which mod owns which tick listener, once per burst.</summary>
    /// <remarks>Once per burst rather than once at startup because mods register and drop listeners
    /// as the world runs. Its own catch: losing the walk costs precision in the map, not the
    /// feature.</remarks>
    private void RefreshOwners()
    {
        try
        {
            attributionProbe?.Refresh(owners!);
        }
        catch (Exception e)
        {
            attributionProbe = null;
            sapi!.Logger.Warning(ListenerWalkWarning, e.Message);
        }
    }

    private void PublishBurst(AttributionBurst burst)
    {
        attributionTicks!.Add(burst.Ticks);
        attributionDropped!.Add(burst.Dropped);
        foreach (KeyValuePair<string, double> entry in burst.Seconds)
        {
            KeyValuePair<string, object?> modid = new("modid", entry.Key);
            modTickSeconds!.Add(entry.Value, modid);
            modTickShare!.Record(burst.BusySeconds > 0 ? entry.Value / burst.BusySeconds : 0, modid);
        }
    }

    /// <summary>Puts the attribution families on the wire from boot, at zero, rather than the first
    /// time a burst completes.</summary>
    /// <remarks>The two labelled families are seeded on the buckets that always exist. A mod's own
    /// series still appears the first time it is measured, which is unavoidable: nothing knows
    /// which mods eat tick time until one has been profiled.</remarks>
    private void SeedAttribution()
    {
        if (attribution == null)
        {
            return;
        }

        attributionTicks!.Add(0);
        attributionDropped!.Add(0);
        foreach (string modid in new[] { TickAttribution.Engine, TickAttribution.Unattributed })
        {
            KeyValuePair<string, object?> label = new("modid", modid);
            modTickSeconds!.Add(0, label);
            modTickShare!.Record(0, label);
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
    /// <summary>The engine rotates its statistics ring every two seconds, a constant wired into
    /// the tick loop, so a completed bucket nominally spans this long. A bucket cut short around a
    /// suspend makes the rate read low for one window; the engine's own /stats has the same
    /// approximation.</summary>
    private const double EngineWindowSeconds = 2.0;

    private IEnumerable<Measurement<double>> PacketMeasurements()
    {
        EngineSample? sample = engine;
        return ChannelMeasurements(
            (sample?.TcpPackets ?? 0) / EngineWindowSeconds, (sample?.UdpPackets ?? 0) / EngineWindowSeconds);
    }

    private IEnumerable<Measurement<double>> ByteMeasurements()
    {
        EngineSample? sample = engine;
        return ChannelMeasurements(
            (sample?.TcpBytes ?? 0) / EngineWindowSeconds, (sample?.UdpBytes ?? 0) / EngineWindowSeconds);
    }

    private static IEnumerable<Measurement<double>> ChannelMeasurements(double tcp, double udp) =>
    [
        new Measurement<double>(tcp, new KeyValuePair<string, object?>("channel", "tcp")),
        new Measurement<double>(udp, new KeyValuePair<string, object?>("channel", "udp")),
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
