using System.Text;
using System.Text.Json.Nodes;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>The same proof as the http/protobuf scenario, over the other protocol the config file
/// accepts. grpc is not a variation on the same request: it is HTTP/2, a service path the exporter
/// appends itself, a length-prefixed message and a status in a trailer, and none of that was ever
/// exercised against a socket.</summary>
/// <remarks>Its own fixture, its own ports and its own seeded config, because the protocol is read
/// once at boot. The ports are constants for the reason the http scenario gives: the config file is
/// seeded from disk before the server boots and so cannot carry a port chosen at runtime.</remarks>
[AtlasDataFiles("data/otlpgrpc", TargetPath = "ModConfig")]
public class OtlpGrpcExportScenarios : AtlasScenarioBase, IDisposable
{
    private const int CollectorPort = 29471;
    private const string TicksCounter = "pulse_server_ticks_total";

    /// <summary>Seeded IntervalSeconds, so the first export is at most this far away plus the
    /// startup the reader does before its first wait.</summary>
    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(5);

    private readonly FakeGrpcCollector collector;

    public OtlpGrpcExportScenarios()
    {
        // xUnit builds the test class before Atlas boots the host, so the collector is already
        // listening by the time the exporter's first export goes out.
        collector = new FakeGrpcCollector(CollectorPort);
    }

    public void Dispose()
    {
        collector.Dispose();
        GC.SuppressFinalize(this);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Exporter_Pushes_PulsesMetrics_OverGrpc()
    {
        FakeGrpcCollector.Export export = await WaitForTicksExport();

        // The exporter builds this path itself from the service definition, which is why the
        // configured endpoint has to stay bare. Seeing it here is what proves the endpoint was not
        // mangled on the way in.
        Assert.Contains(FakeGrpcCollector.ExportPath, export.Headers, StringComparison.Ordinal);
        Assert.Contains("application/grpc", export.Headers, StringComparison.Ordinal);

        // The configured header arrived with it: this is how a hosted backend authenticates. Name
        // and value sit next to each other in the block, separated by one byte holding the value's
        // length, which is the 5 of "atlas". Asserting on the pair is worth the escape: two loose
        // searches could each be satisfied by something else in the block.
        Assert.Contains("x-scope-orgid\u0005atlas", export.Headers, StringComparison.Ordinal);

        // A gRPC message is a compression flag, four big-endian length bytes, then the payload.
        // Checking the length agrees with what arrived is what separates a framed message from a
        // bare protobuf blob posted at a gRPC path.
        Assert.True(export.Body.Length > 5, "the export carried no gRPC message");
        Assert.Equal(0, export.Body[0]);
        int declared = (export.Body[1] << 24) | (export.Body[2] << 16) | (export.Body[3] << 8) | export.Body[4];
        Assert.Equal(export.Body.Length - 5, declared);

        // Instrument and scope names travel as plain length-prefixed strings inside the protobuf
        // payload, so finding them in the raw bytes is enough to prove the base mod's meter reached
        // the collector. Parsing the payload would only test a protobuf library. Latin-1 for the
        // same reason as the header block: one character per byte, so no length prefix can eat the
        // name that follows it. The wait above found the ticks counter the same way.
        string body = Encoding.Latin1.GetString(export.Body);
        Assert.Contains("Pulse.Server", body, StringComparison.Ordinal);

        // service.name is a resource attribute, not a metric or scope name, but it travels in the
        // same length-prefixed encoding inside the same protobuf message, so it is just as findable
        // in the raw bytes.
        Assert.Contains("pulse-atlas-grpc", body, StringComparison.Ordinal);
    }

    /// <summary>The id the server generated for itself is the one on the wire, and the one in its
    /// config file. The fixture has no ServiceInstanceId, like a file 0.2.0 wrote, so the mod makes
    /// one at startup, writes it to pulse-otlp.json and exports it: this is the half of "the same id
    /// after a restart" that happens on the boot that generates it. The other half, that a start
    /// which finds an id in the file exports exactly that and leaves the file alone, is
    /// <see cref="OtlpExportScenarios.Export_Carries_TheConfiguredServiceInstanceId"/>. A scenario
    /// cannot join them by restarting the server: Atlas boots the replacement host in a fresh
    /// scratch directory and seeds ModConfig into it again, so the file one boot wrote never reaches
    /// the next.</summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Export_Carries_TheGeneratedServiceInstanceId()
    {
        FakeGrpcCollector.Export export = await WaitForTicksExport();

        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        string? id = (string?)JsonNode.Parse(File.ReadAllText(configPath))?["ServiceInstanceId"];

        Assert.True(
            Guid.TryParse(id, out Guid parsed) && parsed != Guid.Empty,
            $"ServiceInstanceId is not a generated GUID: {id}");
        Assert.True(
            Exports.CarriesAttribute(export.Body, "service.instance.id", id!),
            $"the export does not carry service.instance.id = {id}");
    }

    /// <summary>The first export that holds the ticks counter, which is not always the first one
    /// the collector gets: that one leaves five seconds after the exporter starts, and on a slow
    /// boot the server has not run a tick by then, so it holds what startup recorded and no ticks
    /// counter. The next one does.</summary>
    private Task<FakeGrpcCollector.Export> WaitForTicksExport()
        => Exports.WaitFor(
            () => collector.FirstWhere(export => Exports.Carries(export.Body, TicksCounter)),
            () => collector.Count, () => World.Ticks(10), ExportInterval * 12, CollectorPort,
            $"export carrying {TicksCounter}");
}
