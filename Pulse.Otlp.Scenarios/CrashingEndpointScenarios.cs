using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>An Endpoint shape TryResolveEndpoint's own check lets through as a syntactically valid
/// absolute http URL, but that neither it nor the OpenTelemetry SDK's own option validation can
/// actually use: an empty user name with a password, which both UriBuilder (TryResolveEndpoint's
/// own, for http/protobuf) and the SDK's own (inside Build(), for grpc) refuse with a
/// UriFormatException. Not a regression, 0.1.0 crashed on this the same way, but
/// UriFormatException is a FormatException, and ModLoader.TryRunModPhase rethrows one instead of
/// absorbing it like every other exception, so left unguarded this takes the whole server down at
/// boot rather than merely failing this one mod.</summary>
[AtlasDataFiles("data/crashingendpoint-http", TargetPath = "ModConfig")]
public class CrashingEndpointHttpScenarios : AtlasScenarioBase
{
    private const string BootCompleteMarker = "systems on Server:";

    [AtlasScenario]
    public async Task Server_Boots_WithExportingOff_AndNeverEchoesTheCredential()
    {
        string log = await ReadServerLog();

        Assert.Contains(BootCompleteMarker, log);
        Assert.Contains("Pulse OTLP could not start exporting", log);
        Assert.Contains("UriFormatException", log);
        Assert.DoesNotContain("s3cretpassword", log);
        Assert.DoesNotContain("Pulse OTLP exporting", log);

        // What ModLoader logs for a mod phase that threw and was not a FormatException: proof
        // this configuration no longer even reaches ModLoader's own catch, since StartServerSide
        // itself now returns normally.
        Assert.DoesNotContain("An exception was thrown when trying to start the mod", log);
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

/// <summary>The same proof as <see cref="CrashingEndpointHttpScenarios"/>, for grpc: the crash for
/// this endpoint shape does not happen in TryResolveEndpoint (grpc leaves the endpoint bare and
/// never runs it through UriBuilder there), it happens inside the SDK's own Build() a few lines
/// later, so the guard has to cover both call sites, not just the one TryResolveEndpoint owns.
/// Its own fixture: the protocol is read once at boot, the same reason every other scenario pair
/// here keeps http and grpc apart.</summary>
[AtlasDataFiles("data/crashingendpoint-grpc", TargetPath = "ModConfig")]
public class CrashingEndpointGrpcScenarios : AtlasScenarioBase
{
    private const string BootCompleteMarker = "systems on Server:";

    [AtlasScenario]
    public async Task Server_Boots_WithExportingOff_AndNeverEchoesTheCredential()
    {
        string log = await ReadServerLog();

        Assert.Contains(BootCompleteMarker, log);
        Assert.Contains("Pulse OTLP could not start exporting", log);
        Assert.Contains("UriFormatException", log);
        Assert.DoesNotContain("s3cretpassword", log);
        Assert.DoesNotContain("Pulse OTLP exporting", log);
        Assert.DoesNotContain("An exception was thrown when trying to start the mod", log);
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(BootCompleteMarker); attempt++)
        {
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
