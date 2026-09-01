using System.Net;
using System.Net.Sockets;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>A metrics endpoint must never be able to take a game server down. This class holds
/// the configured port hostage from before the server boots, so the mod's bind fails.</summary>
[AtlasDataFiles("data/bindfailure/pulse.json", TargetPath = "ModConfig")]
public class BindFailureScenarios : AtlasScenarioBase, IDisposable
{
    private const int Port = 39466;
    private const string BindFailureMarker = "Pulse could not bind";

    private readonly TcpListener squatter;

    public BindFailureScenarios()
    {
        // xUnit builds the test class before Atlas boots the host for its first scenario, so the
        // port is already taken by the time the mod's StartServerSide runs.
        squatter = new TcpListener(IPAddress.Loopback, Port);
        squatter.Start();
    }

    public void Dispose()
    {
        squatter.Stop();
        GC.SuppressFinalize(this);
    }

    [AtlasScenario]
    public async Task Server_Survives_APortConflict_AndSaysSo()
    {
        Assert.True(squatter.Server.IsBound, "the test never got the port it meant to squat on");

        // The world still boots, ticks and mutates: that is what "never crash the server" means.
        BlockPos pos = World.Spawn.Offset(1, 1, 0);
        World.SetBlock("game:chest-east", pos);
        await World.Ticks(30);
        Assert.Equal("game:chest-east", World.BlockAt(pos).Code.ToString());

        Assert.Contains(BindFailureMarker, await ReadServerLog());
    }

    private async Task<string> ReadServerLog()
    {
        string path = Path.Combine(GamePaths.Logs, "server-main.log");
        string text = ReadShared(path);
        for (int attempt = 0; attempt < 10 && !text.Contains(BindFailureMarker); attempt++)
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
