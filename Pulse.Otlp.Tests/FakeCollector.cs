using System.Net;
using System.Text;

namespace Pulse.Otlp.Tests;

/// <summary>An OTLP/HTTP collector reduced to what these tests need: it accepts an export and
/// answers however <paramref name="respond"/> says for that request's one-based number. A status
/// of 0 means "accept the request and never answer", which is how the timeout test reaches a real
/// HttpClient timeout without a real network partition.</summary>
internal sealed class FakeCollector : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Func<int, (int Status, string Body)> respond;
    private int count;

    public FakeCollector(int port, Func<int, (int Status, string Body)> respond)
    {
        this.respond = respond;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task.Run(Accept);
    }

    public void Dispose() => listener.Close();

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

            await context.Request.InputStream.CopyToAsync(Stream.Null);
            (int status, string body) = respond(Interlocked.Increment(ref count));
            if (status == 0)
            {
                // Never answer: the connection is left open until the client's own timeout gives
                // up on it, rather than being refused or reset.
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.StatusDescription = "Test";
            context.Response.ContentType = "application/json";

            // Without an explicit length the response goes out chunked, which carries no
            // Content-Length header at all; the exporter's own response size check compares
            // against that header, so a chunked body would never be seen as too large.
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
    }
}
