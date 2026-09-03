using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Pulse.Scenarios;

/// <summary>Talks to the mod exactly the way Prometheus does: over HTTP, on loopback.</summary>
internal static class Scrape
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<HttpResponseMessage> Get(int port, string path)
        => await Client.GetAsync($"http://127.0.0.1:{port}{path}");

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
