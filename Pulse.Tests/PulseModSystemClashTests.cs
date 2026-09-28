using System.Diagnostics.Metrics;
using Pulse.Tests.Fakes;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Xunit;

namespace Pulse.Tests;

/// <summary>Item 2 of the 0.2.0 release review, the one probe the unit suite was still missing: a
/// name clash on <c>/pulse</c> must cost the command and nothing else. <see
/// cref="ConfigLoadTests.TryRun_ReturnsTheMessage_ForACommandNameAlreadyClaimedByAnotherMod"/>
/// only proves <c>TryRun</c> passes an exception's own message through; nothing in the suite
/// before this drove <see cref="PulseModSystem.StartServerSide"/> itself against a command the
/// engine's own <c>ChatCommandApi</c> had already claimed, so a regression that moved
/// <c>AttributionMetrics.ArmPriming</c> back into the constructor, ahead of the guarded
/// <c>RegisterCommands</c> call, would have left this suite green.</summary>
/// <remarks>Drives the real wiring: a live <c>ChatCommandApi</c> (Vintagestory.Common, engine
/// code, not a Pulse type) with <c>/pulse</c> pre-registered under different casing, an
/// <c>ICoreServerAPI</c> built from <see cref="AutoFakeProxy"/> rather than a live server, and a
/// real <see cref="FrameProfilerUtil"/> read through it. No decoy Atlas mod needed: the clash is
/// the engine's own <c>ChatCommandImpl.WithName</c> exception
/// (<see cref="ConfigLoadTests.TryRun_ReturnsTheMessage_ForACommandNameAlreadyClaimedByAnotherMod"/>
/// confirms its exact message against VintagestoryLib), not a stand-in for it.</remarks>
public class PulseModSystemClashTests
{
    [Fact]
    public void StartServerSide_SurvivesAPulseNameClash_InTheDocumentedOrder_AndLeavesTheProfilerOff()
    {
        List<string> order = [];
        List<Action<float>> tickListeners = [];
        Action? runGamePhase = null;
        List<object?[]?> warnings = [];
        FrameProfilerUtil profiler = new("clash-test");

        (ICoreServerAPI api, AutoFakeProxy apiFake) = AutoFakeProxy.Create<ICoreServerAPI>();

        // The engine's own command table, with "pulse" already claimed by another mod under
        // different casing: ichatCommands is keyed OrdinalIgnoreCase (Vintagestory.Common.
        // ChatCommandApi), the same clash ConfigLoadTests's own probe documents the message for.
        ChatCommandApi chatCommands = new(api);
        chatCommands.Create("PULSE");
        apiFake.On("get_ChatCommands", _ => chatCommands);

        (IServerEventAPI eventApi, AutoFakeProxy eventFake) = AutoFakeProxy.Create<IServerEventAPI>();
        eventFake.On("RegisterGameTickListener", args =>
        {
            order.Add("tick-listener");
            tickListeners.Add((Action<float>)args![0]!);
            return (long)tickListeners.Count;
        });
        eventFake.On("ServerRunPhase", args =>
        {
            if ((EnumServerRunPhase)args![0]! == EnumServerRunPhase.RunGame)
            {
                order.Add("run-phase");
                runGamePhase = (Action)args[1]!;
            }

            return null;
        });
        apiFake.On("get_Event", _ => eventApi);

        (IServerWorldAccessor world, AutoFakeProxy worldFake) = AutoFakeProxy.Create<IServerWorldAccessor>();
        worldFake.On("get_FrameProfiler", _ => profiler);
        apiFake.On("get_World", _ => world);

        (ILogger logger, AutoFakeProxy loggerFake) = AutoFakeProxy.Create<ILogger>();
        loggerFake.On("Warning", args =>
        {
            warnings.Add(args);
            return null;
        });
        apiFake.On("get_Logger", _ => logger);

        Exception? thrown = Record.Exception(() => new PulseModSystem().StartServerSide(api));

        Assert.Null(thrown);

        // The clash costs its own warning and nothing upstream of it: two other warnings fire
        // first (the config file and the engine probe, both unreachable through this fake API and
        // both already unrelated, pre-existing degrade paths of their own), but exactly one names
        // the clash, and none of the three is an exception escaping StartServerSide.
        string[] rendered = warnings
            .Select(w => string.Format((string)w![0]!, (object?[])w[1]!))
            .ToArray();
        string clashWarning = Assert.Single(rendered, message => message.Contains("could not register /pulse"));
        Assert.Contains("Command with such name already exists", clashWarning);

        // The order the review's own DispatchProxy probe recorded: the main tick listener first,
        // then priming armed for RunGame, then the chunk listener. ArmPriming must run after the
        // tick listener that is the only thing ever able to turn a primed profiler back off; the
        // clash above must not have disturbed that.
        Assert.Equal(["tick-listener", "run-phase", "tick-listener"], order);

        // PrimeFrameProfiler runs before the tick loop and turns the profiler on unconditionally;
        // only AttributionMetrics.Tick, riding the first listener registered above, ever turns it
        // back off, and only once it has seen a completed tick to prove the profiler is safe to
        // read. The clash must not have cost that: attribution is off by default, so the first
        // primed tick is Pulse's only chance to undo priming for the rest of the run.
        runGamePhase!();
        Assert.True(profiler.Enabled, "PrimeFrameProfiler must still have run despite the clash");

        profiler.PrevRootEntry = new ProfileEntryRange { ElapsedTicks = 1000 };
        tickListeners[0](0.02f);

        Assert.False(profiler.Enabled);
    }
}
