using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Vintagestory.API.Common;
using Xunit;

namespace Pulse.Tests;

public class MetricsHttpServerTests
{
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";

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

    /// <summary>The serve thread is private; reflection is the only way to see whether it is
    /// still running without changing MetricsHttpServer's shape for a test.</summary>
    private static bool ServeThreadAlive(MetricsHttpServer server)
    {
        FieldInfo field = typeof(MetricsHttpServer).GetField("thread", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((Thread)field.GetValue(server)!).IsAlive;
    }

    [Fact]
    public async Task Metrics_Returns200_WithTheExpositionContentType_AndTheRenderedBody()
    {
        const string body = "# HELP x X.\n# TYPE x counter\nx 1\n";
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => body, new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExpositionContentType, response.Content.Headers.GetValues("Content-Type").Single());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/other")]
    [InlineData("POST", "/metrics")]
    public async Task NonMatchingRequests_Return404(string method, string path)
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "irrelevant", new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpRequestMessage request = new(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RenderThrows_TheServeThreadSurvives_AndTheFailureIsLoggedAtMostOncePerInterval()
    {
        bool healthy = false;
        const string body = "# healed\n";
        FakeLogger logger = new();
        int port = FreePort();
        using MetricsHttpServer server = new(
            "127.0.0.1", port, () => healthy ? body : throw new InvalidOperationException("boom"), logger);
        server.Start();
        using HttpClient client = new();

        // Two failures back to back: only the first should cross the log-rate-limit gate.
        await TryGet(client, port);
        await TryGet(client, port);

        Assert.Single(logger.Entries, e => e.Type == EnumLogType.Warning);

        // The serve thread kept looping through both failures: a request made once the callback
        // heals still gets answered.
        healthy = true;
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Fires a GET at a callback that is expected to throw and discards however the
    /// connection reacts to that: what this test cares about is what got logged and whether the
    /// thread is still serving afterwards, not the exact wire shape of a half-built response.</summary>
    private static async Task TryGet(HttpClient client, int port)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
            _ = await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Dispose_WhileIdle_ReturnsPromptly_AndStopsTheServeThread()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();
        Assert.True(ServeThreadAlive(server));

        Stopwatch watch = Stopwatch.StartNew();
        server.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Dispose took {watch.Elapsed}");
        Assert.False(ServeThreadAlive(server));
    }

    [Fact]
    public void Start_OnAnAlreadyTakenPort_Throws_AndDisposeAfterwardsIsSafe()
    {
        int port = FreePort();
        TcpListener squatter = new(IPAddress.Loopback, port);
        squatter.Start();
        try
        {
            MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
            Assert.ThrowsAny<Exception>(() => server.Start());
            server.Dispose();
        }
        finally
        {
            squatter.Stop();
        }
    }

    /// <summary>Captures every entry through the one abstract hook LoggerBase funnels its whole
    /// friendly API (Warning, Error, ...) through, so it needs no server and no mod loader.</summary>
    private sealed class FakeLogger : LoggerBase
    {
        private readonly object gate = new();
        private readonly List<(EnumLogType Type, string Message)> entries = [];

        public IReadOnlyList<(EnumLogType Type, string Message)> Entries
        {
            get { lock (gate) { return entries.ToList(); } }
        }

        protected override void LogImpl(EnumLogType logType, string format, object[] args)
        {
            string message = args is { Length: > 0 } ? string.Format(format, args) : format;
            lock (gate)
            {
                entries.Add((logType, message));
            }
        }
    }
}
