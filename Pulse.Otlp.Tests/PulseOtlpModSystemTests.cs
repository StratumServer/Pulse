using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>TryStoreDefaults is the one piece of PulseOtlpModSystem.StartServerSide's config
/// handling that touches nothing but a delegate: no ICoreServerAPI, no file system. Delegate-driven
/// like ConfigLoad.Resolve, for the same reason: it needs nothing from the engine and is
/// unit-tested directly.</summary>
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
}
