using Xunit;

namespace Pulse.Tests;

public class PingSummaryTests
{
    [Fact]
    public void Of_Averages_AndMaxes_ThePingsGiven()
    {
        // Worst ping deliberately in the middle: a max that just keeps the last value it saw would
        // otherwise pass on an ascending set.
        PingSummary summary = PingSummary.Of([0.02f, 0.12f, 0.04f]);

        Assert.Equal(0.06, summary.AverageSeconds, 6);
        Assert.Equal(0.12, summary.MaxSeconds, 6);
    }

    /// <summary>The engine reports NaN for a player it no longer has a connection for. One of
    /// those in the set would make both aggregates NaN if it were not skipped.</summary>
    [Fact]
    public void Of_Skips_NaN_WithoutCountingItTowardTheAverage()
    {
        PingSummary summary = PingSummary.Of([0.02f, float.NaN, 0.04f]);

        Assert.Equal(0.03, summary.AverageSeconds, 6);
        Assert.Equal(0.04, summary.MaxSeconds, 6);
    }

    [Fact]
    public void Of_Reads_Zero_WhenEveryPingIsNaN()
    {
        PingSummary summary = PingSummary.Of([float.NaN, float.NaN]);

        Assert.Equal(0, summary.AverageSeconds);
        Assert.Equal(0, summary.MaxSeconds);
    }

    [Fact]
    public void Of_Reads_Zero_WhenNobodyIsOnline()
    {
        PingSummary summary = PingSummary.Of([]);

        Assert.Equal(0, summary.AverageSeconds);
        Assert.Equal(0, summary.MaxSeconds);
    }

    [Fact]
    public void Of_Reads_Zero_ForASinglePlayerWhoseConnectionIsGone()
    {
        Assert.Equal(new PingSummary(0, 0), PingSummary.Of([float.NaN]));
    }
}
