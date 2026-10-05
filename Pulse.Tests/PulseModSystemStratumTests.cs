using Pulse.Tests.Fakes;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Xunit;

namespace Pulse.Tests;

/// <summary>The Stratum timings as <see cref="PulseModSystem.StartServerSide"/> wires them, against an
/// <c>ICoreServerAPI</c> built from <see cref="AutoFakeProxy"/>: a server whose API assembly has no
/// Stratum type in it, which is every vanilla one.</summary>
public class PulseModSystemStratumTests
{
    /// <summary>The block on, on a server that is not Stratum: the one line of the failure table, written
    /// through the mod's own logger, and a tick listener that carries on ticking without a word more or
    /// a family served. Everything else about the start is what it was.</summary>
    [Fact]
    public void StartServerSide_ExplainsTheBlock_OnceAndInTheModsOwnVoice_WhenThisIsNotAStratumServer()
    {
        (ICoreServerAPI api, AutoFakeProxy apiFake) = AutoFakeProxy.Create<ICoreServerAPI>();
        apiFake.On(
            "LoadModConfig",
            _ => new PulseConfig { Port = 0, StratumTimings = new StratumTimingsConfig { Enabled = true, BurstTicks = 3, IntervalSeconds = 1 } });

        FakeLogger serverLogger = new();
        apiFake.On("get_Logger", _ => serverLogger);

        List<Action<float>> tickListeners = [];
        (IServerEventAPI eventApi, AutoFakeProxy eventFake) = AutoFakeProxy.Create<IServerEventAPI>();
        eventFake.On("RegisterGameTickListener", args =>
        {
            tickListeners.Add((Action<float>)args![0]!);
            return (long)tickListeners.Count;
        });
        apiFake.On("get_Event", _ => eventApi);

        PulseModSystem system = new();
        LoadedMod.Attach(system, "pulse", serverLogger);
        try
        {
            system.StartServerSide(api);

            (EnumLogType type, string message) = Assert.Single(
                serverLogger.Entries, entry => entry.Message.Contains("need a Stratum server"));
            Assert.Equal(EnumLogType.Notification, type);
            Assert.StartsWith("[pulse] ", message);

            // Long enough for a burst of three ticks to have come and gone, a second apart, had the
            // feature run: it is the first listener registered that carries the tick.
            for (int tick = 0; tick < 20; tick++)
            {
                tickListeners[0](1f);
            }

            Assert.Single(serverLogger.Entries, entry => entry.Message.Contains("Stratum"));
        }
        finally
        {
            system.Dispose();
        }
    }
}
