using System.Net;
using System.Net.Sockets;
using System.Text;
using Vintagestory.API.Common;

namespace Pulse;

/// <summary>Serves the exposition text over a plain socket: one dedicated thread accepts, and
/// each accepted connection is handled on its own short-lived thread, up to a small concurrency
/// cap.</summary>
/// <remarks>A socket rather than HttpListener, whose two implementations disagree on what matters
/// here: on Windows it goes through HTTP.sys, which refuses a non-administrator the address
/// http://127.0.0.1:port/ until someone reserves it with netsh, and on Linux and macOS it answers
/// 404 to any Host header but the prefix's own, so http://localhost:9464/metrics fails there. A
/// socket bound to the configured address needs no reservation on any OS and answers whatever the
/// client called the host; what reaches it is decided by the bind alone.
///
/// The accept thread comes from <see cref="TyronThreadPool.CreateDedicatedThread"/>, not from
/// <c>api.Server.AddServerThread</c> (those are frozen for the whole of every autosave and are
/// joined for up to 60 s at shutdown, so a blocking accept would stall both) and not from
/// <c>Task.Run</c> (the engine caps the shared pool at 10 workers). Each connection's own thread
/// is a plain background <see cref="Thread"/>: short-lived and bounded in number by
/// <see cref="MaxConcurrentConnections"/>, it carries none of the reasons the accept thread needs
/// that treatment.</remarks>
internal sealed class MetricsHttpServer : IDisposable
{
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";
    private const long ErrorLogIntervalMs = 60_000;

    /// <summary>Larger than any request line and header block a scraper or a browser sends; past
    /// this, the request is treated the same as a malformed one rather than read indefinitely.</summary>
    private const int MaxHeadBytes = 8192;

    /// <summary>Bounds a single socket call: one Read, or the Write of the response, when the
    /// overall deadline still has this much or more left on it. Never the actual wait past the
    /// deadline itself; <see cref="ClampToDeadline"/> shortens it as the deadline closes in.</summary>
    private const int IoTimeoutMs = 5000;

    // ponytail: 16 concurrent connections, each of them stalled, still clears on its own within
    // one request deadline; raise this, or move to an async accept loop, if a real deployment
    // ever needs more scrapers or panels hitting this at once than that.
    private const int MaxConcurrentConnections = 16;

    /// <summary>Accept-loop backoff, applied only to a failed AcceptTcpClient call: an empty
    /// sleep the first time, doubling on every consecutive failure, so a transient error costs
    /// nothing while a persistent one (the process out of file descriptors, say) does not spin a
    /// full core forever behind a log line that only fires once a minute.</summary>
    internal sealed class AcceptBackoff
    {
        internal const int InitialMs = 10;
        internal const int MaxMs = 1000;

        private int nextMs = InitialMs;

        /// <summary>The delay to sleep for this failure; advances for the next one.</summary>
        internal int NextMs()
        {
            int delay = nextMs;
            nextMs = Math.Min(nextMs * 2, MaxMs);
            return delay;
        }

        internal void Reset() => nextMs = InitialMs;
    }

    private sealed record ActiveConnection(Thread Thread, TcpClient Client);

    private readonly TcpListener listener;
    private readonly Func<string> render;
    private readonly ILogger logger;
    private readonly Thread thread;
    private readonly int requestTimeoutMs;
    private readonly SemaphoreSlim connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly CancellationTokenSource stoppingSource = new();
    private readonly AcceptBackoff acceptBackoff = new();
    private readonly List<ActiveConnection> activeConnections = [];
    private readonly Lock activeConnectionsGate = new();

    // Starts one interval in the past so the first failure is logged rather than swallowed.
    private long lastErrorLogMs = -ErrorLogIntervalMs;
    private volatile bool stopping;

    private int acceptFailures;

    /// <summary>How many times AcceptTcpClient has failed outright, backoff included; read back
    /// by the accept-loop-backoff test, nothing in production reads it.</summary>
    internal int AcceptFailures => acceptFailures;

    /// <summary>The handler thread of every connection currently being served; read back by a
    /// test that needs to confirm one has actually stopped, nothing in production reads it.</summary>
    internal IReadOnlyList<Thread> ActiveHandlerThreads
    {
        get
        {
            lock (activeConnectionsGate)
            {
                return activeConnections.Select(c => c.Thread).ToList();
            }
        }
    }

    public MetricsHttpServer(string bind, int port, Func<string> render, ILogger logger)
        : this(bind, port, render, logger, IoTimeoutMs)
    {
    }

    /// <summary>Test seam: production always uses IoTimeoutMs as the overall request deadline
    /// too, through the constructor above. A test shortens it to prove the deadline logic without
    /// waiting on the real one.</summary>
    internal MetricsHttpServer(string bind, int port, Func<string> render, ILogger logger, int requestTimeoutMs)
    {
        listener = new TcpListener(ParseBind(bind), port);
        this.render = render;
        this.logger = logger;
        this.requestTimeoutMs = requestTimeoutMs;
        thread = TyronThreadPool.CreateDedicatedThread(Serve, "pulse-metrics");
    }

