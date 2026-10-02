using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Pulse.Otlp.Scenarios;

/// <summary>An OTLP/HTTP collector reduced to what a test needs: it accepts the POST, answers the
/// way a collector answers, and keeps every request whole.</summary>
internal sealed class FakeCollector : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly HttpStatusCode statusCode;
    private readonly string? responseBody;

    /// <summary>Every export received, in arrival order. Written by the listener thread and read
    /// by the scenario, hence the concurrent queue: the records in it are immutable.</summary>
    private readonly ConcurrentQueue<Export> received = new();

    public FakeCollector(int port, HttpStatusCode statusCode = HttpStatusCode.OK, string? responseBody = null)
    {
        this.statusCode = statusCode;
        this.responseBody = responseBody;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task.Run(Accept);
    }

    /// <summary>The first export received, or null while none has arrived.</summary>
    public Export? First => received.FirstOrDefault();

    /// <summary>The first export received for which <paramref name="match"/> holds, or null while
    /// none has.</summary>
    public Export? FirstWhere(Func<Export, bool> match) => received.FirstOrDefault(match);

    public void Dispose()
    {
        // Close, not Stop: Close unblocks the pending GetContext with an exception the loop treats
        // as its exit signal.
        listener.Close();
    }

    private async Task Accept()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            using MemoryStream body = new();
            await context.Request.InputStream.CopyToAsync(body);

            received.Enqueue(new Export(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? string.Empty,
                context.Request.ContentType ?? string.Empty,
                context.Request.Headers["x-scope-orgid"] ?? string.Empty,
                body.ToArray()));

            // A real collector answers 200 with an empty ExportMetricsServiceResponse, which on the
            // wire is a protobuf message with no fields set, which is zero bytes. A rejecting
            // collector without an explicit body gets none either: most scenarios configuring one
            // only need the status code itself to reach the exporter.
            context.Response.StatusCode = (int)statusCode;
            if (responseBody is { Length: > 0 } text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                context.Response.ContentType = "application/x-protobuf";
            }

            context.Response.Close();
        }
    }

    internal sealed record Export(
        string Method, string Path, string ContentType, string OrgId, byte[] Body);
}
