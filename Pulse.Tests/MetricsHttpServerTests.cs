using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Xunit;
using Xunit.Abstractions;

namespace Pulse.Tests;

public class MetricsHttpServerTests
{
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";

    private readonly ITestOutputHelper output;

    public MetricsHttpServerTests(ITestOutputHelper output)
    {
        this.output = output;
    }

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

    /// <summary>Opens its own connection, writes every chunk in order (a multi-chunk call
    /// exercises a request split across several TCP writes), and returns whatever came back
    /// before the server closed the connection.</summary>
    private static async Task<string> RawRequestAsync(int port, params string[] chunks)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        foreach (string chunk in chunks)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(chunk);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        using MemoryStream received = new();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
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

    [Fact]
    public async Task Head_Returns200_WithTheSameHeaders_ButNoBody()
    {
        const string body = "# HELP x X.\n# TYPE x counter\nx 1\n";
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => body, new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Head, $"http://127.0.0.1:{port}/metrics");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExpositionContentType, response.Content.Headers.GetValues("Content-Type").Single());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/other")]
    public async Task WrongPath_Returns404(string method, string path)
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "irrelevant", new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpRequestMessage request = new(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WrongMethod_OnTheMetricsPath_Returns405(string method)
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "irrelevant", new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpRequestMessage request = new(new HttpMethod(method), $"http://127.0.0.1:{port}/metrics");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("host.docker.internal")]
    [InlineData("some-arbitrary-name.example")]
    public async Task Metrics_Returns200_WhateverTheHostHeaderSays(string host)
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, $"http://127.0.0.1:{port}/metrics");
        request.Headers.Host = host;
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GarbageRequestLine_Returns400_AndTheServerKeepsServing()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        string response = await RawRequestAsync(port, "this is not a request line at all\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", response);

        using HttpClient client = new();
        using HttpResponseMessage next = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task OversizedHeaders_AreRejected_AndTheServerKeepsServing()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        // Larger than MaxHeadBytes before the terminating blank line ever appears, so the server
        // gives up on it rather than growing the buffer to find one.
        string oversized = "GET /metrics HTTP/1.1\r\nX-Pad: " + new string('A', 9000) + "\r\n\r\n";
        string response = await RawRequestAsync(port, oversized);
        Assert.StartsWith("HTTP/1.1 400", response);

        using HttpClient client = new();
        using HttpResponseMessage next = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task RequestSplitAcrossSeveralWrites_IsStillParsed()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        string response = await RawRequestAsync(
            port, "GET ", "/metrics ", "HTTP/1.1\r\n", "Host: ", "127.0.0.1\r\n", "\r\n");

        Assert.StartsWith("HTTP/1.1 200", response);
    }

    /// <summary>RFC 9112 requires an origin server to accept the absolute-form request target as
    /// well as the ordinary origin-form one, the form a request written for a proxy carries.</summary>
    [Fact]
    public async Task AbsoluteFormRequestTarget_IsAccepted()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        string response = await RawRequestAsync(
            port, $"GET http://127.0.0.1:{port}/metrics HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", response);
    }

    [Fact]
    public async Task TwoScrapes_OnSeparateConnections_BothSucceed()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        using HttpClient client = new();
        using HttpResponseMessage first = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        using HttpResponseMessage second = await client.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task IdleClient_IsDroppedAfterTheReceiveTimeout_AndTheNextClientIsServed()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        using (TcpClient idle = new())
        {
            await idle.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = idle.GetStream();

            // Sends nothing at all. The server's own 5 s receive timeout is what has to end
            // this, well within the 8 s this waits for it, or the test times out having proven
            // nothing.
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));
            byte[] buffer = new byte[16];
            int read = await stream.ReadAsync(buffer, cts.Token);
            Assert.Equal(0, read);
        }

        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The per-call receive timeout alone does not bound a request: each single Read
    /// still completes well inside it as long as some byte turns up before it expires, and a
    /// client can keep doing exactly that indefinitely. Only an overall deadline, taken once at
    /// accept, ends this. Uses the internal constructor to shorten that deadline to 1 s; the
    /// per-call timeout stays at its real 5 s throughout, unable to explain a drop this fast on
    /// its own.</summary>
    [Fact]
    public async Task SlowlorisClient_IsDroppedByTheOverallDeadline_NotJustThePerReadTimeout()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger(), requestTimeoutMs: 1000);
        server.Start();

        using TcpClient slow = new();
        await slow.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = slow.GetStream();

        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Watches for the drop concurrently with sending: a graceful close reads back as 0, a
        // reset as an exception, whichever this platform surfaces once the server hangs up.
        Task<bool> waitForDrop = Task.Run(async () =>
        {
            byte[] buffer = new byte[16];
            try
            {
                return await stream.ReadAsync(buffer, cts.Token) == 0;
            }
            catch (IOException)
            {
                return true;
            }
        });

        // One byte every 200 ms: comfortably inside the 5 s per-read socket timeout on every
        // single call, so nothing but an overall deadline can end this.
        byte[] one = Encoding.ASCII.GetBytes("G");
        while (!waitForDrop.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            try
            {
                await stream.WriteAsync(one, cts.Token);
            }
            catch (IOException)
            {
                break;
            }

            await Task.Delay(200, cts.Token);
        }

        Assert.True(await waitForDrop, "the server never dropped the byte-dribbling client");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed} to drop a byte-dribbling client");

        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    public async Task Dispose_WhileAClientIsConnectedButIdle_ReturnsPromptly()
    {
        int port = FreePort();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        using TcpClient stalled = new();
        await stalled.ConnectAsync(IPAddress.Loopback, port);

        // Lets the serve thread actually accept the connection and block on reading its request,
        // so Dispose races against that read rather than against AcceptTcpClient.
        await Task.Delay(200);

        Stopwatch watch = Stopwatch.StartNew();
        server.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Dispose took {watch.Elapsed}");
    }

    /// <summary>A persistent AcceptTcpClient failure, the process out of file descriptors being
    /// the real-world example, must not spin the serve thread at full speed forever behind a log
    /// line that only fires once a minute. Stops the listener directly, through reflection,
    /// rather than through Dispose, so `stopping` stays false and every further accept keeps
    /// failing exactly the way a persistent failure would, not a one-off. Without a backoff this
    /// would run into the tens of thousands of failures within half a second; a low count proves
    /// the loop is sleeping between attempts instead.</summary>
    [Fact]
    public async Task PersistentAcceptFailure_BacksOff_InsteadOfBusySpinning()
    {
        int port = FreePort();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        FieldInfo listenerField = typeof(MetricsHttpServer).GetField("listener", BindingFlags.NonPublic | BindingFlags.Instance)!;
        TcpListener listener = (TcpListener)listenerField.GetValue(server)!;
        listener.Stop();

        await Task.Delay(500);

        Assert.InRange(server.AcceptFailures, 1, 50);

        server.Dispose();
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

    /// <summary>::1 is not guaranteed on every machine this runs on. When binding it fails, this
    /// logs why through the test's own output and stops rather than asserting on an interface
    /// this host may not have.</summary>
    [Fact]
    public async Task Metrics_Returns200_OnIPv6Loopback_WhenTheMachineHasOne()
    {
        int port = FreePort();
        MetricsHttpServer? server = null;
        try
        {
            server = new MetricsHttpServer("::1", port, () => "x", new FakeLogger());
            server.Start();
        }
        catch (SocketException e)
        {
            server?.Dispose();
            output.WriteLine($"skipped: this machine has no IPv6 loopback ({e.Message})");
            return;
        }

        using (server)
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync($"http://[::1]:{port}/metrics");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("*", "0.0.0.0")]
    [InlineData("+", "0.0.0.0")]
    [InlineData("::", "::")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("LOCALHOST", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("10.20.30.40", "10.20.30.40")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("  127.0.0.1  ", "127.0.0.1")]
    [InlineData(" localhost ", "127.0.0.1")]
    [InlineData(" 0.0.0.0 ", "0.0.0.0")]
    public void ParseBind_MapsWildcardsAndNamesAndLiterals(string bind, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), MetricsHttpServer.ParseBind(bind));
    }

    /// <summary>Anything ParseBind cannot make sense of has to fail through IPAddress.Parse's own
    /// FormatException, the exception StartEndpoint already knows how to log cleanly, and never
    /// through a NullReferenceException on the null check itself: a pulse.json that sets Bind to
    /// a JSON null deserialises Bind to exactly that. "[::1]]" checks that only one pair of
    /// brackets is stripped: a blanket Trim('[', ']') would also strip the extra trailing
    /// bracket, silently accepting a malformed value that only looks like a real one; stripping
    /// one pair leaves a lone bracket IPAddress.Parse correctly refuses.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("[::1]]")]
    public void ParseBind_InvalidValues_ThrowFormatException(string? bind)
    {
        Assert.Throws<FormatException>(() => MetricsHttpServer.ParseBind(bind));
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
