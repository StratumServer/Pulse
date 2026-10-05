namespace Pulse;

/// <summary>Contents of ModConfig/pulse.json.</summary>
public sealed class PulseConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Address the metrics endpoint binds. Loopback by default: this is a public game
    /// server, and the endpoint is for the host, not the internet.</summary>
    public string Bind { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9464;

    /// <summary>Serve the runtime's own built-in meter alongside Pulse's: GC counts and pause
    /// time, heap sizes, working set, CPU time, thread pool and exceptions, as dotnet_* families.
    /// They cost nothing to produce; turn them off if you already collect them elsewhere.</summary>
    public bool RuntimeMetrics { get; set; } = true;

    /// <summary>Seconds between two reads of the loaded-chunk count. Deliberately slow, and slower
    /// than any sane scrape interval: the engine exposes no cheap count, so the read clones the
    /// whole loaded-chunk dictionary under the chunk lock. The gauge reads 0 until the first
    /// refresh. Clamped to at most a day.</summary>
    public int ChunksRefreshSeconds { get; set; } = 30;

    /// <summary>Per-mod tick attribution. Off by default, and duty-cycled when on.</summary>
    public AttributionConfig Attribution { get; set; } = new();

    /// <summary>Per-behavior, per-AI-task and per-entity-type tick timings read from a Stratum
    /// server. Off by default, duty-cycled when on, and nothing at all on a server that is not
    /// Stratum.</summary>
    public StratumTimingsConfig StratumTimings { get; set; } = new();
}

/// <summary>The <c>Attribution</c> block of ModConfig/pulse.json.</summary>
/// <remarks>Off by default on purpose. Attribution runs the engine's own frame profiler, which
/// stamps a mark after every listener and every main-thread entity behavior, and that costs
/// close to a quarter of the tick budget for as long as it runs, measured on a 4000-entity
/// server; the cost scales with loaded entities, not with this number. The duty cycle is what
/// makes it affordable: a short burst, then nothing until the next interval.</remarks>
public sealed class AttributionConfig
{
    public bool Enabled { get; set; }

    /// <summary>Consecutive ticks profiled per burst. Tick composition is stable over seconds, so
    /// a short burst describes the interval around it perfectly well.</summary>
    public int BurstTicks { get; set; } = 10;

    /// <summary>Seconds between the end of one burst and the start of the next.</summary>
    public int IntervalSeconds { get; set; } = 10;
}

/// <summary>The <c>StratumTimings</c> block of ModConfig/pulse.json.</summary>
/// <remarks>Off by default on purpose, and on the same duty cycle as attribution. While Stratum
/// records, every behavior, AI task and entity tick pays for two clock reads and a table update:
/// with the admin's whole timings switch on, which is an upper bound for the recording alone, a
/// server with about 4000 loose chickens spent a median 9 percent of its tick budget more and
/// allocated about 2.7 MB more per tick. The duty cycle is what makes it affordable: a short burst,
/// then nothing until the next interval.</remarks>
public sealed class StratumTimingsConfig
{
    public bool Enabled { get; set; }

    /// <summary>Consecutive ticks timed per burst.</summary>
    public int BurstTicks { get; set; } = 10;

    /// <summary>Seconds between the end of one burst and the start of the next.</summary>
    public int IntervalSeconds { get; set; } = 10;
}
