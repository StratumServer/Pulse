using Xunit;

namespace Pulse.Tests;

/// <summary>Resolve and TryRun are the pieces of a config load that touch nothing but a delegate:
/// no ICoreServerAPI, no file system. The wiring around them (calling LoadModConfig, writing
/// defaults, running ConfigUpgrade, logging) is exercised by the Atlas scenarios instead, the same
/// split as ConfigUpgrade.Compare against ConfigUpgrade.Upgrade.</summary>
public class ConfigLoadTests
{
    private sealed class FakeConfig
    {
        public int Value { get; set; }
    }

    [Fact]
    public void Resolve_Reports_Loaded_AndNeverCallsMakeDefaults_WhenTheAttemptFindsAFile()
    {
        FakeConfig found = new() { Value = 42 };
        bool calledMakeDefaults = false;

        ConfigLoadResult<FakeConfig> result = ConfigLoad.Resolve(
            () => found, () => { calledMakeDefaults = true; return new FakeConfig(); });

        Assert.Same(found, result.Config);
        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Null(result.FailureMessage);
        Assert.False(calledMakeDefaults, "a file that loaded fine has no reason to build a default");
    }

    [Fact]
    public void Resolve_Reports_Absent_AndHandsBackTheDefault_WhenTheAttemptFindsNothing()
    {
        FakeConfig defaults = new();

        ConfigLoadResult<FakeConfig> result = ConfigLoad.Resolve<FakeConfig>(() => null, () => defaults);

        Assert.Same(defaults, result.Config);
        Assert.Equal(ConfigLoadStatus.Absent, result.Status);
        Assert.Null(result.FailureMessage);
    }

    /// <summary>The case a stray comma produces: the file exists, so the attempt throws rather than
    /// returning null. This must fall back to defaults, not bubble up and stop the mod loading.</summary>
    [Fact]
    public void Resolve_Reports_Unreadable_AndFallsBackToDefaults_WhenTheAttemptThrows()
    {
        FakeConfig defaults = new();

        ConfigLoadResult<FakeConfig> result = ConfigLoad.Resolve<FakeConfig>(
            () => throw new FormatException("Invalid property identifier character: ,."), () => defaults);

        Assert.Same(defaults, result.Config);
        Assert.Equal(ConfigLoadStatus.Unreadable, result.Status);
        Assert.Equal("Invalid property identifier character: ,.", result.FailureMessage);
    }

    /// <summary>Not just a JSON syntax error: a file that exists but cannot even be read raises too,
    /// and both are the same "unreadable" outcome as far as the caller is concerned.</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Resolve_Treats_AnUnreadableFile_TheSameAsAMalformedOne(Type exceptionType)
    {
        Exception thrown = (Exception)Activator.CreateInstance(exceptionType, "broken")!;

        ConfigLoadResult<FakeConfig> result = ConfigLoad.Resolve<FakeConfig>(() => throw thrown, () => new FakeConfig());

        Assert.Equal(ConfigLoadStatus.Unreadable, result.Status);
        Assert.Equal("broken", result.FailureMessage);
    }

    [Fact]
    public void TryRun_ReturnsNull_WhenTheActionSucceeds()
    {
        bool ran = false;

        string? failure = ConfigLoad.TryRun(() => ran = true);

        Assert.Null(failure);
        Assert.True(ran);
    }

    /// <summary>What a read-only ModConfig folder does to a default-config write, and what a
    /// command name another mod already claimed does to <c>ChatCommands.Create</c>: both throw
    /// rather than fail quietly, and neither must be allowed to stop the rest of the mod from
    /// starting.</summary>
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void TryRun_ReturnsTheMessage_WhenTheActionThrows(Type exceptionType)
    {
        Exception thrown = (Exception)Activator.CreateInstance(exceptionType, "denied")!;

        string? failure = ConfigLoad.TryRun(() => throw thrown);

        Assert.Equal("denied", failure);
    }

    /// <summary><c>ChatCommands.Create</c> raises this exact exception, with this exact message,
    /// when another mod already registered the same command name (confirmed against
    /// ChatCommandImpl.WithName in VintagestoryLib).</summary>
    [Fact]
    public void TryRun_ReturnsTheMessage_ForACommandNameAlreadyClaimedByAnotherMod()
    {
        string? failure = ConfigLoad.TryRun(
            () => throw new InvalidOperationException("Command with such name already exists"));

        Assert.Equal("Command with such name already exists", failure);
    }

    [Fact]
    public void UnreadableMessage_Names_ThePathTheReasonAndWhatToDo()
    {
        string message = string.Format(
            ConfigLoad.UnreadableMessage, "Pulse", "/data/ModConfig/pulse.json", "stray comma",
            "Pulse is running on its built-in defaults (loopback bind)");

        Assert.Contains("/data/ModConfig/pulse.json", message);
        Assert.Contains("stray comma", message);
        Assert.Contains("delete it to get a fresh one with defaults", message);
        Assert.Contains("Pulse is running on its built-in defaults (loopback bind)", message);
    }
}
