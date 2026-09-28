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

    /// <summary>Two exporters whose configured base grpc endpoints differ can still reach export
    /// paths where one is a character-level string prefix of the other ("/pre" of "/prefix"); only
    /// exact equality against the one exact address Pulse's own export reaches tells them apart.
    /// A prefix check would have logged the foreign exporter's failure as Pulse's own.</summary>
    [Fact]
    public void Grpc_RejectsAForeignExporter_WhoseBasePathIsOnlyAStringPrefixOfPulsesOwn()
    {
        int port = FreePort();
        using FakeGrpcFailureCollector collector = new(port);
        using Rig rig = BuildRig($"http://127.0.0.1:{port}/pre", protocol: OtlpExportProtocol.Grpc);
        using ForeignExporter foreign = BuildForeignExporter(
            $"http://127.0.0.1:{port}/prefix/othermod", OtlpExportProtocol.Grpc);

        foreign.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Empty(failures);
    }

    /// <summary>The exporter's own path join collapses only a single trailing slash on the base
    /// into the grpc export path's own leading one; a base kept with two by a config typo still
    /// reaches an address ownGrpcExportPath has to match exactly, or every export against it,
    /// failing or succeeding, goes unrecognised.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/prefix")]
    public void Grpc_MatchesOwnExport_ForAFailure_WhenTheConfiguredBaseKeepsATrailingDoubleSlash(string basePath)
    {
        int port = FreePort();
        using FakeGrpcFailureCollector collector = new(port);
        using Rig rig = BuildRig($"http://127.0.0.1:{port}{basePath}//", protocol: OtlpExportProtocol.Grpc);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Contains("Unauthenticated", Assert.Single(failures));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/prefix")]
    public void Grpc_MatchesOwnExport_ForASuccess_WhenTheConfiguredBaseKeepsATrailingDoubleSlash(string basePath)
    {
        int port = FreePort();
        using FakeGrpcFailureCollector collector = new(port, grpcStatus: 0);
        using Rig rig = BuildRig($"http://127.0.0.1:{port}{basePath}//", protocol: OtlpExportProtocol.Grpc);

        rig.Provider.ForceFlush();

        (List<string> failures, List<string> successes) = DrainAll(rig.Log);
        Assert.Empty(failures);
        Assert.Contains("succeeded", Assert.Single(successes));
    }

    /// <summary>Event 40's declared response size is not part of its key: three different sizes
    /// (a real backend's error body length varies export to export) must still read as the same
    /// kind, not three, contradicting the rate limit and the cap alike.</summary>
    [Fact]
    public void ResponseTooLarge_WithDifferentDeclaredSizes_StillGivesOneLine()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(
            port, n => (401, "{\"error\":\"" + new string('x', 2000 + (n * 37)) + "\"}"));
        using Rig rig = BuildRig(endpoint, maxResponseSizeBytes: 1024);

        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();
        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Single(failures);
    }

    /// <summary>The cause is collector-controlled too (gRPC's Detail field, here): a 5000 character
    /// one must be clipped exactly like an oversized response body is, not embedded whole.</summary>
    [Fact]
    public void GrpcCause_LongerThan200Characters_IsClipped()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}";
        string longDetail = new string('d', 5000);
        using FakeGrpcFailureCollector collector = new(port, longDetail);
        using Rig rig = BuildRig(endpoint, protocol: OtlpExportProtocol.Grpc);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(longDetail, line);
        Assert.True(line.Length < 1000, $"expected a clipped line, got {line.Length} characters");
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

    /// <summary>The shape TryResolveEndpoint now produces for a configured endpoint that already
    /// carries a query string: the query survives after the appended signal path. The endpoint
    /// filter still has to recognise the export as Pulse's own, which it does without any change
    /// of its own, since it already compares scheme, host, port and path only, the same components
    /// the SDK's own EventSource payload is redacted to.</summary>
    [Fact]
    public void Failure_IsLogged_WhenTheConfiguredEndpointCarriesAQueryString()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics?key=abc";
        using FakeCollector collector = new(port, _ => (401, string.Empty));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        Assert.Contains("failed:", Assert.Single(failures));
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
    /// reformatted echo) is still not something the server log should carry whole. Also proves the
    /// token68 restriction: the closing quote and brace right after the credential, and Pulse's own
    /// suffix text after that, both survive intact, which a plain non-whitespace match would not
    /// have left alone.</summary>
    [Fact]
    public void Failure_RedactsABearerShapedCredential_WithoutSwallowingWhatFollowsInACompactBody()
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
        Assert.Contains("{\"error\":\"nope\",\"hint\":\"Bearer ***\"}", line);
        Assert.Contains("Metrics are not reaching the backend", line);
    }

    /// <summary>The regex is case-insensitive: a backend is not obliged to send the scheme back
    /// capitalised the way the specification writes it.</summary>
    [Fact]
    public void Failure_RedactsALowercaseBearerCredential_EvenWhenItIsNotTheConfiguredValue()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"hint\":\"bearer some-other-unconfigured-token\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain("some-other-unconfigured-token", line);
        Assert.Contains("bearer ***", line);
    }

    /// <summary>An echo that drops the scheme entirely, showing only the bare credential, still has
    /// to be caught: the exact-value pass matches the credential half of a configured "scheme
    /// credential" value on its own, not only the value whole.</summary>
    [Theory]
    [InlineData("Bearer")]
    [InlineData("Basic")]
    public void Failure_RedactsTheCredential_EchoedWithoutItsScheme(string scheme)
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        string body = $"{{\"got\":\"{SecretHeaderValue}\"}}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, headerValue: $"{scheme} {SecretHeaderValue}");

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(SecretHeaderValue, line);
        Assert.Contains("\"got\":\"***\"", line);
    }

    /// <summary>PHP's json_encode escapes a literal "/" as "\/" by default; System.Text.Json does
    /// not. A backend built on a different stack from the SDK's own can still echo a slash-bearing
    /// value (a base64 API key, say) in that shape.</summary>
    [Fact]
    public void Failure_RedactsAPhpStyleSlashEscapedCredential()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string apiKey = "abc+def/ghi=";
        const string body = "{\"key\":\"abc+def\\/ghi=\"}";
        using FakeCollector collector = new(port, _ => (401, body));

        // x-api-key, not Authorization: a bare value with no "<scheme> <token>" shape is not a
        // valid Authorization header as far as HttpClient's own parsing is concerned, and this
        // test wants the export to actually reach the collector.
        using Rig rig = BuildRig(endpoint, headerValue: apiKey, headerName: "x-api-key");

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain("ghi=", line);
        Assert.Contains("\"key\":\"***\"", line);
    }

    /// <summary>The SDK trims a header value before it ever reaches the wire; the redactor is only
    /// ever handed the raw, padded configured value, so it has to trim its own copy before
    /// searching, or a padded config value would never match anything a real collector sees.
    /// </summary>
    [Fact]
    public void Failure_RedactsTheCredential_EvenWhenTheConfiguredValueHasPadding()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using EchoingCollector collector = new(port);
        using Rig rig = BuildRig(endpoint, headerValue: $"  Bearer {SecretHeaderValue}  ");

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(SecretHeaderValue, line);
    }

    /// <summary>A short, unrelated value configured before a longer secret (a tenant or org id
    /// ahead of the real credential, say) used to redact value by value in that same order: the
    /// short value's own pass landed inside the longer secret's own exact text first, and the
    /// longer value's own, more specific match then found nothing left to match, leaving a
    /// fragment of the real secret in the log.</summary>
    [Theory]
    [InlineData(
        "1", "Bearer tok1en+x/yZ9q==",
        "{\"X-Scope-OrgID\":\"1\",\"Authorization\":\"Bearer tok1en+x/yZ9q==\"}",
        "\"X-Scope-OrgID\":\"1\"", "\"Authorization\":\"***\"")]
    [InlineData(
        "a", "k3ya1b2c9zQ",
        "{\"tenant\":\"a\",\"x-api-key\":\"k3ya1b2c9zQ\"}",
        "\"tenant\":\"a\"", "\"x-api-key\":\"***\"")]
    public void Failure_RedactsTheLongerSecret_EvenWhenAShorterUnrelatedValuePrecedesItInConfig(
        string shortValue, string longSecret, string body, string untouchedFragment, string redactedFragment)
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, secrets: [shortValue, longSecret]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains(untouchedFragment, line);
        Assert.Contains(redactedFragment, line);
    }

    /// <summary>The length floor alone is not what fixes ordering: even once both configured
    /// values clear it, a shorter one whose own literal text happens to sit inside a longer
    /// secret's text must still not be replaced first, or the longer secret's own, more specific
    /// match finds nothing left in the text to match against.</summary>
    [Fact]
    public void Failure_RedactsTheLongerSecret_EvenWhenAShorterSecretsOwnTextSitsInsideIt()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"X-Scope-OrgID\":\"shortid\",\"Authorization\":\"Bearer shortid-rest-of-token\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, secrets: ["shortid", "Bearer shortid-rest-of-token"]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        Assert.Contains("\"Authorization\":\"***\"", Assert.Single(failures));
    }

    /// <summary>A configured value under 6 characters reads as an ordinary id, not a credential: it
    /// must never stamp "***" over an unrelated character it happens to share with ordinary text,
    /// the "a" in "indicate" here, part of HttpClient's own exception message and present with no
    /// backend response body involved at all.</summary>
    [Fact]
    public void Failure_ShortConfiguredValue_NeverStampsOverOrdinaryCauseText()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, string.Empty));
        using Rig rig = BuildRig(endpoint, secrets: ["a"]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        Assert.Contains("does not indicate success: 401", Assert.Single(failures));
    }

    /// <summary>The same floor holds for the credential half of a "scheme credential" shaped value:
    /// "tenant 1" clears it as a whole, but its "1" alone must not stamp over the status code.</summary>
    [Fact]
    public void Failure_ShortCredentialHalf_NeverStampsOverOrdinaryCauseText()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, string.Empty));
        using Rig rig = BuildRig(endpoint, secrets: ["tenant 1"]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        Assert.Contains("does not indicate success: 401", Assert.Single(failures));
    }

    /// <summary>The fallback regex requires a real credential's length, not just the word "bearer"
    /// or "basic": ordinary prose that merely mentions either, with no secret configured at all,
    /// must come through unredacted.</summary>
    [Theory]
    [InlineData("missing bearer token")]
    [InlineData("Basic auth required")]
    public void Failure_OrdinaryProseMentioningAScheme_IsNeverRedacted_WhenNoSecretIsConfigured(string body)
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, secrets: []);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);
        Assert.Contains(body, Assert.Single(failures));
    }

    /// <summary>The other side of the length floor: a real credential, unconfigured, still has to
    /// be caught the moment it clears 8 characters.</summary>
    [Fact]
    public void Failure_RedactsAnUnconfiguredBearerCredential_AtLeast8CharactersLong()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string body = "{\"hint\":\"Bearer eight888\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, secrets: []);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain("eight888", line);
        Assert.Contains("Bearer ***", line);
    }

    /// <summary>JSON allows either letter case in a \uXXXX escape; System.Text.Json's own encoder
    /// always writes the uppercase form (confirmed against 1.19.1's own dependency: '+' becomes
    /// "+"), but nothing says a backend's own encoder picks the same one this mod's escaped
    /// target was built with. Case-insensitive matching is what catches this, not a second,
    /// lowercase target.</summary>
    [Fact]
    public void Failure_RedactsACredential_EscapedWithLowercaseHexDigits()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        const string secret = "tok1en+x/yZ9q==";
        const string body = "{\"got\":\"tok1en\\u002bx/yZ9q==\"}";
        using FakeCollector collector = new(port, _ => (401, body));
        using Rig rig = BuildRig(endpoint, secrets: [secret]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain("tok1en", line);
        Assert.Contains("\"got\":\"***\"", line);
    }

    /// <summary>Mirrors OtlpOptions.SecretValues(null) and SecretValues of a Headers block holding
    /// a null value: an empty secrets list, the shape PulseOtlpModSystem now always passes instead
    /// of letting a null Headers block or a null value inside it reach the constructor. Nothing
    /// here may throw, and a failure must still be logged.</summary>
    [Fact]
    public void Failure_IsStillLogged_WhenNoSecretsAreConfigured()
    {
        int port = FreePort();
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (401, "{\"error\":\"unauthorized\"}"));
        using Rig rig = BuildRig(endpoint, secrets: []);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.Contains("unauthorized", line);
    }

    /// <summary>LoggableEndpoint already keeps a query string out of the startup line Pulse writes
    /// itself, but a 4xx body that echoes the request target back (a reverse proxy's own error
    /// page, say) would put a signed-URL backend's own secret straight into the log unless the
    /// query value is in the secrets list too, the same as a header value already is. Built with
    /// OtlpOptions.EndpointSecrets, the way PulseOtlpModSystem now actually assembles the list.
    /// </summary>
    [Fact]
    public void Failure_RedactsTheEndpointsQueryValue_WhenTheCollectorEchoesTheRequestTarget()
    {
        int port = FreePort();
        const string queryToken = "endpoint-query-secret-987654";
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics?token={queryToken}";
        using FakeCollector collector = new(
            port, _ => (404, $"{{\"error\":\"no route for /v1/metrics?token={queryToken}\"}}"));
        using Rig rig = BuildRig(endpoint, secrets: [.. OtlpOptions.EndpointSecrets(new Uri(endpoint))]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(queryToken, line);
        Assert.Contains("\"error\":\"no route for /v1/metrics?token=***\"", line);
    }

    /// <summary>A real signed URL's query value is base64 and carries '+', '/' or '=' escaped as
    /// %2B, %2F or %3D; the raw request target on the wire, and so a reverse proxy's own echo of
    /// it, carries exactly that escaped form, never the unescaped one. OtlpOptions.EndpointSecrets
    /// used to yield only Uri.UnescapeDataString's output, which left this shape, the one every
    /// real Azure SAS or AWS-style signature is actually in, unredacted.</summary>
    [Fact]
    public void Failure_RedactsTheEndpointsQueryValue_WhenTheEchoCarriesThePercentEscapedForm()
    {
        int port = FreePort();
        const string escapedSecret = "q8Wf3kL2%2BxYzAbCdEfGh%3D";
        const string unescapedSecret = "q8Wf3kL2+xYzAbCdEfGh=";
        string endpoint = $"http://127.0.0.1:{port}/v1/metrics?sig={escapedSecret}";
        using FakeCollector collector = new(
            port, _ => (404, $"{{\"error\":\"no route for /v1/metrics?sig={escapedSecret}\"}}"));
        using Rig rig = BuildRig(endpoint, secrets: [.. OtlpOptions.EndpointSecrets(new Uri(endpoint))]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(escapedSecret, line);
        Assert.DoesNotContain(unescapedSecret, line);
        Assert.Contains("\"error\":\"no route for /v1/metrics?sig=***\"", line);
    }

    /// <summary>The userinfo half of EndpointSecrets: a backend that authenticates a signed URL
    /// through the user:password form rather than a query parameter must be redacted the same way.
    /// The configured endpoint carries the userinfo straight through into what the exporter dials
    /// (HttpClient itself drops it before the request goes out, confirmed against .NET 10, but
    /// still connects and reads the response), and into what ExportFailureLog compares a failure's
    /// own endpoint payload against: GetComponents(SchemeAndServer) never includes UserInfo (see
    /// LoggableEndpoint's own test), so the match is unaffected by it being there at all.</summary>
    [Fact]
    public void Failure_RedactsTheEndpointsUserinfo_WhenTheCollectorEchoesIt()
    {
        int port = FreePort();
        const string password = "endpoint-userinfo-secret-123456";
        string endpoint = $"http://reporter:{password}@127.0.0.1:{port}/v1/metrics";
        using FakeCollector collector = new(port, _ => (404, $"{{\"hint\":\"tried {password}\"}}"));
        using Rig rig = BuildRig(endpoint, secrets: [.. OtlpOptions.EndpointSecrets(new Uri(endpoint))]);

        rig.Provider.ForceFlush();

        (List<string> failures, _) = DrainAll(rig.Log);

        string line = Assert.Single(failures);
        Assert.DoesNotContain(password, line);
        Assert.Contains("\"hint\":\"tried ***\"", line);
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
        int? maxResponseSizeBytes = null, string headerValue = SecretHeaderValue,
        IReadOnlyCollection<string>? secrets = null, string headerName = "Authorization")
    {
        ExportFailureLog log = new(secrets ?? [headerValue], new Uri(endpoint));
        (MeterProvider provider, Meter meter) = BuildProvider(
            endpoint, headerName, headerValue, timeoutMs, protocol, maxResponseSizeBytes);
        return new Rig(provider, log, meter);
    }

    private static ForeignExporter BuildForeignExporter(
        string endpoint, OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf)
    {
        (MeterProvider provider, Meter meter) = BuildProvider(
            endpoint, "x-scope-orgid", "othermod", 2000, protocol, null);
        return new ForeignExporter(provider, meter);
    }

    private static (MeterProvider Provider, Meter Meter) BuildProvider(
        string endpoint, string headerName, string headerValue, int timeoutMs, OtlpExportProtocol protocol,
        int? maxResponseSizeBytes)
    {
        Meter meter = new($"Pulse.Otlp.Tests.{Guid.NewGuid()}");
        meter.CreateCounter<long>("test_counter").Add(1);

        MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = new Uri(endpoint);
                exporter.Protocol = protocol;

                // Through RenderHeaders, the way PulseOtlpModSystem actually builds this string,
                // not a raw "{headerName}={headerValue}": the padding and escaping tests below
                // depend on going through the real encoding path rather than one of this test's
                // own shortcuts.
                exporter.Headers = OtlpOptions.RenderHeaders(new Dictionary<string, string> { [headerName] = headerValue });
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
