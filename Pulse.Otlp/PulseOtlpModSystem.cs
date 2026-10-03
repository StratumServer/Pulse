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

    /// <summary>How often the queue <see cref="ExportFailureLog"/> fills on the export thread is
    /// drained onto the main thread. Independent of the configured export interval: a short
    /// IntervalSeconds should not also mean the log is checked any more often than this.</summary>
    private const int ExportFailureDrainIntervalMs = 5000;

    private ICoreServerAPI? sapi;
    private MeterProvider? provider;
    private ExportFailureLog? exportFailureLog;
    private long exportFailureLogListenerId = -1;

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
        sapi = api;

        ConfigLoadResult<PulseOtlpConfig> loaded = ConfigLoad.Resolve(
            () => api.LoadModConfig<PulseOtlpConfig>(ConfigFile), () => new PulseOtlpConfig());
        PulseOtlpConfig config = loaded.Config;

        // Before the file is stored or brought up to date below, so that what gets written is the
        // id this session exports: a blank ServiceInstanceId (never set, or missing from a file an
        // older version wrote) is replaced by a fresh GUID here, the store or the upgrade below puts
        // it in the file, and the next start finds it there instead of generating another one.
        // A random id per start is what made every restart a new series in the backend. Only a
        // blank key is touched, so no unrelated rewrite of the file changes an id the admin wrote.
        bool instanceIdGenerated = OtlpOptions.FillBlankServiceInstanceId(config);

        switch (loaded.Status)
        {
            case ConfigLoadStatus.Absent:
                if (!TryStoreDefaults(() => api.StoreModConfig(config, ConfigFile), out string? storeFailure))
                {
                    // A ModConfig directory the mod cannot write to (mounted read-only, say) must
                    // not stop it from starting: it runs on the in-memory defaults for this
                    // session, exporting off, rather than never starting at all.
                    Mod.Logger.Error(
                        "Pulse OTLP could not write {0} ({1}). Running with defaults for this "
                        + "session; exporting is off until the file can be written.",
                        ConfigFile, storeFailure);
                    return;
                }

                break;
            case ConfigLoadStatus.Loaded:
                ConfigUpgrade.Upgrade(api, Mod.Logger, config, ConfigFile, "Pulse OTLP");
                break;
            case ConfigLoadStatus.Unreadable:
                string unreadablePath = Path.Combine(api.GetOrCreateDataPath("ModConfig"), ConfigFile);

                // Redacted, not the raw parser message: a Headers value of the wrong shape (most
                // often OTEL_EXPORTER_OTLP_HEADERS's "k=v,k2=v2" string typed in where the config's
                // own JSON object belongs) makes Newtonsoft quote the offending value verbatim, and
                // that value can be a real bearer token or API key. The path, line and position
                // that make the error findable are not quoted and survive the redaction.
                string safeMessage = OtlpOptions.RedactQuotedValues(loaded.FailureMessage ?? string.Empty);
                Mod.Logger.Error(
                    ConfigLoad.UnreadableMessage, "Pulse OTLP", unreadablePath, safeMessage,
                    "Pulse OTLP is not exporting");
                return;
        }

        if (!config.Enabled)
        {
            Mod.Logger.Notification("Pulse OTLP is disabled in " + ConfigFile + ", nothing registered.");
            return;
        }

        if (!OtlpOptions.TryParseProtocol(config.Protocol, out OtlpExportProtocol protocol))
        {
            Mod.Logger.Warning(
                "Pulse OTLP does not know the protocol '{0}'. Exporting over http/protobuf instead; "
                + "the two names the OTLP specification defines are \"http/protobuf\" and \"grpc\".",
                config.Protocol);
        }

        if (!OtlpOptions.TryValidateHeaders(config.Headers, out string? offendingHeader))
        {
            // Never the value: a name colliding after trimming, or carrying a comma, says nothing
            // about what the value itself holds.
            Mod.Logger.Error(
                "Pulse OTLP's header '{0}' in {1} cannot be exported: its name collides with "
                + "another header once trimmed, or its value contains a comma, which the "
                + "exporter's own header format cannot carry. Nothing will be exported; the game "
                + "server is unaffected.",
                offendingHeader, ConfigFile);
            return;
        }

        // Everything from here down runs inside the OpenTelemetry SDK's own option validation,
        // which throws instead of returning false for a shape none of the checks above catch: an
        // endpoint with an empty user name and a password (http://:token@collector:4318, say)
        // throws UriFormatException, for http/protobuf inside TryResolveEndpoint itself and for
        // grpc inside the SDK's own Build() below; not a regression, 0.1.0 crashed on it the same
        // way, but the guard has to cover both call sites since TryResolveEndpoint can throw
        // before it ever returns false. One try/catch around the whole span, rather than one
        // around Build() alone, is what keeps a shape like that from ever reaching ModLoader:
        // ModLoader.TryRunModPhase rethrows a FormatException instead of absorbing it like every
        // other exception, and UriFormatException is one, so left uncaught here it takes the
        // server down at boot instead of merely failing this one mod.
        try
        {
            if (!OtlpOptions.TryResolveEndpoint(config.Endpoint, protocol, out Uri? endpoint))
            {
                // Never the configured value itself: a backend authenticating through userinfo or
                // a query string in the URL put both right there, and an unparsable endpoint is
                // exactly the case where that value most needs to stay out of the log.
                Mod.Logger.Error(
                    "Pulse OTLP's '{0}' in {1} is not an absolute http or https URL. Nothing will "
                    + "be exported; the game server is unaffected.",
                    nameof(PulseOtlpConfig.Endpoint), ConfigFile);
                return;
            }

            int intervalMs = OtlpOptions.IntervalMilliseconds(config.IntervalSeconds);
            string[] meters = config.IncludeRuntimeMetrics
                ? [PulseMeterName, RuntimeMeterName]
                : [PulseMeterName];

            // Nothing past the Build() below can take the server down. Every export runs on the
            // SDK's own background thread ("OpenTelemetry-PeriodicExportingMetricReader-..."), and
            // MetricReader.Collect wraps the collect-and-send in a catch that only writes to the
            // SDK's own EventSource. A refused connection, a 401 from a SaaS backend or a DNS
            // failure is therefore invisible to a try/catch placed here, by construction: the
            // EventListener below, not a guard around the export call, is what makes it visible
            // instead. Checked against OpenTelemetry 1.19.1.
            //
            // Constructed before the provider that creates the exporter, so the listener is
            // already attached to the exporter's EventSource by the time the first export can
            // happen.
            exportFailureLog = new ExportFailureLog(
                [.. OtlpOptions.SecretValues(config.Headers), .. OtlpOptions.EndpointSecrets(endpoint)],
                endpoint);

            provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(meters)
                .ConfigureResource(r => OtlpOptions.ConfigureServiceIdentity(
                    r, config.ServiceName, config.ServiceInstanceId))
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = endpoint;
                    exporter.Protocol = protocol;
                    exporter.Headers = OtlpOptions.RenderHeaders(config.Headers);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = intervalMs;
                })
                .Build();

            // The errorHandler overload is not optional, for the same reason as the base mod's
            // own tick listeners: an unhandled exception here would log Fatal and count toward
            // the engine's DieAboveErrorCount self-shutdown.
            exportFailureLogListenerId = api.Event.RegisterGameTickListener(
                OnDrainExportFailures, OnDrainExportFailuresError, ExportFailureDrainIntervalMs);

            // The identity is read back from the provider rather than worked out a second time
            // here: the environment can decide either value (see
            // OtlpOptions.ConfigureServiceIdentity), and the line is only any use to an admin if it
            // names what the backend will actually see. No instance clause when the resource has
            // none, which is what an OTEL_SERVICE_NAME set without an id in
            // OTEL_RESOURCE_ATTRIBUTES gives.
            Resource exported = provider.GetResource();
            string? instanceId = OtlpOptions.ResourceAttribute(exported, OtlpOptions.ServiceInstanceIdKey);

            // Scheme, host, port and path only, the same components the exporter's own
            // diagnostics ever carry: userinfo or a query string in the configured endpoint (a
            // backend that authenticates through a signed URL, say) has no business in a log line
            // at any level.
            Mod.Logger.Notification(
                "Pulse OTLP exporting {0} to {1} over {2} every {3}s as service '{4}'{5}",
                string.Join(", ", meters), OtlpOptions.LoggableEndpoint(endpoint),
                protocol == OtlpExportProtocol.Grpc ? "grpc" : "http/protobuf", intervalMs / 1000,
                OtlpOptions.ResourceAttribute(exported, OtlpOptions.ServiceNameKey),
                instanceId is null ? string.Empty : $", instance '{instanceId}'");

            // A generated id is only worth anything if the next start finds it in the file, and
            // neither the store nor the upgrade above can promise that. A ModConfig folder mounted
            // read-only is a warning of the upgrade's own, which does not say what it costs. A file
            // Newtonsoft reads and the upgrade's comparison cannot (single quotes, unquoted keys)
            // is no warning at all, since that comparison then finds nothing missing. Reading the
            // file back, through the loader the next start will use, is the one check that covers
            // every way of not getting the id written. Skipped when the exported id is not the
            // generated one: the environment's decided it, or the key was never used.
            if (instanceIdGenerated
                && instanceId == config.ServiceInstanceId
                && !FileHoldsInstanceId(() => api.LoadModConfig<PulseOtlpConfig>(ConfigFile), instanceId))
            {
                Mod.Logger.Warning(UnsavedInstanceIdMessage, ConfigFile, instanceId);
            }
        }
        catch (Exception ex)
        {
            // Never ex.Message: an exporter option's own validation message is not something
            // this mod controls, and nothing guarantees it never echoes the value that failed it.
            // The exception's type is diagnostic enough to tell a bad endpoint apart from a bad
            // interval without repeating either.
            Mod.Logger.Error(
                "Pulse OTLP could not start exporting ({0}); its configuration in {1} is not "
                + "something the OpenTelemetry SDK accepts. Nothing will be exported; the game "
                + "server is unaffected.",
                ex.GetType().Name, ConfigFile);

            // Nothing built so far may outlive this attempt: a provider that did finish building
            // would keep its reader's background timer running, a registered tick listener would
            // keep draining a log nothing will ever queue into again, and the event listener,
            // left registered, would keep reading the process-wide exporter EventSource for the
            // rest of the server's life, all three for an export that will never happen.
            provider?.Dispose();
            provider = null;

            if (exportFailureLogListenerId >= 0)
            {
                api.Event.UnregisterGameTickListener(exportFailureLogListenerId);
                exportFailureLogListenerId = -1;
            }

            exportFailureLog?.Dispose();
            exportFailureLog = null;
        }
    }

    public override void Dispose()
    {
        // Order matters. Disposing the provider shuts the reader down, which force-flushes one
        // last export before the process goes away; draining right after that flush, rather than
        // before it, is what catches a failure on that very last attempt, for a failure quick
        // enough to surface within the shutdown budget: MeterProviderSdk.Dispose gives the reader
        // only 5 seconds (OpenTelemetry 1.19.1), while the exporter's own default timeout is 10, so
        // a collector that hangs rather than answers can still lose its very last failure to a
        // process that exits before the timeout would have reported one. The tick listener comes
        // down only once nothing more will be queued, and the event listener only once nothing is
        // left to drain.
        provider?.Dispose();
        provider = null;
        DrainExportFailures();

        if (exportFailureLogListenerId >= 0)
        {
            sapi?.Event.UnregisterGameTickListener(exportFailureLogListenerId);
            exportFailureLogListenerId = -1;
        }

        exportFailureLog?.Dispose();
        exportFailureLog = null;
    }

    /// <summary>Runs <paramref name="store"/>, tolerating whatever it throws: writing the freshly
    /// defaulted config back to disk can fail the same way loading one can, a ModConfig directory
    /// mounted read-only among them, and this is the only thing standing between that failure and
    /// ModLoader. Delegate-driven, the same arrangement as <see cref="ConfigLoad.Resolve{T}"/>, so
    /// it needs nothing from the engine and is unit-tested directly.</summary>
    internal static bool TryStoreDefaults(Action store, out string? failureReason)
    {
        try
        {
            store();
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>The one line an admin sees when the id this session generated could not be written
    /// to pulse-otlp.json. Args: the config file's name, the id this session exports. It says what
    /// that costs and both ways out, since the admin is the only one who can take either.</summary>
    internal const string UnsavedInstanceIdMessage =
        "Pulse OTLP exports the generated service.instance.id '{1}' this session but could not save "
        + "it to {0} (a read-only ModConfig folder, say, or a file Pulse cannot rewrite), so the next "
        + "start will export a different one and every restart will begin a new set of series in "
        + "the backend. To keep one, set ServiceInstanceId in {0} to any text you like, '{1}' "
        + "included, or set OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id> in the server's "
        + "environment.";

    /// <summary>Whether the config file, read back through <paramref name="load"/>, holds
    /// <paramref name="instanceId"/>: what the next start will find in it. False for a file that is
    /// gone or will not load, since <see cref="ConfigLoad.Resolve{T}"/> then hands back a default
    /// config, whose id is blank and so never equals a generated one. Delegate-driven, like
    /// <see cref="TryStoreDefaults"/>, so it is unit-tested without an engine.</summary>
    internal static bool FileHoldsInstanceId(Func<PulseOtlpConfig?> load, string instanceId)
        => string.Equals(
            ConfigLoad.Resolve(load, () => new PulseOtlpConfig()).Config.ServiceInstanceId,
            instanceId, StringComparison.Ordinal);

    private void OnDrainExportFailures(float _) => DrainExportFailures();

    private void OnDrainExportFailuresError(Exception e) => Mod.Logger.Error(e);

    /// <summary>Passed as an argument, never as the format string: the game's logger runs every
    /// message through string.Format, and a backend's JSON error body can carry braces that would
    /// throw and lose the line. Failures are Warning, since DieAboveErrorCount counts Error and
    /// Fatal; the "succeeded" line is Notification, so the very first export on a healthy server
    /// does not read as a warning about anything.</summary>
    private void DrainExportFailures() => exportFailureLog?.Drain(
        line => Mod.Logger.Warning("{0}", line),
        line => Mod.Logger.Notification("{0}", line));
}
