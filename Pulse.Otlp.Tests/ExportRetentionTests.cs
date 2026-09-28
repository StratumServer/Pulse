using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>The shipped default endpoint fails every export for anyone without a collector, for
/// the whole life of the server, so a failed export must leave nothing behind. Measures the live
/// heap after forced full GCs, which is process-wide: the class runs alone, never alongside the
/// other test classes.</summary>
[CollectionDefinition(nameof(ExportRetentionTests), DisableParallelization = true)]
[Collection(nameof(ExportRetentionTests))]
public class ExportRetentionTests
{
    [Fact]
    public void FailedExports_AgainstARefusedConnection_RetainNothing()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Uri endpoint = new($"http://127.0.0.1:{port}/v1/metrics");

        using Meter meter = new($"Pulse.Otlp.Tests.{Guid.NewGuid()}");
        Counter<long> counter = meter.CreateCounter<long>("test_counter");
        using ExportFailureLog log = new([], endpoint);
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = endpoint;
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60_000;
            })
            .Build();

        void Export(int count)
        {
            for (int i = 0; i < count; i++)
            {
                counter.Add(1);
                Assert.False(provider.ForceFlush());
            }
        }

        Export(500); // JIT, pools, the SDK's first-failure diagnostics
        long before = LiveBytes();
        Export(10_000);
        long after = LiveBytes();

        List<string> failures = [];
        log.Drain(failures.Add, _ => { });
        Assert.Single(failures); // the failure path really ran, rate-limited to one line

        // 1 MB over 10 000 exports is 100 B each. Measured on 1.19.1: a one-time step of about
        // 250 KB in the first seconds of exporting, then a few hundred bytes per 10 000 exports.
        // The leak once suspected here (about 1 MB per export) would show as gigabytes.
        Assert.True(after - before < 1_000_000, $"live heap grew {after - before:N0} bytes over 10 000 failed exports");
    }

    private static long LiveBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
