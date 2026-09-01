using System.Diagnostics.Metrics;
using Xunit;

namespace Pulse.Tests;

public class TickBookkeeperTests
{
    private const double Interval = 1.0;

    /// <summary>A MeterListener sees every Meter in the process, so each test gets its own name.</summary>
    private static string UniqueMeterName([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $"Pulse.Test.{caller}.{Guid.NewGuid():N}";

    [Fact]
    public void OnTick_Adds1ToTheCounter_OnEveryCall_IncludingTheFirst()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> ticks = meter.CreateCounter<long>("ticks_total", "{tick}", "T.");
        Histogram<double> seconds = meter.CreateHistogram<double>("tick_seconds", "s", "S.");
        TickBookkeeper bookkeeper = new(ticks, seconds, Interval);

        bookkeeper.OnTick(0);
        bookkeeper.OnTick(0.01);
        bookkeeper.OnTick(0.01);

        Assert.Equal(3, aggregator.Collect().Single(s => s.Name == "ticks_total").Value);
    }

    [Fact]
    public void OnTick_Ignores_TheDurationPassedOnTheFirstCall()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> ticks = meter.CreateCounter<long>("ticks_total", "{tick}", "T.");
        Histogram<double> seconds = meter.CreateHistogram<double>("tick_seconds", "s", "S.");
        TickBookkeeper bookkeeper = new(ticks, seconds, Interval);

        // A large first duration: if it were recorded or accumulated, the histogram would carry
        // it and this alone would already cross the snapshot interval.
        bool due = bookkeeper.OnTick(5.0);

        Assert.False(due);

        // A series exists from its first measurement (see MetricsAggregator), so an untouched
        // histogram reports no series at all rather than one sitting at a zero count.
        Assert.DoesNotContain(aggregator.Collect(), s => s.Name == "tick_seconds");
    }

    [Fact]
    public void OnTick_Records_DurationsFromTheSecondCallOnward()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> ticks = meter.CreateCounter<long>("ticks_total", "{tick}", "T.");
        Histogram<double> seconds = meter.CreateHistogram<double>("tick_seconds", "s", "S.");
        TickBookkeeper bookkeeper = new(ticks, seconds, Interval);

        bookkeeper.OnTick(0);      // warm-up call, not recorded
        bookkeeper.OnTick(0.02);
        bookkeeper.OnTick(0.03);

        MetricSample sample = aggregator.Collect().Single(s => s.Name == "tick_seconds");
        Assert.Equal(2, sample.Count);
        Assert.Equal(0.05, sample.Sum, 10);
    }

    [Fact]
    public void OnTick_SignalsDue_OnceAccumulatedSecondsReachTheInterval_ThenResetsTheAccumulator()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        Counter<long> ticks = meter.CreateCounter<long>("ticks_total", "{tick}", "T.");
        Histogram<double> seconds = meter.CreateHistogram<double>("tick_seconds", "s", "S.");
        TickBookkeeper bookkeeper = new(ticks, seconds, Interval);

        Assert.False(bookkeeper.OnTick(0));     // warm-up
        Assert.False(bookkeeper.OnTick(0.4));   // 0.4 accumulated
        Assert.False(bookkeeper.OnTick(0.4));   // 0.8 accumulated
        Assert.True(bookkeeper.OnTick(0.3));    // 1.1: crosses the interval, resets to 0

        // The accumulator restarted at zero, so a small duration right after does not fire again.
        Assert.False(bookkeeper.OnTick(0.1));
    }

    [Fact]
    public void OnTick_TreatsExactlyReachingTheInterval_AsDue()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        Counter<long> ticks = meter.CreateCounter<long>("ticks_total", "{tick}", "T.");
        Histogram<double> seconds = meter.CreateHistogram<double>("tick_seconds", "s", "S.");
        TickBookkeeper bookkeeper = new(ticks, seconds, Interval);

        bookkeeper.OnTick(0); // warm-up

        Assert.True(bookkeeper.OnTick(Interval));
    }
}
