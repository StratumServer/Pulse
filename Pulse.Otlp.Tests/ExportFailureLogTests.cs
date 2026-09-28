using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>Drives the real 1.19.1 OTLP exporter against a fake collector and checks what
/// <see cref="ExportFailureLog"/> queues, with no server and no mod loader involved: ForceFlush
/// runs an export synchronously, so every assertion here follows a specific, known export rather
/// than a race against a background timer.</summary>
public class ExportFailureLogTests
{
    /// <summary>Sent as the exporter's Authorization header on every test. Never expected to
    /// appear in a queued line: the whole point of reading the EventSource instead of the request
    /// is that the SDK's own diagnostics never carry request headers.</summary>
    private const string SecretHeaderValue = "test-secret-token-should-never-be-logged";

    /// <summary>Binds an ephemeral port and releases it immediately. The window between release
    /// and reuse is a tiny, accepted race: nothing else on this machine runs at test time.</summary>
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void Failure_Http401_QueuesExactlyOneLine_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        const string body = "{\"status\":\"error\",\"error\":\"authentication error: invalid token\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");

        // Three separate export attempts, all failing the same way: the rate limit keeps this to
        // one line, not three.
        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains($"Pulse OTLP export to http://127.0.0.1:{port}/v1/metrics failed:", line);
        Assert.Contains("401", line);
        Assert.Contains(body, line);
        Assert.Contains("logged again at most every 10 minutes", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_ConnectionRefused_IsLogged_AndNeverLeaksTheConfiguredHeader()
    {
        // A free port with nothing listening on it refuses the connection outright.
        int port = FreePort();
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains($"Pulse OTLP export to http://127.0.0.1:{port}/v1/metrics failed:", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_Timeout_IsLogged_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        using FakeCollector collector = new(port, _ => (0, string.Empty));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics", timeoutMs: 300);

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains($"Pulse OTLP export to http://127.0.0.1:{port}/v1/metrics failed:", line);
        Assert.Contains("HttpClient.Timeout", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_Grpc_NonOkStatus_IsLogged_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        using FakeGrpcFailureCollector collector = new(port);
        using Rig rig = BuildRig($"http://127.0.0.1:{port}", protocol: OtlpExportProtocol.Grpc);

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains($"Pulse OTLP export to http://127.0.0.1:{port}", line);
        Assert.Contains("Unauthenticated", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Recovery_QueuesASucceededLine_OnlyForTheFirstDeliveryAfterAFailure()
    {
        int port = FreePort();
        using FakeCollector collector = new(
            port, n => n <= 2 ? (401, "{\"error\":\"invalid token\"}") : (200, string.Empty));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");
        List<string> lines = [];

        rig.Provider.ForceFlush(); // fails
        rig.Log.Drain(lines.Add);
        Assert.Single(lines);
        Assert.Contains("failed:", lines[0]);

        rig.Provider.ForceFlush(); // fails again, inside the rate limit window: no new line
        rig.Log.Drain(lines.Add);
        Assert.Single(lines);

        rig.Provider.ForceFlush(); // recovers
        rig.Log.Drain(lines.Add);
        Assert.Equal(2, lines.Count);
        Assert.Contains($"Pulse OTLP export to http://127.0.0.1:{port}/v1/metrics succeeded.", lines[1]);

        rig.Provider.ForceFlush(); // still healthy: no second succeeded line
        rig.Log.Drain(lines.Add);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void FailureBody_ContainingBraces_IsLoggedIntact()
    {
        int port = FreePort();
        const string body = "{\"status\":\"error\",\"error\":\"nested {braces} in the message\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains(body, line);
    }

    /// <summary>The trap PulseOtlpModSystem's drain call has to avoid: the real server logger
    /// funnels every message through string.Format. A queued line is never itself a safe format
    /// string, since a backend's JSON error body can carry braces string.Format would try to read
    /// as a placeholder; it is safe only as the argument to a fixed "{0}" template.</summary>
    [Fact]
    public void ALineContainingBraces_ThrowsAsAFormatString_ButSurvivesAsAnArgument()
    {
        const string line = "Pulse OTLP export to http://127.0.0.1/v1/metrics failed: Response status "
            + "code does not indicate success: 401 (Unauthorized). The backend answered: "
            + "{\"status\":\"error\",\"error\":\"invalid token\"} Metrics are not reaching the backend; "
            + "check Endpoint and Headers in pulse-otlp.json. This is logged again at most every 10 "
            + "minutes.";

        Assert.Throws<FormatException>(() => string.Format(line));
        Assert.Equal(line, string.Format("{0}", line));
    }

    [Fact]
    public void ResponseBody_IsClippedAt200Characters()
    {
        int port = FreePort();
        string longBody = "{\"error\":\"" + new string('x', 300) + "\"}";
        using FakeCollector collector = new(port, _ => (401, longBody));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        string line = Assert.Single(lines);
        Assert.DoesNotContain(longBody, line);
        Assert.Contains(longBody[..200] + "...", line);
    }

    /// <summary>A response the exporter refuses to even read has no exception or gRPC status in
    /// its event payload, only an endpoint and two byte counts: the one shape that reaches Cause's
    /// own message-template fallback instead of an exception or status string.</summary>
    [Fact]
    public void ResponseTooLargeToRead_IsLogged_ViaTheMessageTemplateFallback()
    {
        int port = FreePort();
        string oversizedBody = "{\"error\":\"" + new string('x', 2000) + "\"}";
        using FakeCollector collector = new(port, _ => (401, oversizedBody));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics", maxResponseSizeBytes: 1024);

        rig.Provider.ForceFlush();

        List<string> lines = [];
        rig.Log.Drain(lines.Add);

        // No exception and no gRPC status travel on this event, only the endpoint and two byte
        // counts, so this is the one failure kind that reaches Cause's message-template fallback
        // rather than an exception message or a status string.
        string line = Assert.Single(lines);
        Assert.Contains("failed: The response from", line);
        Assert.Contains("was discarded because its size of", line);
        Assert.Contains("exceeds the maximum response size of 1024 bytes", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void DistinctFailureKinds_AreCappedAt32()
    {
        // 33 distinct, well-known HTTP status codes: one more than the cap, so the last one proves
        // a kind past it is dropped rather than merely rate-limited.
        int[] codes =
        [
            .. Enumerable.Range(400, 18), // 400-417
            421, 422, 423, 424, 426, 428, 429, 431, 451,
            .. Enumerable.Range(500, 6), // 500-505
        ];
        Assert.True(codes.Length == 33, "this test needs exactly one more code than the cap");

        int port = FreePort();
        using FakeCollector collector = new(port, n => (codes[n - 1], string.Empty));
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/v1/metrics");

        foreach (int _ in codes)
        {
            rig.Provider.ForceFlush();
        }

        List<string> lines = [];
        rig.Log.Drain(lines.Add);
        Assert.Equal(32, lines.Count);
    }

    /// <summary>Everything one test needs to drive the real exporter against a fake collector: its
    /// own meter (kept alive for as long as the rig is), the provider built on top of it, and the
    /// listener reading the exporter's own diagnostics.</summary>
    private sealed record Rig(MeterProvider Provider, ExportFailureLog Log, Meter Meter) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Log.Dispose();
            Meter.Dispose();
        }
    }

    private static Rig BuildRig(
        string endpoint, int timeoutMs = 5000, OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf,
        int? maxResponseSizeBytes = null)
    {
        ExportFailureLog log = new();
        Meter meter = new($"Pulse.Otlp.Tests.{Guid.NewGuid()}");
        meter.CreateCounter<long>("test_counter").Add(1);

        MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = new Uri(endpoint);
                exporter.Protocol = protocol;
                exporter.Headers = $"Authorization={SecretHeaderValue}";
                exporter.TimeoutMilliseconds = timeoutMs;
                if (maxResponseSizeBytes.HasValue)
                {
                    exporter.MaxResponseSizeBytes = maxResponseSizeBytes.Value;
                }

                // Long enough that the periodic reader's own timer never fires during a test:
                // every export here is driven explicitly through ForceFlush instead.
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60_000;
            })
            .Build();

        return new Rig(provider, log, meter);
    }
}
