using Vintagestory.API.Common;
using Xunit;

namespace Pulse.Tests;

public class LogClassifierTests
{
    [Theory]
    [InlineData(EnumLogType.Warning, "warning")]
    [InlineData(EnumLogType.Error, "error")]
    [InlineData(EnumLogType.Fatal, "fatal")]
    public void Level_Names_TheSeveritiesPulseCounts(EnumLogType type, string expected)
    {
        Assert.Equal(expected, LogClassifier.Level(type));
    }

    [Theory]
    [InlineData(EnumLogType.Notification)]
    [InlineData(EnumLogType.Debug)]
    [InlineData(EnumLogType.Chat)]
    [InlineData(EnumLogType.Audit)]
    [InlineData(EnumLogType.Worldgen)]
    public void Level_Ignores_TheSeveritiesThatSayNothingAboutHealth(EnumLogType type)
    {
        Assert.Null(LogClassifier.Level(type));
    }

    /// <summary>The engine's own 1.22.7 strings, verbatim from the decompiled call sites, as
    /// EntryAdded hands them over: the format string, before argument substitution.</summary>
    [Theory]
    [InlineData("Server overloaded. A tick took {0}ms to complete.", "overload")]
    [InlineData(
        "The server is currently using more than 90% of its maximum allowed memory. If usage reaches 100% (4096 MB), the server will shut down automatically.",
        "memory")]
    [InlineData(
        "Server suspend requested, but reached max wait time. A thread is possibly deadlocked/in an endless loop. Will resume again.",
        "suspend_timeout")]
    [InlineData(
        "Call to autosave, but server is already saving. May indicate a disk i/o bottleneck. Reduce autosave interval or improve file i/o. Will ignore this autosave call.",
        "autosave_io")]
    public void EngineWarning_Matches_EveryEngineStringItTracks(string format, string expected)
    {
        Assert.Equal(expected, LogClassifier.EngineWarning(EnumLogType.Warning, format));
    }

    [Fact]
    public void EngineWarning_Covers_EveryKindItAdvertises()
    {
        string[] matched =
        [
            LogClassifier.EngineWarning(EnumLogType.Warning, "Server overloaded. A tick took {0}ms to complete.")!,
            LogClassifier.EngineWarning(EnumLogType.Warning, "The server is currently using more than 90% of its maximum allowed memory.")!,
            LogClassifier.EngineWarning(EnumLogType.Warning, "Server suspend requested, but reached max wait time.")!,
            LogClassifier.EngineWarning(EnumLogType.Warning, "Call to autosave, but server is already saving.")!,
        ];

        Assert.Equal(LogClassifier.Kinds, matched);
    }

    [Fact]
    public void EngineWarning_Ignores_AnOrdinaryWarning()
    {
        Assert.Null(LogClassifier.EngineWarning(
            EnumLogType.Warning, "Mod {0} has no modinfo.json, ignoring it."));
    }

    [Fact]
    public void EngineWarning_Ignores_TheSameTextAtAnotherSeverity()
    {
        // The prefixes are engine warnings. Something else logging the same sentence at another
        // level is not the engine reporting an overload.
        Assert.Null(LogClassifier.EngineWarning(
            EnumLogType.Notification, "Server overloaded. A tick took {0}ms to complete."));
    }

    [Fact]
    public void EngineWarning_Ignores_AnEntryWithNoMessage()
    {
        Assert.Null(LogClassifier.EngineWarning(EnumLogType.Warning, null));
    }
}
