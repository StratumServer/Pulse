using Xunit;

namespace Pulse.Tests;

public class EngineSampleTests
{
    [Fact]
    public void BusySeconds_Averages_TheBucketAndConvertsFromMilliseconds()
    {
        // 120 ms of work spread over 60 ticks is 2 ms a tick.
        Assert.Equal(0.002, EngineSample.BusySeconds(120, 60), 10);
    }

    [Fact]
    public void BusySeconds_Reads_Zero_WhenTheBucketCountedNoTicks()
    {
        Assert.Equal(0, EngineSample.BusySeconds(0, 0));
    }

    /// <summary>A bucket the engine zeroed on rotation but never got to fill, as happens when a
    /// suspend swallows a whole window. Dividing anyway would report Infinity, which the exposition
    /// writer would faithfully serve as +Inf.</summary>
    [Fact]
    public void BusySeconds_Reads_Zero_WhenTicksAreZeroButTimeIsNot()
    {
        Assert.Equal(0, EngineSample.BusySeconds(1500, 0));
    }

    [Fact]
    public void BusySeconds_Keeps_SubMillisecondPrecision_AcrossTheAverage()
    {
        // 5 ms over 2 ticks is 2.5 ms, which is not representable in the whole milliseconds the
        // engine records per tick but comes straight out of the average.
        Assert.Equal(0.0025, EngineSample.BusySeconds(5, 2), 10);
    }
}
