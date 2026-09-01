using System.Net;

namespace Pulse.Otlp.Scenarios;

/// <summary>An OTLP/HTTP collector reduced to what a test needs: it accepts the POST, answers the
/// way a collector answers, and keeps the first request whole.</summary>
internal sealed class FakeCollector : IDisposable
{
    private readonly HttpListener listener = new();

    /// <summary>The first export received, or null while none has arrived. Written by the listener
    /// thread and read by the scenario, hence the volatile: the record itself is immutable.</summary>
    private volatile Export? first;

    public FakeCollector(int port)
    {
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task.Run(Accept);
    }

    public Export? First => first;

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

            first ??= new Export(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? string.Empty,
                context.Request.ContentType ?? string.Empty,
                context.Request.Headers["x-scope-orgid"] ?? string.Empty,
                body.ToArray());

            // A real collector answers 200 with an empty ExportMetricsServiceResponse, which on the
            // wire is a protobuf message with no fields set, which is zero bytes.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.Close();
        }
    }

    internal sealed record Export(
        string Method, string Path, string ContentType, string OrgId, byte[] Body);
}
