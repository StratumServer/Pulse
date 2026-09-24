using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Pulse.Scenarios;

/// <summary>Talks to the mod exactly the way Prometheus does: over HTTP, on loopback.</summary>
internal static class Scrape
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long a scrape keeps retrying a connection error before giving up.
    /// <c>MetricsHttpServer.Start</c> binds the listener synchronously from <c>StartServerSide</c>,
    /// before the engine starts ticking, so the socket already exists by a scenario's first tick;
    /// this is not covering an unbound listener. What it covers is a connection attempt losing a
    /// scheduling race on a slow or loaded machine, the same jitter that shows up elsewhere as a
    /// slow tick, applied here rather than in each scenario so every scrape is safe from it.</summary>
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
            catch (HttpRequestException e)
                when (e.HttpRequestError == HttpRequestError.ConnectionError && DateTime.UtcNow < deadline)
            {
                // A connection-level failure only, not a reset or a truncated response mid-stream:
                // those point at a real regression in the server, not a slow start, and should
                // fail immediately rather than be retried into a false pass.
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
