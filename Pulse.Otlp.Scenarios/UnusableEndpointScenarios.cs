using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>A pulse-otlp.json Endpoint that cannot be resolved at all: a non-http(s) scheme here,
/// carrying both userinfo and a query string, the two shapes a signed-URL or basic-auth-in-the-URL
/// backend could realistically produce. The engine's own error line for this must name the problem
/// without ever repeating the value back, since userinfo and a query string are exactly the shapes
/// a raw echo would leak.</summary>
[AtlasDataFiles("data/unusableendpoint", TargetPath = "ModConfig")]
public class UnusableEndpointScenarios : AtlasScenarioBase
{
    private const string ErrorMarker = "is not an absolute http or https URL";
    private const string BootCompleteMarker = "systems on Server:";

    [AtlasScenario]
    public async Task Server_Runs_WithExportingOff_AndNeverEchoesTheEndpoint()
    {
        string log = await ReadServerLog();

        Assert.Contains(ErrorMarker, log);
        Assert.Contains("Endpoint", log);
        Assert.Contains("pulse-otlp.json", log);

        // The two things a raw echo of the configured value would have put in the log: its
        // userinfo and its query string.
        Assert.DoesNotContain("s3cret", log);
        Assert.DoesNotContain("alsosecret", log);
        Assert.DoesNotContain("otlp.example.com", log);

        // The mod only ever logs this line once it has actually built a provider, so its absence
        // after boot has finished is proof exporting never started, not a timing guess.
        Assert.DoesNotContain("Pulse OTLP exporting", log);
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(BootCompleteMarker); attempt++)
        {
            // The engine's logger writes on its own thread; pump the world instead of sleeping.
            await World.Ticks(10);
            text = ReadShared(path);
        }

        return text;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