    /// <summary>Maps a configured Bind address to the interface, or set of interfaces, to listen
    /// on. "0.0.0.0", "*" and "+" mean every IPv4 interface; "::" means every IPv6 interface.
    /// "localhost" resolves to the IPv4 loopback rather than going through DNS: .NET tries ::1
    /// first on Linux, and every common client that finds ::1 closed falls back to 127.0.0.1 on
    /// its own, so binding the address the client falls back to is what actually gets scraped.
    /// Surrounding whitespace is trimmed first, since the value comes straight out of a config
    /// file. Anything else is a literal address with one pair of brackets stripped, the way a URL
    /// would carry them around an IPv6 one; a blank value (null included, which a config file
    /// that sets Bind to a JSON null parses to) reaches IPAddress.Parse the same as any other
    /// unparseable text, rather than throwing on the null check itself.</summary>
    internal static IPAddress ParseBind(string? bind)
    {
        string trimmed = (bind ?? string.Empty).Trim();
        return trimmed switch
        {
            "0.0.0.0" or "*" or "+" => IPAddress.Any,
            "::" => IPAddress.IPv6Any,
            _ when trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) => IPAddress.Loopback,
            _ => IPAddress.Parse(StripOneBracketPair(trimmed)),
        };
    }

    private static string StripOneBracketPair(string value) =>
        value.Length >= 2 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;

    /// <summary>Binds the socket and starts serving. Throws when the port is unavailable or the
    /// bind address does not parse; the caller logs that and keeps the game server running
    /// without an endpoint.</summary>
    public void Start()
    {
        listener.Start();
        thread.Start();
    }

    public void Dispose()
    {
        stopping = true;
        stoppingSource.Cancel();
        listener.Stop();

        List<ActiveConnection> connections;
        lock (activeConnectionsGate)
        {
            connections = [.. activeConnections];
        }

        // Closing each socket directly makes whatever Read or Write its handler thread is
        // blocked in fail at once; the joins below then only have to wait for that thread to
        // unwind, not for any I/O to time out on its own.
        foreach (ActiveConnection connection in connections)
        {
            connection.Client.Close();
        }

        long joinDeadline = Environment.TickCount64 + 1000;
        foreach (ActiveConnection connection in connections)
        {
            int remaining = (int)Math.Clamp(joinDeadline - Environment.TickCount64, 0, 1000);
            connection.Thread.Join(remaining);
        }

        if (thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        stoppingSource.Dispose();
        connectionSlots.Dispose();
    }

    // Each accepted connection gets its own short-lived thread, up to MaxConcurrentConnections at
    // once, so one stalled or merely idle connection no longer blocks every other scrape behind
    // it. A connection past the cap waits in the kernel's own accept backlog instead of being
    // accepted and then dropped, since the accept thread does not call AcceptTcpClient again
    // until a slot frees up.
    private void Serve()
    {
        while (!stopping)
        {
            try
            {
                connectionSlots.Wait(stoppingSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception e)
            {
                connectionSlots.Release();
                if (stopping)
                {
                    break;
                }

                // A one-off failure and a persistent one (the process out of file descriptors,
                // for instance) look identical from here, so both back off: nothing else stands
                // between a persistent failure and a full core spent re-failing as fast as
                // AcceptTcpClient can throw, invisible behind a log line rate-limited to once a
                // minute.
                LogOccasionally(e);
                acceptFailures++;
                Thread.Sleep(acceptBackoff.NextMs());
                continue;
            }

            acceptBackoff.Reset();
            StartHandler(client);
        }
    }

    private void StartHandler(TcpClient client)
    {
        long deadline = Environment.TickCount64 + requestTimeoutMs;
        Thread handler = new(() => HandleConnection(client, deadline))
        {
            IsBackground = true,
            Name = "pulse-metrics-request",
        };

        lock (activeConnectionsGate)
        {
            activeConnections.Add(new ActiveConnection(handler, client));
        }

        handler.Start();
    }

    private void HandleConnection(TcpClient client, long deadline)
    {
        try
        {
            using (client)
            {
                try
                {
                    Handle(client.GetStream(), deadline);
                }
                catch (Exception e)
                {
                    // Otherwise Dispose closed this connection's socket mid-request, normal
                    // shutdown, nothing to log.
                    if (!stopping)
                    {
                        LogOccasionally(e);
                    }
                }
            }
        }
        finally
        {
            lock (activeConnectionsGate)
            {
                activeConnections.RemoveAll(c => c.Client == client);
            }

            connectionSlots.Release();
        }
    }

    private void Handle(NetworkStream stream, long deadline)
    {
        Request? request = ParseRequestLine(ReadRequestLine(stream, deadline));
        byte[] body = [];
        string status;
        string headers = "";

        if (request is null)
        {
            status = "400 Bad Request";
        }
        else if (request.Path != "/metrics")
        {
            status = "404 Not Found";
        }
        else if (request.Method is not ("GET" or "HEAD"))
        {
            status = "405 Method Not Allowed";
        }
        else if (stopping)
        {
            // Dispose may already be tearing down whatever render reads from (the aggregator and
            // the meter belong to the caller, not this class); never call into that state once
            // shutdown has started, even for a request that got this far before it did.
            status = "503 Service Unavailable";
        }
        else
        {
            body = Encoding.UTF8.GetBytes(render());
            status = "200 OK";
            headers = $"Content-Type: {ExpositionContentType}\r\n";
        }

        // One deadline covers the whole connection, reading and writing both: shortens the
        // backstop timeout to whatever is actually left of it, rather than handing a full
        // IoTimeoutMs to a write that starts most of the way through it.
        stream.WriteTimeout = ClampToDeadline(deadline);
        stream.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"));

        // HEAD carries every header a GET would, the body excepted; the client already has its
        // answer (the Content-Length above) without it.
        if (request?.Method != "HEAD")
        {
            stream.Write(body);
        }
    }

    /// <summary>What is left of <paramref name="deadline"/>, floored at 1 ms so a Read or Write
    /// this close to it still gets one real attempt rather than none, and capped at
    /// <see cref="IoTimeoutMs"/> so the deadline can only ever shorten a socket call, never
    /// lengthen it past the usual backstop.</summary>
    private static int ClampToDeadline(long deadline) =>
        (int)Math.Clamp(deadline - Environment.TickCount64, 1, IoTimeoutMs);

    /// <summary>Reads up to the first blank line and returns the request line; the bytes after it
    /// are discarded unread, since nothing here needs a header value or a body. Null when the
    /// connection closes before a blank line arrives, or the head is larger than
    /// <see cref="MaxHeadBytes"/>.</summary>
    /// <remarks>A flat per-call timeout alone bounds one Read, not the request: a client sending
    /// one byte every four seconds never trips a five second timeout and can hold this loop for
    /// as long as it keeps doing that. <paramref name="deadline"/>, an
    /// <see cref="Environment.TickCount64"/> value taken once at accept, is the actual ceiling:
    /// checked before every Read that could otherwise block again, and, through
    /// <see cref="ClampToDeadline"/>, applied to the Read itself so a call already blocked when
    /// the deadline passes does not wait out a full five seconds of its own on top of it.</remarks>
    private static string? ReadRequestLine(NetworkStream stream, long deadline)
    {
        byte[] buffer = new byte[MaxHeadBytes];
        int length = 0;
        int searchedTo = 0;
        while (length < buffer.Length)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("the request did not complete within the overall deadline");
            }

            stream.ReadTimeout = ClampToDeadline(deadline);
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                return null;
            }

            length += read;

            // Only the bytes this call just added need scanning, a 3 byte lookback included so a
            // terminator split across two reads is not missed. Re-decoding and re-scanning the
            // whole buffer on every read, as this once did, turned a head arriving one byte at a
            // time into an O(n^2) string allocation: about 67 MB of garbage for one ordinary 8 KB
            // head trickled in that way.
            if (HasBlankLine(buffer, Math.Max(0, searchedTo - 3), length))
            {
                string head = Encoding.ASCII.GetString(buffer, 0, length);
                return head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
            }

            searchedTo = length;
        }

        return null;
    }

    /// <summary>Whether "\r\n\r\n" occurs anywhere in buffer[from, length), a plain byte scan with
    /// no allocation of its own.</summary>
    private static bool HasBlankLine(byte[] buffer, int from, int length)
    {
        for (int i = from; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Method and path off the request line, the query string dropped and the path
    /// matched exactly. No header, Host included, is ever consulted: what reached this socket is
    /// enough on its own. The target is ordinarily origin-form ("/metrics"), but RFC 9112
    /// requires an origin server to also accept absolute-form ("http://host/metrics"), the form a
    /// request written for a proxy carries; <see cref="AbsoluteFormPath"/> is that one path taken
    /// out.</summary>
    private static Request? ParseRequestLine(string? line)
    {
        string[] parts = line?.Split(' ') ?? [];
        if (parts.Length != 3)
        {
            return null;
        }

        string target = parts[1];
        string? path = target.StartsWith('/') ? target.Split('?')[0] : AbsoluteFormPath(target);
        return path is null ? null : new Request(parts[0], path);
    }

    private static string? AbsoluteFormPath(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute) ? absolute.AbsolutePath : null;

    private sealed record Request(string Method, string Path);

    // ponytail: one line per minute at most, the rest dropped on the floor. Enough to notice a
    // broken endpoint, not enough for a port scanner to fill server-main.log. Count the drops if
    // anyone ever needs to know how many there were.
    private void LogOccasionally(Exception e)
    {
        long now = Environment.TickCount64;
        if (now - lastErrorLogMs < ErrorLogIntervalMs)
        {
            return;
        }

        lastErrorLogMs = now;
        logger.Warning("Pulse metrics request failed: {0}", e.Message);
    }
}
