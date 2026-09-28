using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse.Otlp;

/// <summary>Pushes the metrics the base Pulse mod instruments to an OTLP collector.</summary>
/// <remarks>There is no assembly reference to Pulse here, and there must not be one: the mod loader
/// gives every mod's dll the same load context, so a compile-time reference would pin a version and
/// buy nothing. The two mods meet at a meter name, which is all System.Diagnostics.Metrics needs.
/// modinfo.json declares the dependency, so load order and presence are the loader's problem.
/// </remarks>
public sealed class PulseOtlpModSystem : ModSystem
{
    /// <summary>The meter the base mod publishes. A string on purpose: see the class remarks.</summary>
    private const string PulseMeterName = "Pulse.Server";

    /// <summary>The runtime's own meter, published by the shared framework on .NET 8 and up.</summary>
    private const string RuntimeMeterName = "System.Runtime";

    private const string ConfigFile = "pulse-otlp.json";

    private MeterProvider? provider;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Runs before ModSystem's own default of 0.1, which PulseModSystem does not override.
    /// ModLoader sorts every enabled mod's systems by this value after resolving dependency order,
    /// so without an override here the two mods keep whatever order dependency resolution happened
    /// to produce. This mod's MeterProvider has to be built, and therefore already listening for
    /// the "Pulse.Server" meter by name, before PulseModSystem creates that meter and seeds its
    /// counters: a measurement only reaches the listeners attached at the moment it is recorded.
    /// MeterListener.Start() does see an instrument that already existed, since it walks every
    /// published instrument when it starts, so starting later costs nothing on the instrument
    /// itself; what it costs is whatever was already recorded on it, which for a fresh instrument
    /// is exactly its seed. See PulseModSystem.SeedCounters for the call this protects.</summary>
    public override double ExecuteOrder() => 0.05;

    public override void StartServerSide(ICoreServerAPI api)
    {
        ConfigLoadResult<PulseOtlpConfig> loaded = ConfigLoad.Resolve(
            () => api.LoadModConfig<PulseOtlpConfig>(ConfigFile), () => new PulseOtlpConfig());
        PulseOtlpConfig config = loaded.Config;
        switch (loaded.Status)
        {
            case ConfigLoadStatus.Absent:
                api.StoreModConfig(config, ConfigFile);
                break;
            case ConfigLoadStatus.Loaded:
                ConfigUpgrade.Upgrade(api, config, ConfigFile, "Pulse OTLP");
                break;
            case ConfigLoadStatus.Unreadable:
                string unreadablePath = Path.Combine(api.GetOrCreateDataPath("ModConfig"), ConfigFile);

                // Redacted, not the raw parser message: a Headers value of the wrong shape (most
                // often OTEL_EXPORTER_OTLP_HEADERS's "k=v,k2=v2" string typed in where the config's
                // own JSON object belongs) makes Newtonsoft quote the offending value verbatim, and
                // that value can be a real bearer token or API key. The path, line and position
                // that make the error findable are not quoted and survive the redaction.
                string safeMessage = OtlpOptions.RedactQuotedValues(loaded.FailureMessage ?? string.Empty);
                api.Logger.Error(
                    ConfigLoad.UnreadableMessage, "Pulse OTLP", unreadablePath, safeMessage,
                    "Pulse OTLP is not exporting");
                return;
        }

        if (!config.Enabled)
        {
            api.Logger.Notification("Pulse OTLP is disabled in " + ConfigFile + ", nothing registered.");
            return;
        }

        if (!OtlpOptions.TryParseProtocol(config.Protocol, out OtlpExportProtocol protocol))
        {
            api.Logger.Warning(
                "Pulse OTLP does not know the protocol '{0}'. Exporting over http/protobuf instead; "
                + "the two names the OTLP specification defines are \"http/protobuf\" and \"grpc\".",
                config.Protocol);
        }

        // A malformed endpoint is the one failure the exporter cannot absorb for us, because it
        // throws while the provider is being built rather than on the export thread.
        if (!OtlpOptions.TryResolveEndpoint(config.Endpoint, protocol, out Uri? endpoint))
        {
            api.Logger.Error(
                "Pulse OTLP cannot read '{0}' as an http or https endpoint. Nothing will be exported; "
                + "the game server is unaffected.",
                config.Endpoint);
            return;
        }

        int intervalMs = OtlpOptions.IntervalMilliseconds(config.IntervalSeconds);
        string[] meters = config.IncludeRuntimeMetrics
            ? [PulseMeterName, RuntimeMeterName]
            : [PulseMeterName];

        // OTEL_SERVICE_NAME, the ecosystem's standard override, must win over the config key when
        // it is set. That is not automatic: ResourceBuilder.CreateDefault() (the seed
        // ConfigureResource lazily creates) already ends with the detector that reads this
        // variable, but ConfigureResource's own AddService call is appended after it, and
        // ResourceBuilder.Build() merges every detector's Resource left to right with the later
        // one winning on a collision (Resource.Merge: "In case of a collision the other Resource
        // takes precedence"). An unconditional AddService would therefore always beat the
        // environment variable. Checked against MeterProviderBuilderSdk.ConfigureResource,
        // ResourceBuilder.CreateDefault/Build and Resource.Merge in OpenTelemetry .NET 1.19.1
        // (github.com/open-telemetry/opentelemetry-dotnet, tag core-1.19.1). Skipping the call
        // when the variable is set leaves the SDK's own default resource pipeline, which already
        // reads it, untouched.
        string? serviceNameFromEnvironment = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        bool serviceNameSetByEnvironment = !string.IsNullOrWhiteSpace(serviceNameFromEnvironment);
        string serviceName = serviceNameSetByEnvironment
            ? serviceNameFromEnvironment!
            : OtlpOptions.ResolveServiceName(config.ServiceName);

        // Nothing past this point can take the server down. Every export runs on the SDK's own
        // background thread ("OpenTelemetry-PeriodicExportingMetricReader-..."), and
        // MetricReader.Collect wraps the collect-and-send in a catch that only writes to the SDK's
        // EventSource. A refused connection, a 401 from a SaaS backend or a DNS failure is
        // therefore invisible here by construction; adding our own guard around it would catch
        // nothing. Checked against OpenTelemetry 1.19.1.
        provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meters)
            .ConfigureResource(r =>
            {
                if (!serviceNameSetByEnvironment)
                {
                    r.AddService(serviceName);
                }
            })
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = endpoint;
                exporter.Protocol = protocol;
                exporter.Headers = OtlpOptions.RenderHeaders(config.Headers);
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = intervalMs;
            })
            .Build();

        api.Logger.Notification(
            "Pulse OTLP exporting {0} to {1} over {2} every {3}s as service '{4}'",
            string.Join(", ", meters), endpoint,
            protocol == OtlpExportProtocol.Grpc ? "grpc" : "http/protobuf", intervalMs / 1000, serviceName);
    }

    public override void Dispose()
    {
        // Disposing the provider shuts the reader down, which force-flushes one last export before
        // the process goes away. Blocking here is the point: the alternative is losing the window
        // that holds whatever went wrong just before shutdown.
        provider?.Dispose();
        provider = null;
    }
}
