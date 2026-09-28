using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Read back by reflection, the same way ServeThreadAlive reads the serve thread:
    /// the field is private, and this is what proves the "one interval in the past" comment is
    /// actually what the field starts at, deterministically, rather than something only
    /// observable by waiting out a real interval.</summary>
    [Fact]
    public void Constructor_SeedsTheErrorLogTimestamp_OneIntervalInThePast()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());

        FieldInfo field = typeof(MetricsHttpServer).GetField("lastErrorLogMs", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.Equal(-60_000L, (long)field.GetValue(server)!);
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

    /// <summary>The test above goes through HttpClient, which already knows a HEAD response
    /// carries no body per RFC 9110 and simply never reads one, whatever the server actually put
    /// on the wire after the headers. This reads the raw bytes instead, so a body written despite
    /// the HEAD guard cannot hide behind the client's own leniency.</summary>
    [Fact]
    public async Task Head_Writes_NoBodyBytes_OnTheWireItself()
    {
        const string body = "# HELP x X.\n# TYPE x counter\nx 1\n";
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => body, new FakeLogger());
        server.Start();

        string response = await RawRequestAsync(port, "HEAD /metrics HTTP/1.1\r\nHost: x\r\n\r\n");

        int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0, $"no header terminator in: {response}");
        Assert.Equal(string.Empty, response[(headerEnd + 4)..]);
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
        // gives up on it rather than growing the buffer to find one. Because of that, part of
        // this request is still unread and sitting in the connection's receive buffer at the
        // point the server writes its 400 and closes: the same condition
        // ExpiredDeadline_StopsTheConnection_BeforeItReadsEvenABufferedRequest documents as
        // resetting the connection instead of closing it gracefully, there because the deadline
        // is already expired, here because MaxHeadBytes was reached first. On Linux this still
        // reads back the 400 every time; on Windows CI it reset the connection before the 400
        // ever reached this end, every run. Either outcome proves the same thing: the oversized
        // head was rejected, not accepted as a 200.
        string oversized = "GET /metrics HTTP/1.1\r\nX-Pad: " + new string('A', 9000) + "\r\n\r\n";
        string response;
        try
        {
            response = await RawRequestAsync(port, oversized);
        }
        catch (IOException)
        {
            response = string.Empty;
        }

        Assert.True(response.Length == 0 || response.StartsWith("HTTP/1.1 400"),
            $"expected either a reset connection or an actual 400 response, got: {response}");

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

    /// <summary>The blank-line scan keeps a 3 byte lookback specifically so a terminator split
    /// across two reads is not missed; this splits one even tighter, inside the request line's
    /// own line ending rather than at the final blank line, landing a lone \r with no \n yet at
    /// the very end of the first chunk.</summary>
    [Fact]
    public async Task RequestSplit_WithALoneCarriageReturn_BeforeItsLineFeedArrives_IsStillParsed()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        string response = await RawRequestAsync(
            port, "GET /metrics HTTP/1.1\r", "\nHost: x\r\n\r\n");

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

    /// <summary>A connection closed before it ever sent a byte makes the very first Read return 0
    /// (a graceful close, not an error). ReadRequestLine has to recognise that and return null
    /// promptly; the alternative is a tight loop of zero-byte reads that never advances length
    /// and never reaches MaxHeadBytes either, pinning the handler thread and its concurrency slot
    /// forever.</summary>
    [Fact]
    public async Task ConnectionClosedWithNothingSent_EndsTheHandlerThread_Promptly()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            // Lets the accept thread hand this off to its own handler thread first.
            await Task.Delay(100);
        } // Disposing closes the connection with nothing sent: the handler's Read returns 0.

        Stopwatch watch = Stopwatch.StartNew();
        while (server.GetActiveHandlerThreads().Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(20);
        }

        Assert.Empty(server.GetActiveHandlerThreads());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"the handler thread was still alive {watch.Elapsed} after the connection closed with nothing sent");
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

    /// <summary>The deterministic companion to the test above, whose sub-millisecond pacing Nagle
    /// or a loaded machine can break: with the deadline already past at accept and a whole request
    /// waiting in the socket, only the explicit check before each Read stops the server from
    /// reading it, since a clamped read timeout still waits its 1 ms floor and a buffered request
    /// beats that every time.</summary>
    [Fact]
    public async Task ExpiredDeadline_StopsTheConnection_BeforeItReadsEvenABufferedRequest()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger(), requestTimeoutMs: -1000);
        server.Start();

        string response;
        try
        {
            response = await RawRequestAsync(port, "GET /metrics HTTP/1.1\r\nHost: x\r\n\r\n");
        }
        catch (IOException)
        {
            // Closing a socket with the request still unread in its receive buffer resets it.
            response = string.Empty;
        }

        Assert.DoesNotContain("200 OK", response);
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

        (EnumLogType Type, string Message) entry = Assert.Single(logger.Entries, e => e.Type == EnumLogType.Warning);
        Assert.Equal("Pulse metrics request failed: boom", entry.Message);

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

    /// <summary>Every slot taken means the accept loop itself is blocked in
    /// connectionSlots.Wait(stoppingSource.Token), not in AcceptTcpClient: listener.Stop() alone
    /// cannot reach that wait, only cancelling its token can. Reuses the same sixteen-connection
    /// setup ScrapeQueuedBehindAFullCap_... already pays for elsewhere in this file.</summary>
    [Fact]
    public async Task Dispose_WhileTheServeThreadIsBlockedOnAFullConnectionCap_ReturnsPromptly()
    {
        int port = FreePort();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        List<TcpClient> idle = [];
        try
        {
            for (int i = 0; i < MetricsHttpServer.MaxConcurrentConnections; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                idle.Add(client);
            }

            // Lets the accept thread take all sixteen and block waiting for a slot on the next one.
            await Task.Delay(200);

            Stopwatch watch = Stopwatch.StartNew();
            server.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1),
                $"Dispose took {watch.Elapsed} with the serve thread blocked on a full connection cap");
        }
        finally
        {
            foreach (TcpClient client in idle)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Thread names and background status matter for the same reason the accept thread's
    /// own name does: a hung or CPU-heavy handler is something an operator has to be able to spot
    /// in a thread dump, and a foreground thread would keep the whole process alive against its
    /// own shutdown.</summary>
    [Fact]
    public async Task ConnectionHandlerThread_IsNamed_AndRunsInTheBackground()
    {
        int port = FreePort();
        using MetricsHttpServer server = new("127.0.0.1", port, () => "x", new FakeLogger());
        server.Start();

        FieldInfo acceptThreadField = typeof(MetricsHttpServer).GetField("thread", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Thread acceptThread = (Thread)acceptThreadField.GetValue(server)!;
        Assert.Equal("pulse-metrics", acceptThread.Name);

        using TcpClient stalled = new();
        await stalled.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(200);

        Thread handlerThread = Assert.Single(server.GetActiveHandlerThreads());
        Assert.Equal("pulse-metrics-request", handlerThread.Name);
        Assert.True(handlerThread.IsBackground);
    }

    /// <summary>Before connectionSlots and stoppingSource stopped being disposed, a handler
    /// thread still running its render callback when Dispose's own join timed out would call
    /// Release on an already-disposed SemaphoreSlim from its finally block once that render
    /// finally returned: an unhandled exception on a background thread, which crashes the whole
    /// process, not just this class. A 1.5 s render, well past Dispose's 1 s join budget, puts a
    /// handler thread exactly there. Reaching the end of this test at all is most of the proof:
    /// the old bug took the whole test host down with it, not just this one test.</summary>
    [Fact]
    [SuppressMessage(
        "Major Code Smell", "S2925:Thread.Sleep should not be used in tests",
        Justification = "This is the render callback, synchronous by contract like the real one, not the test body; it has to actually block the handler thread for a real duration to reproduce a race against Dispose's own timed join, which an awaited delay could not do from inside a synchronous Func<string>.")]
    public async Task Dispose_DuringASlowRender_TheProcessSurvives_AndTheHandlerEndsCleanly()
    {
        int port = FreePort();
        using ManualResetEventSlim renderStarted = new();
        FakeLogger logger = new();
        MetricsHttpServer server = new("127.0.0.1", port, () =>
        {
            renderStarted.Set();
            Thread.Sleep(1500);
            return "x";
        }, logger);
        server.Start();

        using HttpClient client = new();
        Task<HttpResponseMessage> scrape = client.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.True(renderStarted.Wait(TimeSpan.FromSeconds(2)), "the request never reached render");

        // The render (1.5 s) outlasts JoinStarted's own 1 s budget for it, so Dispose has to
        // wait close to that whole second before giving up on it, not return as soon as the
        // sockets are closed: proof that connections are actually joined, not merely closed.
        Stopwatch watch = Stopwatch.StartNew();
        server.Dispose();
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(700),
            $"Dispose returned in {watch.Elapsed}, suspiciously fast for one that should wait up to a second for a slow handler");

        // Well past the 1.5 s render, so its handler thread has certainly finished (or, on the
        // old code, certainly crashed the process) by now.
        await Task.Delay(1700);
        Assert.Empty(server.GetActiveHandlerThreads());

        // The thread-level catch-alls would swallow a Release on a disposed semaphore, so the
        // process surviving is not enough on its own: nothing may have reached them either.
        Assert.Empty(logger.Entries);

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

    /// <summary>A connection is registered just before its handler thread starts, so the snapshot
    /// Dispose takes can hold a thread that has not started yet. Joining one throws
    /// ThreadStateException, which escaped Dispose and skipped the rest of PulseModSystem's own
    /// cleanup; a reviewer's probe hit it in 81 of 400 Disposes under a busy accept loop.</summary>
    [Fact]
    public void JoinStarted_SkipsAThreadThatHasNotStartedYet()
    {
        Thread notStarted = new(() => { });

        Exception? thrown = Record.Exception(() => MetricsHttpServer.JoinStarted([notStarted], 100));

        Assert.Null(thrown);
    }

    /// <summary>The integration-level Dispose tests only prove JoinStarted eventually returns;
    /// they cannot tell a real join from one that gave up on arrival. Driven from another thread
    /// so the test body can observe it still blocked partway through, the same technique the
    /// timing tests above use for a still-open connection.</summary>
    [Fact]
    public async Task JoinStarted_WaitsForARunningThread_WithinItsBudget()
    {
        using ManualResetEventSlim canFinish = new();
        Thread worker = new(() => canFinish.Wait()) { IsBackground = true };
        worker.Start();

        try
        {
            Task joinTask = Task.Run(() => MetricsHttpServer.JoinStarted([worker], 2000));
            await Task.Delay(150);
            Assert.False(joinTask.IsCompleted, "JoinStarted returned before the thread it was joining finished");

            canFinish.Set();
            Task completed = await Task.WhenAny(joinTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.True(completed == joinTask, "JoinStarted did not return once the thread finished");
            Assert.False(worker.IsAlive);
        }
        finally
        {
            canFinish.Set();
            worker.Join(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>All within one shared budget, per JoinStarted's own doc comment: a thread that
    /// outlives the budget must not give every thread after it a fresh one, or a handful of stuck
    /// handlers could each cost Dispose their own full budget in turn.</summary>
    [Fact]
    public async Task JoinStarted_Shares_OneBudget_AcrossSeveralThreads_RatherThanRestartingItForEach()
    {
        using ManualResetEventSlim firstCanFinish = new();
        using ManualResetEventSlim secondCanFinish = new();
        Thread first = new(() => firstCanFinish.Wait()) { IsBackground = true };
        Thread second = new(() => secondCanFinish.Wait()) { IsBackground = true };
        first.Start();
        second.Start();

        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            Task joinTask = Task.Run(() => MetricsHttpServer.JoinStarted([first, second], 2000));
            await Task.Delay(1400);
            firstCanFinish.Set(); // first finishes now; a fresh budget would let second run ~2000 ms more

            await joinTask;

            // Shared budget: ~600 ms left for second once first is done, total call time ~2000 ms.
            // A fresh budget per thread would instead cost another ~2000 ms on top of the 1400
            // already spent (~3400 ms). The 2700 ms ceiling sits roughly halfway between the two,
            // a ~700 ms margin either side well past ordinary scheduling jitter or the extra
            // overhead a coverage-instrumented run adds.
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(2700),
                $"JoinStarted took {watch.Elapsed}: the shared budget was not honoured");
            Assert.True(second.IsAlive, "second never got a chance to keep waiting under its own fresh budget");
        }
        finally
        {
            secondCanFinish.Set();
            second.Join(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>ReadRequestLine's scan keeps a 3 byte lookback rather than re-scanning the whole
    /// buffer; the loop bound this protects has to stop at length, not wander past it into
    /// whatever the rest of an 8 KB buffer happens to hold. Reflected directly: forcing length to
    /// sit exactly at the buffer's own end through a real request would depend on how the OS
    /// happens to chunk the bytes, which this sidesteps entirely.</summary>
    [Fact]
    public void HasBlankLine_DoesNotReadPastTheReceivedBytes_WhenTheyFillTheWholeBuffer()
    {
        byte[] buffer = new byte[8];
        MethodInfo method = typeof(MetricsHttpServer).GetMethod("HasBlankLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? result = method.Invoke(null, [buffer, 0, buffer.Length]);

        Assert.False((bool)result!);
    }

    [Fact]
    public void ParseRequestLine_ReturnsNull_ForANullLine_RatherThanThrowing()
    {
        MethodInfo method = typeof(MetricsHttpServer).GetMethod("ParseRequestLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? result = method.Invoke(null, [null]);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRequestLine_ReturnsNull_ForALineWithTooFewParts_RatherThanThrowing()
    {
        MethodInfo method = typeof(MetricsHttpServer).GetMethod("ParseRequestLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? result = method.Invoke(null, ["GARBAGE"]);

        Assert.Null(result);
    }

    /// <summary>A target that starts with neither '/' (origin-form) nor anything
    /// Uri.TryCreate(..., Absolute, ...) accepts has to fall through both branches to null, not
    /// reach AbsoluteFormPath's caller with one to build a Request out of.</summary>
    [Fact]
    public void ParseRequestLine_ReturnsNull_ForATargetThatIsNeitherOriginFormNorAnAbsoluteUri()
    {
        MethodInfo method = typeof(MetricsHttpServer).GetMethod("ParseRequestLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? result = method.Invoke(null, ["GET not-a-url HTTP/1.1"]);

        Assert.Null(result);
    }

    [Fact]
    public void Dispose_CalledTwice_IsSafe()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();

        server.Dispose();
        Exception? secondCall = Record.Exception(server.Dispose);

        Assert.Null(secondCall);
    }

    /// <summary>Dispose_CalledTwice_IsSafe above proves a second call does not throw, but running
    /// the real cleanup twice would not throw either (closing a socket or joining a thread twice
    /// over is itself harmless), so it cannot tell a skipped second call from one that quietly
    /// redid the work. Pre-marking disposed by reflection isolates exactly the guard: a real
    /// second caller never reaches this method with stopping still false.</summary>
    [Fact]
    public void Dispose_WhenAlreadyMarkedDisposed_SkipsCleanup()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();
        FieldInfo disposedField = typeof(MetricsHttpServer).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo stoppingField = typeof(MetricsHttpServer).GetField("stopping", BindingFlags.NonPublic | BindingFlags.Instance)!;
        disposedField.SetValue(server, 1);

        server.Dispose();

        Assert.False((bool)stoppingField.GetValue(server)!, "the guard let a call through even though disposed was already set");

        // Undoes the simulation so the real cleanup still runs and the process is left clean.
        disposedField.SetValue(server, 0);
        server.Dispose();
    }

    [Fact]
    public async Task Dispose_CalledConcurrently_IsSafe()
    {
        MetricsHttpServer server = new("127.0.0.1", FreePort(), () => "x", new FakeLogger());
        server.Start();

        Exception? thrown = await Record.ExceptionAsync(() => Task.WhenAll(
            Task.Run(server.Dispose),
            Task.Run(server.Dispose)));

        Assert.Null(thrown);
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
        FakeLogger logger = new();
        MetricsHttpServer server = new("127.0.0.1", port, () => "x", logger);
        server.Start();

        FieldInfo listenerField = typeof(MetricsHttpServer).GetField("listener", BindingFlags.NonPublic | BindingFlags.Instance)!;
        TcpListener listener = (TcpListener)listenerField.GetValue(server)!;
        listener.Stop();

        await Task.Delay(500);

        Assert.InRange(server.AcceptFailures, 1, 50);
        // Rate-limited to one line, but there has to be at least the one: proves the failure was
        // actually logged, not merely counted.
        Assert.NotEmpty(logger.Entries);

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
    // IPv4 is never bracketed, so this can only pass by way of StripOneBracketPair itself: unlike
    // "[::1]", an IPv4 literal has no bracketed form of its own for IPAddress.Parse to accept
    // regardless of whether the brackets were stripped first.
    [InlineData("[10.0.0.1]", "10.0.0.1")]
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
