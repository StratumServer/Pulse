namespace Pulse;

/// <summary>Contents of ModConfig/pulse.json.</summary>
public sealed class PulseConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Address the metrics endpoint binds. Loopback by default: this is a public game
    /// server, and the endpoint is for the host, not the internet.</summary>
    public string Bind { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9464;
}
