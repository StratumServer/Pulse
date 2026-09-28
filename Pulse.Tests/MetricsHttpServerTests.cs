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

    /// <summary>Regression check for the single-threaded design this once had: a handful of
    /// connections that never send anything used to occupy the one thread that also accepted and
    /// served every scrape, so any of them sitting open starved every scrape behind it. Each
    /// connection now gets its own thread, so idle ones below the concurrency cap have no effect
    /// on a scrape at all.</summary>
    [Fact]
    public async Task IdleConnectionsBelowTheCap_DoNotBlockConcurrentScrapes()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        List<TcpClient> idle = [];
        try
        {
            for (int i = 0; i < 5; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                idle.Add(client);
            }

            // Lets the accept thread actually pick up all five idle connections before scraping.
            await Task.Delay(200);

            using HttpClient http = new();
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/metrics");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
                $"10 scrapes behind 5 idle connections took {watch.Elapsed}");
        }
        finally
        {
            foreach (TcpClient client in idle)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Fills every concurrency slot with a connection that will not send anything, then
    /// scrapes: that seventeenth connection has nowhere to go until one of the sixteen idle ones
    /// is evicted by its own (short, for this test) deadline and frees a slot. The lower bound
    /// confirms it actually waited for that, rather than the cap having no effect at all.</summary>
    [Fact]
    public async Task ScrapeQueuedBehindAFullCap_SucceedsOnceAShortDeadlineFreesASlot()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger(), requestTimeoutMs: 1000);
        server.Start();

        List<TcpClient> idle = [];
        try
        {
            for (int i = 0; i < 16; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                idle.Add(client);
            }

            // Lets the accept thread actually pick up all sixteen before the seventeenth connects.
            // Well short of their 1 s deadline, so most of that second is still ahead of the
            // scrape below, leaving a wide margin either side of its expected wait.
            await Task.Delay(100);

            using HttpClient http = new();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            Stopwatch watch = Stopwatch.StartNew();
            using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/metrics", cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(400),
                $"the scrape succeeded in {watch.Elapsed}, suspiciously fast for one actually queued behind a full cap");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"queued scrape took {watch.Elapsed}");
        }
        finally
        {
            foreach (TcpClient client in idle)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Reading the request is not the only socket call the deadline has to bound: a
    /// client that stops reading its response makes the server's own Write block once the
    /// send buffer fills, and that Write needs the same clamp Read gets, or it falls back to the
    /// full IoTimeoutMs backstop regardless of how little of the deadline is actually left by
    /// then.</summary>
    [Fact]
    public async Task WriteTimeout_IsClampedToTheDeadline_NotTheFullBackstop()
    {
        int port = FreePort();
        // Comfortably larger than the OS send and receive buffers combined, so writing it blocks
        // once the client below stops reading.
        string bigBody = new string('x', 4 * 1024 * 1024);
        using MetricsHttpServer server = new("127.0.0.1", port, () => bigBody, new FakeLogger(), requestTimeoutMs: 1000);
        server.Start();

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /metrics HTTP/1.1\r\nHost: x\r\n\r\n"));

        // Never reads any of the response: the server's write of several megabytes eventually
        // blocks on a full send buffer, giving the write timeout something real to bound.
        Stopwatch watch = Stopwatch.StartNew();
        while (server.GetActiveHandlerThreads().Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(50);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
            $"the handler thread was still writing a stalled response after {watch.Elapsed}, past the 1 s deadline it should be clamped to");
    }

    /// <summary>If a failed accept ever kept the concurrency slot it had acquired instead of
    /// releasing it, the semaphore would run permanently short by one for every failure, and
    /// enough of them would exhaust it for good; a handful is enough to prove each one gives its
    /// slot back.</summary>
    [Fact]
    public async Task AcceptFailure_ReleasesItsConcurrencySlot()
    {
        int port = FreePort();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        FieldInfo listenerField = typeof(MetricsHttpServer).GetField("listener", BindingFlags.NonPublic | BindingFlags.Instance)!;
        TcpListener listener = (TcpListener)listenerField.GetValue(server)!;
        listener.Stop();

        while (server.AcceptFailures < 3)
        {
            await Task.Delay(10);
        }

        FieldInfo slotsField = typeof(MetricsHttpServer).GetField("connectionSlots", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SemaphoreSlim slots = (SemaphoreSlim)slotsField.GetValue(server)!;
        Assert.Equal(MetricsHttpServer.MaxConcurrentConnections, slots.CurrentCount);

        server.Dispose();
    }

    /// <summary>Sets stopping directly, bypassing Dispose entirely, so this checks exactly the
    /// branch in Handle: a request that reaches the render-or-not decision while shutdown is
    /// already under way must never call render, whatever else Dispose itself does or does not
    /// close.</summary>
    [Fact]
    public async Task Handle_Returns503_AndNeverCallsRender_WhenStoppingIsAlreadySet()
    {
        int port = FreePort();
        bool rendered = false;
        using MetricsHttpServer server = new("127.0.0.1", port, () =>
        {
            rendered = true;
            return "x";
        }, new FakeLogger());
        server.Start();

        FieldInfo stoppingField = typeof(MetricsHttpServer).GetField("stopping", BindingFlags.NonPublic | BindingFlags.Instance)!;
        stoppingField.SetValue(server, true);

        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        Assert.False(rendered, "render was called even though stopping was already set");
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

    /// <summary>The deadline is only checked between reads, so a Read already blocked when it
    /// passes has to be bounded some other way, or it simply waits out its own full per-call
    /// timeout regardless of how little of the deadline was left when it started. One byte, then
    /// silence, puts the very next Read in exactly that position: already waiting when the 1 s
    /// deadline arrives. It has to come back in about a second, not five.</summary>
    [Fact]
    public async Task OneByteThenSilence_IsDroppedAtTheDeadline_NotAfterAFullPerCallTimeout()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger(), requestTimeoutMs: 1000);
        server.Start();

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("G"));

        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(4));
        byte[] buffer = new byte[16];
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, cts.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.Equal(0, read);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"took {watch.Elapsed} to drop a connection that sent one byte and then went silent");
    }

    /// <summary>ClampToDeadline floors the timeout it hands a Read at 1 ms, never 0 or negative,
    /// so a client sending data faster than that floor keeps every single Read completing well
    /// inside it, the same gap the per-call timeout alone always had, just at a far smaller
    /// interval. Only the deadline checked once per loop, independently of what any Read call
    /// does, closes it. Paced at one byte every 0.5 ms, comfortably faster than that 1 ms floor,
    /// but slow enough that filling the 8 KB head this way would take about 4 s, well past the
    /// 1 s deadline that has to end this instead; a write loop with no pacing at all fills that
    /// head in well under a second regardless of the deadline, which proves nothing.</summary>
    [Fact]
    public async Task FastDribblingClient_IsDroppedAtTheDeadline_NotOnlyWhenTheHeadFills()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger(), requestTimeoutMs: 1000);
        server.Start();

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();

        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

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

        byte[] one = Encoding.ASCII.GetBytes("G");
        int bytesSent = 0;
        // Task.Delay cannot reach sub-millisecond precision on most platforms, so this paces
        // itself against a Stopwatch instead, busy-waiting for each byte's own absolute target
        // time rather than sleeping a fixed amount per iteration, which would drift.
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

            bytesSent++;
            double targetMs = bytesSent * 0.5;
            while (!waitForDrop.IsCompleted && watch.Elapsed.TotalMilliseconds < targetMs)
            {
            }
        }

        Assert.True(await waitForDrop, "the server never dropped the fast-dribbling client");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"took {watch.Elapsed} to drop a fast-dribbling client, past the 1 s deadline it should be bounded by");
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

    /// <summary>Before the sockets of in-flight connections were tracked and force-closed,
    /// Dispose's own 2 s Join was the only thing bounding it: it returned having merely run out of
    /// patience, with the handler thread still alive and still blocked on the idle client's
    /// socket. This pins Dispose to well under that budget, confirms the specific handler thread
    /// for this connection is actually gone afterwards, and confirms render, which reads state a
    /// caller may dispose immediately after Dispose returns, is never called once it has.</summary>
    [Fact]
    public async Task Dispose_WhileAClientIsConnectedButIdle_ReturnsPromptly_StopsItsHandlerThread_AndNeverRendersAfterwards()
    {
        int port = FreePort();
        bool disposed = false;
        bool renderedAfterDispose = false;
        MetricsHttpServer server = new("127.0.0.1", port, () =>
        {
            if (disposed)
            {
                renderedAfterDispose = true;
            }

            return "x";
        }, new FakeLogger());
        server.Start();

        using TcpClient stalled = new();
        await stalled.ConnectAsync(IPAddress.Loopback, port);

        // Lets the accept thread hand this connection to its own handler thread and block on
        // reading its request, so Dispose races against that thread rather than against
        // AcceptTcpClient or the semaphore wait.
        await Task.Delay(200);

        Thread handlerThread = Assert.Single(server.GetActiveHandlerThreads());
        Assert.True(handlerThread.IsAlive);

        Stopwatch watch = Stopwatch.StartNew();
        server.Dispose();
        disposed = true;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {watch.Elapsed}");
        Assert.False(handlerThread.IsAlive, "the handler thread was still alive once Dispose returned");

        // Gives any straggling render call, if the guard above did not catch it, the time it
        // would need to run.
        await Task.Delay(300);
        Assert.False(renderedAfterDispose, "render ran after Dispose returned");
    }

    /// <summary>Before connectionSlots and stoppingSource stopped being disposed, a handler
    /// thread still running its render callback when Dispose's own join timed out would call
    /// Release on an already-disposed SemaphoreSlim from its finally block once that render
    /// finally returned: an unhandled exception on a background thread, which crashes the whole
    /// process, not just this class. A 1.5 s render, well past Dispose's 1 s join budget, puts a
    /// handler thread exactly there. Reaching the end of this test at all is most of the proof:
    /// the old bug took the whole test host down with it, not just this one test.</summary>
    [Fact]
    public async Task Dispose_DuringASlowRender_TheProcessSurvives_AndTheHandlerEndsCleanly()
    {
        int port = FreePort();
        using ManualResetEventSlim renderStarted = new();
        MetricsHttpServer server = new("127.0.0.1", port, () =>
        {
            renderStarted.Set();
            Thread.Sleep(1500);
            return "x";
        }, new FakeLogger());
        server.Start();

        using HttpClient client = new();
        Task<HttpResponseMessage> scrape = client.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.True(renderStarted.Wait(TimeSpan.FromSeconds(2)), "the request never reached render");

        server.Dispose();

        // Well past the 1.5 s render, so its handler thread has certainly finished (or, on the
        // old code, certainly crashed the process) by now.
        await Task.Delay(1700);
        Assert.Empty(server.GetActiveHandlerThreads());

        try
        {
            using HttpResponseMessage response = await scrape;
        }
        catch
        {
            // The connection was torn down from under this request once Dispose closed its
            // socket; this test only cares that nothing crashed and that the handler thread
            // cleaned up after itself, not what this half-abandoned response looks like.
        }
    }

    [Fact]
    public void Dispose_CalledTwice_IsSafe()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();

        server.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task Dispose_CalledConcurrently_IsSafe()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();

        await Task.WhenAll(
            Task.Run(server.Dispose),
            Task.Run(server.Dispose));
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

    /// <summary>The integration test above only proves the accept loop sleeps at all; it stays
    /// green whether the delay doubles, stays flat, or grows without bound, since all three still
    /// keep the failure count low over half a second. Exercised directly, with no timing involved,
    /// these are the specific behaviours that test cannot tell apart.</summary>
    [Fact]
    public void AcceptBackoff_DoublesEachFailure_UpToACap()
    {
        MetricsHttpServer.AcceptBackoff backoff = new();

        Assert.Equal(MetricsHttpServer.AcceptBackoff.InitialMs, backoff.NextMs());
        Assert.Equal(MetricsHttpServer.AcceptBackoff.InitialMs * 2, backoff.NextMs());
        Assert.Equal(MetricsHttpServer.AcceptBackoff.InitialMs * 4, backoff.NextMs());

        int delay = 0;
        for (int i = 0; i < 20; i++)
        {
            delay = backoff.NextMs();
        }

        Assert.Equal(MetricsHttpServer.AcceptBackoff.MaxMs, delay);
    }

    [Fact]
    public void AcceptBackoff_Reset_ReturnsToTheInitialDelay()
    {
        MetricsHttpServer.AcceptBackoff backoff = new();
        for (int i = 0; i < 5; i++)
        {
            backoff.NextMs();
        }

        backoff.Reset();

        Assert.Equal(MetricsHttpServer.AcceptBackoff.InitialMs, backoff.NextMs());
    }

    /// <summary>The two tests above prove AcceptBackoff's own arithmetic; neither proves Serve
    /// actually calls Reset on its one instance after a successful accept. Advances the server's
    /// real backoff by reflection before ever starting it, so the very first accept, a normal
    /// scrape once the server is listening, is the one that has to reset it back down.</summary>
    [Fact]
    public async Task AcceptBackoff_ResetsAfterASuccessfulAccept_InTheRealServeLoop()
    {
        int port = FreePort();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());

        FieldInfo backoffField = typeof(MetricsHttpServer).GetField("acceptBackoff", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MetricsHttpServer.AcceptBackoff backoff = (MetricsHttpServer.AcceptBackoff)backoffField.GetValue(server)!;
        backoff.NextMs();
        backoff.NextMs();
        Assert.True(backoff.CurrentMs > MetricsHttpServer.AcceptBackoff.InitialMs);

        server.Start();
        try
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Equal(MetricsHttpServer.AcceptBackoff.InitialMs, backoff.CurrentMs);
        }
        finally
        {
            server.Dispose();
        }
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
