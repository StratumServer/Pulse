using System.Text;
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
    private const int CollectorPort = 39471;

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
        FakeGrpcCollector.Export export = await WaitForExport();

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
        // name that follows it.
        string body = Encoding.Latin1.GetString(export.Body);
        Assert.Contains("pulse_server_ticks_total", body, StringComparison.Ordinal);
        Assert.Contains("Pulse.Server", body, StringComparison.Ordinal);

        // service.name is a resource attribute, not a metric or scope name, but it travels in the
        // same length-prefixed encoding inside the same protobuf message, so it is just as findable
        // in the raw bytes.
        Assert.Contains("pulse-atlas-grpc", body, StringComparison.Ordinal);
    }

    private Task<FakeGrpcCollector.Export> WaitForExport()
        => Exports.WaitFor(() => collector.First, () => World.Ticks(10), ExportInterval * 12, CollectorPort);
}
