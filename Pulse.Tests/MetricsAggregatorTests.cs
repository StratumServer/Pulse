using System.Diagnostics.Metrics;
using Xunit;

namespace Pulse.Tests;

public class MetricsAggregatorTests
{
    private static readonly double[] Bounds = [0.025, 0.05, 0.1];

    /// <summary>A MeterListener sees every Meter in the process, so each test gets its own name.</summary>
    private static string UniqueMeterName([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $"Pulse.Test.{caller}.{Guid.NewGuid():N}";

    private static Histogram<double> CreateHistogram(Meter meter, string name = "h_seconds", string help = "H.")
        => meter.CreateHistogram(name, "s", help, tags: null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = Bounds });

    private static MetricSample Sample(IReadOnlyList<MetricSample> samples, string name)
        => samples.Single(s => s.Name == name);

    [Fact]
    public void Counter_Accumulates_AcrossAdds()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{tick}", "C.");

        counter.Add(1);
        counter.Add(4);
        counter.Add(10);

        MetricSample sample = Sample(aggregator.Collect(), "c_total");
        Assert.Equal(MetricKind.Counter, sample.Kind);
        Assert.Equal("C.", sample.Help);
        Assert.Equal(15, sample.Value);
    }

    [Fact]
    public void Counter_Keeps_AccumulatingAcrossScrapes()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{tick}", "C.");

        counter.Add(2);
        Assert.Equal(2, Sample(aggregator.Collect(), "c_total").Value);
        counter.Add(3);
        Assert.Equal(5, Sample(aggregator.Collect(), "c_total").Value);
    }

    [Fact]
    public void ObservableGauge_Reads_ItsCallback_AtScrapeTime()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        int players = 0;
        meter.CreateObservableGauge("g_online", () => players, "{player}", "G.");

        Assert.Equal(0, Sample(aggregator.Collect(), "g_online").Value);
        players = 7;
        MetricSample sample = Sample(aggregator.Collect(), "g_online");
        Assert.Equal(MetricKind.Gauge, sample.Kind);
        Assert.Equal(7, sample.Value);
    }

    [Fact]
    public void Histogram_Places_ValuesInTheFirstBucketThatCoversThem()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> histogram = CreateHistogram(meter);

        histogram.Record(0.001);   // bucket 0
        histogram.Record(0.030);   // bucket 1
        histogram.Record(0.070);   // bucket 2

        MetricSample sample = Sample(aggregator.Collect(), "h_seconds");
        Assert.Equal(MetricKind.Histogram, sample.Kind);
        Assert.Equal(Bounds, sample.Bounds);
        Assert.Equal(new long[] { 1, 1, 1 }, sample.Buckets);
    }

    [Fact]
    public void Histogram_Counts_AValueExactlyOnABound_InThatBound()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> histogram = CreateHistogram(meter);

        histogram.Record(0.025);
        histogram.Record(0.05);
        histogram.Record(0.1);

        Assert.Equal(new long[] { 1, 1, 1 }, Sample(aggregator.Collect(), "h_seconds").Buckets);
    }

    [Fact]
    public void Histogram_Puts_ValuesAboveEveryBound_InTheImplicitInfBucket()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> histogram = CreateHistogram(meter);

        histogram.Record(0.02);
        histogram.Record(4.0);
        histogram.Record(9.5);

        MetricSample sample = Sample(aggregator.Collect(), "h_seconds");
        Assert.Equal(new long[] { 1, 0, 0 }, sample.Buckets);

        // Nothing above the last bound is stored in a bucket; Count carries it, which is what the
        // writer renders as le="+Inf".
        Assert.Equal(3, sample.Count);
        Assert.Equal(1, sample.Buckets.Sum());
    }

    [Fact]
    public void Histogram_Tracks_SumAndCount()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> histogram = CreateHistogram(meter);

        histogram.Record(0.01);
        histogram.Record(0.02);
        histogram.Record(0.5);

        MetricSample sample = Sample(aggregator.Collect(), "h_seconds");
        Assert.Equal(0.53, sample.Sum, 10);
        Assert.Equal(3, sample.Count);
    }

    [Fact]
    public void Collect_Returns_ACopyOfTheBuckets()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> histogram = CreateHistogram(meter);

        histogram.Record(0.01);
        MetricSample first = Sample(aggregator.Collect(), "h_seconds");
        histogram.Record(0.01);

        Assert.Equal(new long[] { 1, 0, 0 }, first.Buckets);
        Assert.Equal(new long[] { 2, 0, 0 }, Sample(aggregator.Collect(), "h_seconds").Buckets);
    }

    [Fact]
    public void Aggregator_Ignores_OtherMeters()
    {
        string meterName = UniqueMeterName();
        using Meter mine = new(meterName);
        using Meter other = new(meterName + ".Other");
        using MetricsAggregator aggregator = new(meterName);
        mine.CreateCounter<long>("mine_total", "{x}", "Mine.").Add(1);
        other.CreateCounter<long>("theirs_total", "{x}", "Theirs.").Add(1);

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Single(samples);
        Assert.Equal("mine_total", samples[0].Name);
    }

    [Fact]
    public void Records_And_Scrapes_CanRunConcurrently()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{tick}", "C.");
        Histogram<double> histogram = CreateHistogram(meter);
        const int records = 20_000;

        Thread recorder = new(() =>
        {
            for (int i = 0; i < records; i++)
            {
                counter.Add(1);
                histogram.Record(0.03);
            }
        });

        recorder.Start();
        int scrapes = 0;
        while (recorder.IsAlive)
        {
            IReadOnlyList<MetricSample> samples = aggregator.Collect();
            MetricSample h = Sample(samples, "h_seconds");
            Assert.Equal(h.Count, h.Buckets.Sum());
            scrapes++;
        }

        recorder.Join();
        Assert.True(scrapes > 0, "the scraping loop never ran");
        Assert.Equal(records, Sample(aggregator.Collect(), "c_total").Value);
        Assert.Equal(records, Sample(aggregator.Collect(), "h_seconds").Count);
    }
}
