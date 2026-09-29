using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>The defaults are the config file a first boot writes, so they are part of the shipped
/// behaviour rather than an implementation detail.</summary>
public class PulseOtlpConfigTests
{
    [Fact]
    public void Defaults_Export_ToALocalCollector_EveryMinute()
    {
        PulseOtlpConfig config = new();

        Assert.True(config.Enabled);
        Assert.Equal("http://localhost:4318", config.Endpoint);
        Assert.Equal("http/protobuf", config.Protocol);
        Assert.Empty(config.Headers);
        Assert.Equal(60, config.IntervalSeconds);
        Assert.True(config.IncludeRuntimeMetrics);
        Assert.Equal("vintagestory", config.ServiceName);
    }

    [Theory]
    [InlineData(60, 60_000)]
    [InlineData(15, 15_000)]
    [InlineData(5, 5_000)]
    public void IntervalMilliseconds_Keeps_AnIntervalAtOrAboveTheFloor(int seconds, int expected)
        => Assert.Equal(expected, OtlpOptions.IntervalMilliseconds(seconds));

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-30)]
    public void IntervalMilliseconds_Floors_AnythingBelowFiveSeconds(int seconds)
        => Assert.Equal(5_000, OtlpOptions.IntervalMilliseconds(seconds));

    /// <summary>3,000,000 seconds, typed for "a lot less often", used to overflow a 32-bit
    /// millisecond count once multiplied by 1000 (it becomes -1,294,967,296), which the reader's
    /// own option validation then rejected with an unhandled ArgumentOutOfRangeException instead of
    /// this ever getting the chance to floor or cap it.</summary>
    [Fact]
    public void IntervalMilliseconds_Clamps_AValueThatWouldOtherwiseOverflowOnTheMultiply()
        => Assert.Equal(
            OtlpOptions.MaximumIntervalSeconds * 1000, OtlpOptions.IntervalMilliseconds(3_000_000));

    [Theory]
    [InlineData(86_400, 86_400_000)] // exactly the documented maximum: not touched
    [InlineData(86_401, 86_400_000)] // one second past it: capped
    [InlineData(int.MaxValue, 86_400_000)]
    public void IntervalMilliseconds_Caps_AnythingAboveTheDocumentedMaximum(int seconds, int expected)
        => Assert.Equal(expected, OtlpOptions.IntervalMilliseconds(seconds));
}
