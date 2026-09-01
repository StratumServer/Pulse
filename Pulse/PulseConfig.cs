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
    /// refresh.</summary>
    public int ChunksRefreshSeconds { get; set; } = 30;
}
