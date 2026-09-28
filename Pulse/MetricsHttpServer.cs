using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vintagestory.API.Common;

namespace Pulse;

/// <summary>Serves the exposition text on its own thread, over a plain socket.</summary>
/// <remarks>A socket rather than HttpListener, whose two implementations disagree on what matters
/// here: on Windows it goes through HTTP.sys, which refuses a non-administrator the address
/// http://127.0.0.1:port/ until someone reserves it with netsh, and on Linux and macOS it answers
/// 404 to any Host header but the prefix's own, so http://localhost:9464/metrics fails there. A
/// socket bound to the configured address needs no reservation on any OS and answers whatever the
/// client called the host; what reaches it is decided by the bind alone.
///
/// The thread comes from <see cref="TyronThreadPool.CreateDedicatedThread"/>, not from
/// <c>api.Server.AddServerThread</c> (those are frozen for the whole of every autosave and are
/// joined for up to 60 s at shutdown, so a blocking accept would stall both) and not from
/// <c>Task.Run</c> (the engine caps the shared pool at 10 workers).</remarks>
internal sealed class MetricsHttpServer : IDisposable
{
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";
    private const long ErrorLogIntervalMs = 60_000;

    /// <summary>Larger than any request line and header block a scraper or a browser sends; past
    /// this, the request is treated the same as a malformed one rather than read indefinitely.</summary>
    private const int MaxHeadBytes = 8192;

    /// <summary>Bounds a single socket call: one Read, or the Write of the response. This alone
    /// does not bound a whole request, since a client that keeps a call alive by sending or
    /// accepting one byte just often enough never trips it; see <see cref="requestTimeoutMs"/>.</summary>
    private const int IoTimeoutMs = 5000;

    /// <summary>Accept-loop backoff, applied only to a failed AcceptTcpClient call: an empty
    /// sleep the first time, doubling on every consecutive failure, so a transient error costs
    /// nothing while a persistent one (the process out of file descriptors, say) does not spin a
    /// full core forever behind a log line that only fires once a minute.</summary>
    private const int InitialAcceptBackoffMs = 10;
    private const int MaxAcceptBackoffMs = 1000;

    private readonly TcpListener listener;
    private readonly Func<string> render;
    private readonly ILogger logger;
    private readonly Thread thread;
    private readonly int requestTimeoutMs;

    // Starts one interval in the past so the first failure is logged rather than swallowed.
    private long lastErrorLogMs = -ErrorLogIntervalMs;
    private volatile bool stopping;

    private int acceptFailures;

    /// <summary>How many times AcceptTcpClient has failed outright, backoff included; read back
    /// by the accept-loop-backoff test, nothing in production reads it.</summary>
    internal int AcceptFailures => acceptFailures;

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
        listener.Stop();
        if (thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    // ponytail: one connection at a time, an accepted client blocking the next until it finishes
    // or a timeout evicts it. A stalled scraper only ever delays the next scrape by that long.
    // Accept concurrently if that ever matters.
    [SuppressMessage(
        "Major Code Smell", "S2589:Boolean expressions should not be gratuitous",
        Justification = "stopping is volatile and Dispose sets it from another thread; the analysis assumes it cannot change inside the loop body.")]
    private void Serve()
    {
        int acceptBackoffMs = InitialAcceptBackoffMs;
        while (!stopping)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception e)
            {
                if (stopping)
                {
                    // Dispose stopped the listener out from under AcceptTcpClient. Normal
                    // shutdown.
                    continue;
                }

                // A one-off failure and a persistent one (the process out of file descriptors,
                // for instance) look identical from here, so both back off: nothing else stands
                // between a persistent failure and a full core spent re-failing as fast as
                // AcceptTcpClient can throw, invisible behind a log line rate-limited to once a
                // minute.
                LogOccasionally(e);
                acceptFailures++;
                Thread.Sleep(acceptBackoffMs);
                acceptBackoffMs = Math.Min(acceptBackoffMs * 2, MaxAcceptBackoffMs);
                continue;
            }

            acceptBackoffMs = InitialAcceptBackoffMs;
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = IoTimeoutMs;
                    client.SendTimeout = IoTimeoutMs;
                    Handle(client.GetStream(), Environment.TickCount64 + requestTimeoutMs);
                }
                catch (Exception e)
                {
                    // Otherwise Dispose evicted this connection's socket mid-request, normal
                    // shutdown, nothing to log.
                    if (!stopping)
                    {
                        LogOccasionally(e);
                    }
                }
            }
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
        else
        {
            body = Encoding.UTF8.GetBytes(render());
            status = "200 OK";
            headers = $"Content-Type: {ExpositionContentType}\r\n";
        }

        stream.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"));

        // HEAD carries every header a GET would, the body excepted; the client already has its
        // answer (the Content-Length above) without it.
        if (request?.Method != "HEAD")
        {
            stream.Write(body);
        }
    }

    /// <summary>Reads up to the first blank line and returns the request line; the bytes after it
    /// are discarded unread, since nothing here needs a header value or a body. Null when the
    /// connection closes before a blank line arrives, or the head is larger than
    /// <see cref="MaxHeadBytes"/>.</summary>
    /// <remarks>IoTimeoutMs alone bounds one Read call, not the request: a client that sends one
    /// byte every four seconds never trips a five second per-call timeout and can hold this loop
    /// for as long as it keeps doing that. <paramref name="deadline"/>, an
    /// <see cref="Environment.TickCount64"/> value taken once at accept, is the actual ceiling on
    /// the whole read; it is checked before every call that could otherwise block again.</remarks>
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
