namespace Pulse.Otlp;

/// <summary>Contents of ModConfig/pulse-otlp.json. A plain mirror of the file; everything derived
/// from it lives in <see cref="OtlpOptions"/>.</summary>
public sealed class PulseOtlpConfig
{
    /// <summary>On by default. Nobody installs a second zip by accident, so the mod being present
    /// is the intent; this key is here to turn export off without uninstalling.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base endpoint of the collector, without a signal path. The OTLP defaults: 4318 for
    /// http/protobuf, 4317 for grpc.</summary>
    public string Endpoint { get; set; } = "http://localhost:4318";

    /// <summary>"http/protobuf" or "grpc", the two names the OTLP specification defines.</summary>
    public string Protocol { get; set; } = "http/protobuf";

    /// <summary>Headers sent with every export, which is how hosted backends authenticate. These
    /// are secrets: see the note in the README about who can read this file.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Seconds between two exports. Floored at 5, because the interval also decides how
    /// often every observable instrument is polled, and the loaded-chunk style reads behind some of
    /// them are not free.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Export the runtime's own System.Runtime meter alongside Pulse's. Independent of the
    /// base mod's RuntimeMetrics flag: that one decides what the scrape endpoint serves, this one
    /// decides what gets pushed, and a host may well want different answers.</summary>
    public bool IncludeRuntimeMetrics { get; set; } = true;

    /// <summary>The service.name resource attribute every export carries. This is how a backend
    /// that receives metrics from more than one server tells them apart: grouping, filtering and
    /// dashboard variables are usually keyed off it. A blank value falls back to the default
    /// rather than exporting an empty attribute; see <see cref="OtlpOptions.ResolveServiceName"/>.
    /// The OTEL_SERVICE_NAME environment variable, the ecosystem's standard override, takes
    /// precedence over this key when it is set: see <see cref="OtlpOptions.ConfigureServiceIdentity"/>.</summary>
    public string ServiceName { get; set; } = "vintagestory";

    /// <summary>The service.instance.id resource attribute every export carries, which a backend
    /// turns into the "instance" label: it tells two servers with the same <see cref="ServiceName"/>
    /// apart, and a series is only continuous across restarts if it stays the same. Blank, or
    /// missing from a file written by an older version, means "generate one": Pulse OTLP puts a
    /// fresh GUID in this key at startup, writing the file, and so finds the same one on every
    /// start after that. Any other value is used as written, trimmed. Each server needs its own: a
    /// file copied to a second server carries the first one's id along. The
    /// OTEL_RESOURCE_ATTRIBUTES environment variable's service.instance.id takes precedence over
    /// this key when it has one, and OTEL_SERVICE_NAME leaves this key unused, along with
    /// <see cref="ServiceName"/>: see <see cref="OtlpOptions.ConfigureServiceIdentity"/>.</summary>
    public string ServiceInstanceId { get; set; } = string.Empty;
}
