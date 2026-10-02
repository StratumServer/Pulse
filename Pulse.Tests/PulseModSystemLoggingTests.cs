using Pulse.Tests.Fakes;
using Vintagestory.API.Server;
using Xunit;

namespace Pulse.Tests;

/// <summary>Everything Pulse writes to the server log goes through its own mod's logger, never the
/// server's. The engine marks each entry of a mod's logger with the mod id, so an admin reading the
/// log, or a harness listing the errors of a boot, can tell whose line it is.</summary>
/// <remarks>Drives <see cref="PulseModSystem.StartServerSide"/> against an <c>ICoreServerAPI</c>
/// built from <see cref="AutoFakeProxy"/>, with the engine's own mod container attached by
/// <see cref="LoadedMod"/>: the mark on an entry is the engine's, not a stand-in for it. The server
/// logger is one object that both the API and the mod's logger lead to, as on a live server, so an
/// entry written to it directly shows up here without the mark. The four entries this start writes
/// come from the mod itself, from the config upgrade and from attribution, the last two of which
/// take the logger as an argument.</remarks>
public class PulseModSystemLoggingTests
{
    [Fact]
    public void StartServerSide_WritesEveryEntry_ThroughTheModsOwnLogger()
    {
        (ICoreServerAPI api, AutoFakeProxy apiFake) = AutoFakeProxy.Create<ICoreServerAPI>();

        // Port 0 asks the OS for whatever is free: left unstubbed, the fake API hands back a
        // default config, whose port is the real Pulse's own 9464.
        apiFake.On("LoadModConfig", _ => new PulseConfig { Port = 0 });

        FakeLogger serverLogger = new();
        apiFake.On("get_Logger", _ => serverLogger);

        PulseModSystem system = new();
        LoadedMod.Attach(system, "pulse", serverLogger);
        try
        {
            system.StartServerSide(api);

            Assert.Contains(serverLogger.Entries, entry => entry.Message.Contains("Pulse serving metrics on"));
            Assert.All(serverLogger.Entries, entry => Assert.StartsWith("[pulse] ", entry.Message));
        }
        finally
        {
            // StartServerSide bound a real, ephemeral socket for the metrics endpoint.
            system.Dispose();
        }
    }
}
