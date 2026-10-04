using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>TryStoreDefaults and FileHoldsInstanceId are the pieces of
/// PulseOtlpModSystem.StartServerSide's config handling that touch nothing but a delegate: no
/// ICoreServerAPI, no file system. Delegate-driven like ConfigLoad.Resolve, for the same reason:
/// they need nothing from the engine and are unit-tested directly.</summary>
public class PulseOtlpModSystemTests
{
    [Fact]
    public void TryStoreDefaults_Succeeds_WhenTheDelegateDoesNotThrow()
    {
        bool called = false;

        bool result = PulseOtlpModSystem.TryStoreDefaults(() => called = true, out string? failureReason);

        Assert.True(result);
        Assert.True(called);
        Assert.Null(failureReason);
    }

    /// <summary>What StoreModConfig throws for a ModConfig directory mounted read-only: an
    /// UnauthorizedAccessException, or on some setups an IOException. Either must be swallowed
    /// rather than left to reach ModLoader, which is what used to stop the whole mod from starting
    /// over a file that was never going to be there anyway.</summary>
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void TryStoreDefaults_Fails_WithoutThrowing_WhenTheDelegateThrows(Type exceptionType)
    {
        Exception thrown = (Exception)Activator.CreateInstance(exceptionType, "read-only ModConfig")!;

        bool result = PulseOtlpModSystem.TryStoreDefaults(() => throw thrown, out string? failureReason);

        Assert.False(result);
        Assert.Equal(exceptionType.Name, failureReason);
    }

    [Fact]
    public void FileHoldsInstanceId_IsTrue_WhenTheFileReadsBackWithTheId()
        => Assert.True(PulseOtlpModSystem.FileHoldsInstanceId(
            () => new PulseOtlpConfig { ServiceInstanceId = "3f2a8c1e" }, "3f2a8c1e"));

    /// <summary>What a write that did not happen leaves behind: the file as it was, which loads with
    /// the key blank (it never had one, or held it empty) or with another id. Newtonsoft can load
    /// a null too, from an explicit null in the file.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("another-id")]
    public void FileHoldsInstanceId_IsFalse_WhenTheFileStillHoldsSomethingElse(string? onDisk)
        => Assert.False(PulseOtlpModSystem.FileHoldsInstanceId(
            () => new PulseOtlpConfig { ServiceInstanceId = onDisk! }, "3f2a8c1e"));

    /// <summary>LoadModConfig returns null for a file that is not there.</summary>
    [Fact]
    public void FileHoldsInstanceId_IsFalse_WhenTheFileIsGone()
        => Assert.False(PulseOtlpModSystem.FileHoldsInstanceId(() => null, "3f2a8c1e"));

    /// <summary>A file that exists and will not load throws instead: a parser's exception, or the
    /// read itself failing. Either is swallowed and counts as the id not being there, so the check
    /// can never be what takes a server down.</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(FormatException))]
    public void FileHoldsInstanceId_IsFalse_WhenTheFileWillNotLoad(Type exceptionType)
    {
        Exception thrown = (Exception)Activator.CreateInstance(exceptionType, "unreadable")!;

        Assert.False(PulseOtlpModSystem.FileHoldsInstanceId(() => throw thrown, "3f2a8c1e"));
    }

    /// <summary>The line is the admin's only notice, and the admin is the only one who can act on
    /// it: it has to name the file, the id this session exports, what the next start will do, and
    /// both ways to keep an id.</summary>
    [Fact]
    public void UnsavedInstanceIdMessage_Names_TheFile_TheId_TheCost_AndBothWaysToKeepOne()
    {
        string line = string.Format(
            PulseOtlpModSystem.UnsavedInstanceIdMessage, "pulse-otlp.json", "3f2a8c1e");

        Assert.Contains("could not save it to pulse-otlp.json", line);
        Assert.Contains("'3f2a8c1e' included", line);
        Assert.Contains("next start will export a different one", line);
        Assert.Contains("set ServiceInstanceId in pulse-otlp.json", line);
        Assert.Contains("OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id>", line);
    }
}
