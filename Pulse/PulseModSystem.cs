using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse;

public sealed class PulseModSystem : ModSystem
{
    public const string MeterName = "Pulse.Server";

    private const string ConfigFile = "pulse.json";
    private const double SnapshotIntervalSeconds = 1.0;

    /// <summary>Tick period buckets, seconds. Placed around the 33.3 ms default budget so a
    /// healthy server fills the low buckets and every overrun is separable.</summary>
    private static readonly double[] TickBuckets = [0.025, 0.0334, 0.05, 0.075, 0.1, 0.25, 0.5, 1.0];

    private readonly Stopwatch tickClock = new();

    // Written by the main thread, read by the ObservableGauge callbacks, which run on whatever
    // thread scrapes. World state in this engine is main-thread-only, so the callbacks read this
    // snapshot and nothing else.
    private volatile Snapshot snapshot = new(0, 0, 0);

    private ICoreServerAPI? sapi;
    private Meter? meter;
    private MetricsAggregator? aggregator;
    private MetricsHttpServer? http;
    private Counter<long>? ticks;
    private Histogram<double>? tickSeconds;
    private long listenerId = -1;
    private double sinceSnapshotSeconds;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

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
        ticks = meter.CreateCounter<long>(
            "pulse_server_ticks_total", "{tick}", "Server ticks processed since startup.");
        tickSeconds = meter.CreateHistogram(
            "pulse_server_tick_seconds", "s", "Wall clock seconds between consecutive server ticks.",
            tags: null, new InstrumentAdvice<double> { HistogramBucketBoundaries = TickBuckets });
        meter.CreateObservableGauge(
            "pulse_players_online", () => snapshot.Players, "{player}", "Players currently connected.");
        meter.CreateObservableGauge(
            "pulse_entities_loaded", () => snapshot.Entities, "{entity}", "Entities loaded in the world.");
        meter.CreateObservableGauge(
            "pulse_server_tick_budget_seconds", () => snapshot.TickBudgetSeconds, "s",
            "Configured server tick budget in seconds.");

        aggregator = new MetricsAggregator(MeterName);
        PublishSnapshot();

        // The errorHandler overload is not optional. Without it an exception from this listener
        // aborts the remainder of the whole server tick and logs Fatal, and Fatal entries count
        // toward the engine's DieAboveErrorCount self-shutdown. Metrics must not be able to stop
        // a server: log and swallow.
        listenerId = api.Event.RegisterGameTickListener(OnTick, OnTickError, 0);

        StartEndpoint(api, config);
    }

    public override void Dispose()
    {
        http?.Dispose();
        if (listenerId >= 0)
        {
            sapi?.Event.UnregisterGameTickListener(listenerId);
            listenerId = -1;
        }

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
        ticks!.Add(1);

        // The mod's own stopwatch, not the float the engine passes: that one is derived from
        // Stopwatch.ElapsedMilliseconds and is quantised to whole milliseconds.
        if (tickClock.IsRunning)
        {
            double seconds = tickClock.Elapsed.TotalSeconds;
            tickSeconds!.Record(seconds);
            sinceSnapshotSeconds += seconds;
        }

        tickClock.Restart();

        if (sinceSnapshotSeconds < SnapshotIntervalSeconds)
        {
            return;
        }

        sinceSnapshotSeconds = 0;
        PublishSnapshot();
    }

    private void OnTickError(Exception e) => sapi?.Logger.Error(e);

    private void PublishSnapshot()
    {
        ICoreServerAPI api = sapi!;

        // AllOnlinePlayers over Server.Players: the former is backed by the engine's concurrent
        // client table, the latter by a plain dictionary written without a lock.
        snapshot = new Snapshot(
            api.World.AllOnlinePlayers.Length,
            api.World.LoadedEntities.Count,
            api.Server.Config.TickTime / 1000.0);
    }

    /// <summary>Immutable handoff from the main thread to the scrape thread.</summary>
    private sealed record Snapshot(int Players, int Entities, double TickBudgetSeconds);
}
