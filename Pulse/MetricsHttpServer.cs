using System.Net;
using System.Text;
using Vintagestory.API.Common;

namespace Pulse;

/// <summary>Serves the exposition text on its own thread.</summary>
/// <remarks>The thread comes from <see cref="TyronThreadPool.CreateDedicatedThread"/>, not from
/// <c>api.Server.AddServerThread</c> (those are frozen for the whole of every autosave and are
/// joined for up to 60 s at shutdown, so a blocking accept would stall both) and not from
/// <c>Task.Run</c> (the engine caps the shared pool at 10 workers).</remarks>
internal sealed class MetricsHttpServer : IDisposable
{
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";
    private const long ErrorLogIntervalMs = 60_000;

    private readonly HttpListener listener = new();
    private readonly Func<string> render;
    private readonly ILogger logger;
    private readonly Thread thread;

    // Starts one interval in the past so the first failure is logged rather than swallowed.
    private long lastErrorLogMs = -ErrorLogIntervalMs;
    private volatile bool stopping;

    public MetricsHttpServer(string bind, int port, Func<string> render, ILogger logger)
    {
        this.render = render;
        this.logger = logger;
        listener.Prefixes.Add($"http://{bind}:{port}/");
        thread = TyronThreadPool.CreateDedicatedThread(Serve, "pulse-metrics");
    }

    /// <summary>Binds the socket and starts serving. Throws when the port is unavailable; the
    /// caller logs that and keeps the game server running without an endpoint.</summary>
    public void Start()
    {
        listener.Start();
        thread.Start();
    }

    public void Dispose()
    {
        stopping = true;
        listener.Close();
        if (thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Serve()
    {
        while (!stopping && listener.IsListening)
        {
            try
            {
                Handle(listener.GetContext());
            }
            catch (Exception e) when (!stopping)
            {
                LogOccasionally(e);
            }
            catch
            {
                // Dispose closed the listener out from under GetContext. Normal shutdown.
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        using HttpListenerResponse response = context.Response;
        if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/metrics")
        {
            byte[] body = Encoding.UTF8.GetBytes(render());
            response.StatusCode = 200;
            response.ContentType = ExpositionContentType;
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }
        else
        {
            response.StatusCode = 404;
        }
    }

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
