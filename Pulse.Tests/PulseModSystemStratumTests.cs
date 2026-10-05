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

    /// <summary>The table of owners is built from the mod list, which is two plain reads, but whatever
    /// they do, it costs the features that read the table and nothing else. On a server that is not
    /// Stratum the Stratum timings have no use for it and never ask, so attribution, which has always
    /// been guarded against what its constructor reads, loses its line to a mod list that cannot be
    /// read and the Stratum timings add none of their own, with the block off or on. The endpoint is
    /// still served.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartServerSide_LosesOnlyAttribution_WhenTheModLoaderThrows_OnAServerThatIsNotStratum(bool block)
    {
        (ICoreServerAPI api, AutoFakeProxy apiFake) = AutoFakeProxy.Create<ICoreServerAPI>();
        apiFake.On(
            "LoadModConfig", _ => new PulseConfig { Port = 0, StratumTimings = new StratumTimingsConfig { Enabled = block } });
        apiFake.On("get_ModLoader", _ => throw new InvalidOperationException("the mod loader is gone"));

        FakeLogger serverLogger = new();
        apiFake.On("get_Logger", _ => serverLogger);

        PulseModSystem system = new();
        LoadedMod.Attach(system, "pulse", serverLogger);
        try
        {
            Exception? thrown = Record.Exception(() => system.StartServerSide(api));

            Assert.Null(thrown);

            // Attribution's line, as it was before the Stratum timings existed, and no other warning
            // about the mod list.
            Assert.Single(
                serverLogger.Entries,
                entry => entry.Type == EnumLogType.Warning
                    && entry.Message.Contains("could not start per-mod tick attribution (the mod loader is gone)"));
            Assert.DoesNotContain(
                serverLogger.Entries, entry => entry.Message.Contains("could not start the Stratum entity timings"));
            Assert.DoesNotContain(
                serverLogger.Entries, entry => entry.Message.Contains("the mod loader is gone") && entry.Message.Contains("Stratum"));

            // What the Stratum timings do say is the one notification the block earns on such a server,
            // and only when it is on.
            Assert.Equal(
                block ? 1 : 0, serverLogger.Entries.Count(entry => entry.Message.Contains("need a Stratum server")));
            Assert.Contains(serverLogger.Entries, entry => entry.Message.Contains("Pulse serving metrics on"));
        }
        finally
        {
            system.Dispose();
        }
    }
}
