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

    /// <summary>Bounds how long a connected client can take to finish sending its request and how
    /// long it can take to accept the response, so a stalled client delays the next scrape by at
    /// most this, never the game.</summary>
    private const int IoTimeoutMs = 5000;

    private readonly TcpListener listener;
    private readonly Func<string> render;
    private readonly ILogger logger;
    private readonly Thread thread;

    // Starts one interval in the past so the first failure is logged rather than swallowed.
    private long lastErrorLogMs = -ErrorLogIntervalMs;
    private volatile bool stopping;

    public MetricsHttpServer(string bind, int port, Func<string> render, ILogger logger)
    {
        listener = new TcpListener(ParseBind(bind), port);
        this.render = render;
        this.logger = logger;
        thread = TyronThreadPool.CreateDedicatedThread(Serve, "pulse-metrics");
    }

    /// <summary>Maps a configured Bind address to the interface, or set of interfaces, to listen
    /// on. "0.0.0.0", "*" and "+" mean every IPv4 interface; "::" means every IPv6 interface.
    /// "localhost" resolves to the IPv4 loopback rather than going through DNS: .NET tries ::1
    /// first on Linux, and every common client that finds ::1 closed falls back to 127.0.0.1 on
    /// its own, so binding the address the client falls back to is what actually gets scraped.
    /// Anything else is a literal address, brackets stripped the way a URL would carry them
    /// around an IPv6 one.</summary>
    internal static IPAddress ParseBind(string bind) => bind switch
    {
        "0.0.0.0" or "*" or "+" => IPAddress.Any,
        "::" => IPAddress.IPv6Any,
        _ when bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) => IPAddress.Loopback,
        _ => IPAddress.Parse(bind.Trim('[', ']')),
    };

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
    // or the I/O timeout evicts it. A stalled scraper only ever delays the next scrape by that
    // long. Accept concurrently if that ever matters.
    private void Serve()
    {
        while (!stopping)
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;
                Handle(client.GetStream());
            }
            catch (Exception e) when (!stopping)
            {
                LogOccasionally(e);
            }
            catch
            {
                // Dispose stopped the listener out from under AcceptTcpClient, or evicted a
                // stalled client's socket. Normal shutdown either way.
            }
        }
    }

    private void Handle(NetworkStream stream)
    {
        Request? request = ParseRequestLine(ReadRequestLine(stream));
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
    /// <see cref="MaxHeadBytes"/>; the caller answers both the same way it answers a malformed
    /// request.</summary>
    private static string? ReadRequestLine(NetworkStream stream)
    {
        byte[] buffer = new byte[MaxHeadBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                return null;
            }

            length += read;
            string head = Encoding.ASCII.GetString(buffer, 0, length);
            int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
            }
        }

        return null;
    }

    /// <summary>Method and path off the request line, the query string dropped and the path
    /// matched exactly. No header, Host included, is ever consulted: what reached this socket is
    /// enough on its own.</summary>
    private static Request? ParseRequestLine(string? line)
    {
        string[] parts = line?.Split(' ') ?? [];
        return parts.Length == 3 && parts[1].StartsWith('/')
            ? new Request(parts[0], parts[1].Split('?')[0])
            : null;
    }

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
