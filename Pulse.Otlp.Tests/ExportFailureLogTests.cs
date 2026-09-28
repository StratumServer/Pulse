using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    /// <summary>Sent as the exporter's Authorization header on most tests. Never expected to
    /// appear in a queued line: the whole point of reading the EventSource instead of the request
    /// is that the SDK's own diagnostics never carry request headers, and a collector that echoes
    /// one back must be redacted regardless.</summary>
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

    /// <summary>The private rate-limit and cap dictionary, reached by reflection: the only way to
    /// prove eviction and re-logging without an actual ten-minute wait in a test.</summary>
    private static ConcurrentDictionary<string, long> LastLogged(ExportFailureLog log) =>
        (ConcurrentDictionary<string, long>)typeof(ExportFailureLog)
            .GetField("lastLogged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(log)!;

    private static (List<string> Failures, List<string> Successes) DrainAll(ExportFailureLog log)
    {
        List<string> failures = [];
        List<string> successes = [];
        log.Drain(failures.Add, successes.Add);
        return (failures, successes);
    }

    [Fact]
    public void Failure_Http401_QueuesExactlyOneLine_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"status\":\"error\",\"error\":\"authentication error: invalid token\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint);

        // Three separate export attempts, all failing the same way: the rate limit keeps this to
        // one line, not three.
        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();

        (List<string> failures, List<string> successes) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Empty(successes);
        Assert.Contains($"Pulse OTLP export to {endpoint} failed:", line);
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
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains($"Pulse OTLP export to {endpoint} failed:", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_Timeout_IsLogged_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (0, string.Empty));
        using Rig rig = BuildRig(endpoint, timeoutMs: 300);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains($"Pulse OTLP export to {endpoint} failed:", line);
        Assert.Contains("HttpClient.Timeout", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_Grpc_NonOkStatus_IsLogged_AndNeverLeaksTheConfiguredHeader()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}";
        using FakeGrpcFailureCollector collector = new(port);
        using Rig rig = BuildRig(endpoint, protocol: OtlpExportProtocol.Grpc);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains($"Pulse OTLP export to {endpoint}", line);
        Assert.Contains("Unauthenticated", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Recovery_QueuesASucceededLine_OnlyForTheFirstDeliveryAfterAFailure()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(
            port, n => n <= 2 ? (401, "{\"error\":\"invalid token\"}") : (200, string.Empty));
        using Rig rig = BuildRig(endpoint);
        List<string> allFailures = [];
        List<string> allSuccesses = [];

        rig.Provider.ForceFlush(); // fails
        (List<string> f, List<string> s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Empty(allSuccesses);

        rig.Provider.ForceFlush(); // fails again, inside the rate limit window: no new line
        (f, s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Empty(allSuccesses);

        rig.Provider.ForceFlush(); // recovers
        (f, s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Contains($"Pulse OTLP export to {endpoint} succeeded.", Assert.Single(allSuccesses));

        rig.Provider.ForceFlush(); // still healthy: no second succeeded line
        (f, s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Single(allSuccesses);
    }

    [Fact]
    public void Recovery_SuccessThenFailureThenSuccess_LogsBothSuccessesAndTheOneFailure()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, n => n == 2 ? (401, string.Empty) : (200, string.Empty));
        using Rig rig = BuildRig(endpoint);
        List<string> allFailures = [];
        List<string> allSuccesses = [];

        rig.Provider.ForceFlush(); // healthy from the very first export
        (List<string> f, List<string> s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Empty(allFailures);
        Assert.Single(allSuccesses);

        rig.Provider.ForceFlush(); // fails once
        (f, s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Single(allSuccesses);

        rig.Provider.ForceFlush(); // recovers: a second, distinct success line
        (f, s) = DrainAll(rig.Log);
        allFailures.AddRange(f);
        allSuccesses.AddRange(s);
        Assert.Single(allFailures);
        Assert.Equal(2, allSuccesses.Count);
    }

    [Fact]
    public void Failure_IsLoggedAgain_OnceItsEntryIsOlderThanTheRepeatWindow()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, string.Empty));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();
        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Single(failures);

        // Back-date every tracked entry past the ten-minute window instead of waiting for one.
        ConcurrentDictionary<string, long> tracked = LastLogged(rig.Log);
        foreach (string key in tracked.Keys)
        {
            tracked[key] -= (long)TimeSpan.FromMinutes(11).TotalMilliseconds;
        }

        rig.Provider.ForceFlush();
        (failures, _) = DrainAll(rig.Log);
        Assert.Single(failures);
    }

    [Fact]
    public void FailureBody_ContainingBraces_IsLoggedIntact()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"status\":\"error\",\"error\":\"nested {braces} in the message\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
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

#pragma warning disable CA2241 // Deliberate: this is the exact trap PulseOtlpModSystem's drain call
        // must avoid. string.Format(line) here proves the game's logger would throw if a queued
        // line were ever passed as the format string itself, rather than as the argument to a
        // fixed "{0}" template.
        Assert.Throws<FormatException>(() => string.Format(line));
#pragma warning restore CA2241
        Assert.Equal(line, string.Format("{0}", line));
    }

    [Fact]
    public void ResponseBody_IsClippedAt200Characters()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        string longBody = "{\"error\":\"" + new string('x', 300) + "\"}";
        using FakeCollector collector = new(port, _ => (401, longBody));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(longBody, line);
        Assert.Contains(longBody[..200] + "...", line);
    }

    /// <summary>P2: clipping at a fixed character count can land exactly between the two UTF-16
    /// code units of a surrogate pair (an emoji, say), which would otherwise leave an unpaired
    /// high surrogate, invalid UTF-16, sitting right before the "...".</summary>
    [Fact]
    public void ResponseBody_ClipNeverSplitsASurrogatePair()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        string body = new string('x', 199) + "\U0001F600" + "tail";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        int cut = line.IndexOf("...", StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(line[cut - 1]), "the clip left an unpaired high surrogate");
    }

    [Fact]
    public void ResponseTooLargeToRead_IsLogged_ViaTheMessageTemplateFallback()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        string oversizedBody = "{\"error\":\"" + new string('x', 2000) + "\"}";
        using FakeCollector collector = new(port, _ => (401, oversizedBody));
        using Rig rig = BuildRig(endpoint, maxResponseSizeBytes: 1024);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        // No exception and no gRPC status travel on this event, only the endpoint and two byte
        // counts, so this is the one failure kind that reaches Cause's message-template fallback
        // rather than an exception message or a status string.
        string line = Assert.Single(failures);
        Assert.Contains("failed: The response from", line);
        Assert.Contains("was discarded because its size of", line);
        Assert.Contains("exceeds the maximum response size of 1024 bytes", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    [Fact]
    public void Failure_NeverLeaksTheHeaderValue_EvenWhenTheCollectorEchoesIt()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string credential = "Bearer " + SecretHeaderValue;
        using EchoingCollector collector = new(port);
        using Rig rig = BuildRig(endpoint, headerValue: credential);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains("invalid credentials", line);
        Assert.DoesNotContain(SecretHeaderValue, line);
        Assert.DoesNotContain(credential, line);
        Assert.Contains("\"got\":\"***\"", line);
    }

    /// <summary>The regex fallback, not the exact-value match: a credential shaped like a bearer
    /// or basic token but not equal to anything this mod itself configured (someone else's, or a
    /// reformatted echo) is still not something the server log should carry whole.</summary>
    [Fact]
    public void Failure_RedactsABearerShapedCredential_EvenWhenItIsNotTheConfiguredValue()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"error\":\"nope\",\"hint\":\"Bearer totally-unrelated-credential\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain("totally-unrelated-credential", line);
        Assert.Contains("Bearer ***", line);
    }

    [Fact]
    public void ForeignExportersOnOtherEndpoints_AreIgnored_SuccessesAndFailuresAlike()
    {
        int pulsePort = FreePort();
        int otherOkPort = FreePort();
        int otherDeadPort = FreePort(); // nothing ever listens here: connection refused
        string pulseEndpoint = $"http://127.0.0.1:{pulsePort}/v1/metrics";

        using FakeCollector pulseCollector = new(pulsePort, _ => (401, string.Empty));
        using FakeCollector otherOkCollector = new(otherOkPort, _ => (200, string.Empty));

        using Rig rig = BuildRig(pulseEndpoint);
        using ForeignExporter otherOk = BuildForeignExporter($"http://127.0.0.1:{otherOkPort}/othermod/v1/metrics");
        using ForeignExporter otherDead = BuildForeignExporter($"http://127.0.0.1:{otherDeadPort}/othermod/v1/traces");

        rig.Provider.ForceFlush();     // Pulse fails: 401
        otherOk.Provider.ForceFlush(); // another mod succeeds
        otherDead.Provider.ForceFlush(); // another mod fails

        (List<string> failures, List<string> successes) = DrainAll(rig.Log);

        string failureLine = Assert.Single(failures);
        Assert.Contains(pulseEndpoint, failureLine);
        Assert.Empty(successes);
    }

    [Fact]
    public void DistinctFailureKind_IsLogged_WhenStaleEntriesFreeRoomInTheCap()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, string.Empty));
        using Rig rig = BuildRig(endpoint);

        // Fill every one of the 32 slots with a kind that is already an hour old.
        ConcurrentDictionary<string, long> tracked = LastLogged(rig.Log);
        long longAgo = Environment.TickCount64 - (60 * 60_000);
        for (int i = 0; i < 32; i++)
        {
            tracked[$"999 stale kind {i}"] = longAgo;
        }

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        // The cap's 32 slots were all stale, so evicting them first leaves room for this new,
        // 33rd kind; before eviction this would have been dropped and lines would be empty.
        Assert.Single(failures);
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
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";

        // Math.Min guards a request past the 33rd: the exporter can retry or probe beyond what
        // this test drives, and indexing past the array would throw inside the listener's accept
        // loop, which otherwise cost several seconds resolving the broken connection.
        using FakeCollector collector = new(port, n => (codes[Math.Min(n, codes.Length) - 1], string.Empty));
        using Rig rig = BuildRig(endpoint);

        foreach (int _ in codes)
        {
            rig.Provider.ForceFlush();
        }

        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Equal(32, failures.Count);
    }

    /// <summary>Everything one test needs to drive the real exporter against a fake collector: its
    /// own meter (kept alive for as long as the rig is), the provider built on top of it, and the
    /// listener reading the exporter's own diagnostics for Pulse's own configured endpoint.
    /// </summary>
    private sealed record Rig(MeterProvider Provider, ExportFailureLog Log, Meter Meter) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Log.Dispose();
            Meter.Dispose();
        }
    }

    /// <summary>A second exporter in the same process, on its own endpoint, with no
    /// <see cref="ExportFailureLog"/> of its own: standing in for another mod's OTLP export, which
    /// Pulse's own listener has to ignore regardless of whether it succeeds or fails.</summary>
    private sealed record ForeignExporter(MeterProvider Provider, Meter Meter) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Meter.Dispose();
        }
    }

    private static Rig BuildRig(
        string endpoint, int timeoutMs = 5000, OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf,
        int? maxResponseSizeBytes = null, string headerValue = SecretHeaderValue)
    {
        ExportFailureLog log = new([headerValue], new Uri(endpoint));
        (MeterProvider provider, Meter meter) = BuildProvider(
            endpoint, headerValue, timeoutMs, protocol, maxResponseSizeBytes);
        return new Rig(provider, log, meter);
    }

    private static ForeignExporter BuildForeignExporter(string endpoint)
    {
        (MeterProvider provider, Meter meter) = BuildProvider(endpoint, "k=v", 2000, OtlpExportProtocol.HttpProtobuf, null);
        return new ForeignExporter(provider, meter);
    }

    private static (MeterProvider Provider, Meter Meter) BuildProvider(
        string endpoint, string headerValue, int timeoutMs, OtlpExportProtocol protocol, int? maxResponseSizeBytes)
    {
        Meter meter = new($"Pulse.Otlp.Tests.{Guid.NewGuid()}");
        meter.CreateCounter<long>("test_counter").Add(1);

        MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = new Uri(endpoint);
                exporter.Protocol = protocol;
                exporter.Headers = $"Authorization={headerValue}";
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

        return (provider, meter);
    }
}
