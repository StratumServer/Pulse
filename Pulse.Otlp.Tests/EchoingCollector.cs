using System.Net;
using System.Text;

namespace Pulse.Otlp.Tests;

/// <summary>An OTLP/HTTP collector that answers 401 and echoes the request's own Authorization
/// header straight into the JSON body, the way a real backend's own error response might
/// accidentally do. Exists to prove <see cref="ExportFailureLog"/> redacts a backend's own words,
/// not just whatever the SDK's diagnostics carry.</summary>
internal sealed class EchoingCollector : IDisposable
{
    private readonly HttpListener listener = new();

    public EchoingCollector(int port)
    {
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
            string authorization = context.Request.Headers["Authorization"] ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(
                "{\"error\":\"invalid credentials\",\"got\":\"" + authorization + "\"}");
            context.Response.StatusCode = 401;
            context.Response.StatusDescription = "Test";
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
    }
}
