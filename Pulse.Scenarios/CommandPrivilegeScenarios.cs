using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary><c>/pulse</c> is gated on <c>controlserver</c> at the top level (see
/// AttributionMetrics.Server.RegisterCommands): the engine's own RequiresPrivilege precondition,
/// not a check any Pulse handler runs itself. The other command scenarios never exercise it: they
/// all run through the console caller, which carries every privilege by construction and so never
/// sees a refusal. A joined player is the only caller that can actually lack one.</summary>
[AtlasDataFiles("data/privilege/pulse.json", TargetPath = "ModConfig")]
public class CommandPrivilegeScenarios : AtlasScenarioBase
{
    [AtlasScenario]
    public async Task AttributionStatus_Refuses_APlayerWithoutControlServer_AndAccepts_OnceRestored()
    {
        ITestPlayer player = await World.JoinPlayer("pulse-caller");
        player.Client.Clear(); // Drop the engine's own join-time welcome notification.

        // A joined test player rides the same dummy-socket path real singleplayer does, so the
        // engine hands it the highest-privilege role regardless of the server's configured
        // default: it starts out holding controlserver, not lacking it. Downgrading to suplayer
        // (chat only) opens a real gap for the refusal to prove itself against.
        player.Player.SetRole("suplayer");

        CommandResult refused = await player.ExecuteCommand("/pulse attribution status");

        Assert.False(refused.Ok);
        Assert.Equal("noprivilege", refused.Raw.ErrorCode);
        Assert.Equal("Sorry, you don't have the privilege to use this command", refused.Message);

        // RequiresPrivilege answers through the returned CommandResult alone: ExecuteCommand skips
        // the network round trip Say goes through, so unlike a real typed chat command, this
        // refusal never reaches the player's own chat.
        Assert.Empty(player.Client.Chat());

        // The downgrade is not sticky: granting, denying or revoking a privilege re-fetches this
        // player's data and puts a joined test player straight back on the highest-privilege role.
        // Restoring admin is how this suite grants the privilege back.
        player.Player.SetRole("admin");

        CommandResult accepted = await player.ExecuteCommand("/pulse attribution status");

        Assert.True(accepted.Ok, accepted.Message);
        Assert.Equal(
            "Attribution is off. It would run bursts of 5 ticks every 1s; 0 ticks profiled so far.",
            accepted.Message);
    }
}
