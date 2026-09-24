using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Pulse.Scenarios;

/// <summary>Talks to the mod exactly the way Prometheus does: over HTTP, on loopback.</summary>
internal static class Scrape
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long a scrape keeps retrying a refused connection before giving up. The
    /// mod's HTTP listener binds asynchronously during boot, so a scenario's first request can
    /// arrive before it is up, especially on a loaded machine; this is the bounded deadline for
    /// that race, applied here rather than in each scenario so every scrape is safe from it.</summary>
    private static readonly TimeSpan ConnectRetryDeadline = TimeSpan.FromSeconds(3);

    public static async Task<HttpResponseMessage> Get(int port, string path)
    {
        string url = $"http://127.0.0.1:{port}{path}";
        DateTime deadline = DateTime.UtcNow + ConnectRetryDeadline;
        while (true)
        {
            try
            {
                return await Client.GetAsync(url);
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                // Connection refused, most likely: the listener has not bound yet. Retry rather
                // than fail, up to the deadline; a genuinely dead endpoint still fails, just after
                // it, with the same exception a caller already handles.
                await Task.Delay(50);
            }
        }
    }

    public static async Task<string> Metrics(int port)
    {
        using HttpResponseMessage response = await Get(port, "/metrics");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"/metrics returned {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Reads one sample line out of an exposition body by its exact name, labels
    /// included when the series has any.</summary>
    public static double Value(string exposition, string name)
    {
        foreach (string line in exposition.Split('\n'))
        {
            if (!line.StartsWith(name + " ", StringComparison.Ordinal))
            {
                continue;
            }

            return double.Parse(line[(name.Length + 1)..], CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"'{name}' is not in the exposition:\n{exposition}");
    }
}
