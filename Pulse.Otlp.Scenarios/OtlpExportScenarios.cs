using System.Text;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>The whole point of the second mod, proved end to end: a real server, both mods loaded
/// from folders laid out like their zips, and a collector on the other end of the socket receiving
/// the base mod's metrics without either mod referencing the other.</summary>
/// <remarks>The port is a constant rather than one the OS picks, because the config file is seeded
/// from disk before the server boots and so cannot carry a port chosen at runtime. Same trade the
/// bind-failure scenario makes in the base suite.</remarks>
[AtlasDataFiles("data/otlp", TargetPath = "ModConfig")]
public class OtlpExportScenarios : AtlasScenarioBase, IDisposable
{
    private const int CollectorPort = 39469;

    /// <summary>Seeded IntervalSeconds, so the first export is at most this far away plus the
    /// startup the reader does before its first wait.</summary>
    private static readonly TimeSpan ExportInterval = TimeSpan.FromSeconds(5);

    private readonly FakeCollector collector;

    public OtlpExportScenarios()
    {
        // xUnit builds the test class before Atlas boots the host, so the collector is already
        // listening by the time the exporter's first export goes out.
        collector = new FakeCollector(CollectorPort);
    }

    public void Dispose()
    {
        collector.Dispose();
        GC.SuppressFinalize(this);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Exporter_Pushes_PulsesMetrics_ToAnOtlpCollector()
    {
        FakeCollector.Export export = await WaitForExport();

        Assert.Equal("POST", export.Method);
        Assert.Equal("/v1/metrics", export.Path);
        Assert.Equal("application/x-protobuf", export.ContentType);
        Assert.NotEmpty(export.Body);

        // Instrument and scope names travel as plain UTF-8 length-prefixed strings inside the
        // protobuf payload, so finding them in the raw bytes is enough to prove the base mod's
        // meter reached the collector. Parsing the payload would only test a protobuf library.
        string body = Encoding.UTF8.GetString(export.Body);
        Assert.Contains("pulse_server_ticks_total", body, StringComparison.Ordinal);
        Assert.Contains("Pulse.Server", body, StringComparison.Ordinal);

        // service.name is a resource attribute, not a metric or scope name, but it travels in the
        // same length-prefixed UTF-8 encoding inside the same protobuf message, so it is just as
        // findable in the raw bytes.
        Assert.Contains("pulse-atlas-test", body, StringComparison.Ordinal);

        // The configured header arrived with it: this is how a hosted backend authenticates.
        Assert.Equal("atlas", export.OrgId);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Server_Keeps_Ticking_WhileExporting()
    {
        await WaitForExport();

        BlockPos pos = World.Spawn.Offset(1, 1, 0);
        World.SetBlock("game:chest-east", pos);
        await World.Ticks(30);

        Assert.Equal("game:chest-east", World.BlockAt(pos).Code.ToString());
    }

    private Task<FakeCollector.Export> WaitForExport()
        => Exports.WaitFor(() => collector.First, () => World.Ticks(10), ExportInterval * 6, CollectorPort);
}
