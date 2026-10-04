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

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

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

    /// <summary>What lets a family like a per-mod share retire a tag set once its owner stops
    /// reporting it, instead of serving that tag set forever at whatever it last measured.</summary>
    [Fact]
    public void ObservableGauge_Retires_ATagSet_ItsCallbackStopsReporting()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        bool includeMod = true;
        meter.CreateObservableGauge("g_share", () =>
        {
            List<Measurement<double>> shares = [new(1.0, new KeyValuePair<string, object?>("modid", "engine"))];
            if (includeMod)
            {
                shares.Add(new(0.5, new KeyValuePair<string, object?>("modid", "mymod")));
            }

            return shares;
        });

        IReadOnlyList<MetricSample> withMod = aggregator.Collect();
        Assert.Contains(withMod, s => s.Name == "g_share" && s.Labels.Any(l => l.Value == "mymod"));

        includeMod = false;
        IReadOnlyList<MetricSample> withoutMod = aggregator.Collect();
        Assert.DoesNotContain(withoutMod, s => s.Name == "g_share" && s.Labels.Any(l => l.Value == "mymod"));
        Assert.Contains(withoutMod, s => s.Name == "g_share" && s.Labels.Any(l => l.Value == "engine"));
    }

    /// <summary>The exemption the retiring behaviour above needs: a synchronous instrument is only
    /// ever touched when the application explicitly records one, not every scrape, so a series it
    /// is not scraping-blind to must keep reading its last value rather than vanish between calls.</summary>
    [Fact]
    public void Counter_Keeps_ItsSeries_BetweenScrapesEvenWithoutBeingRecordedAgain()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{tick}", "C.");

        counter.Add(3);
        aggregator.Collect();
        aggregator.Collect();
        aggregator.Collect();

        Assert.Equal(3, Sample(aggregator.Collect(), "c_total").Value);
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

        // Value is the synchronous-counter/gauge field; a histogram must never fall through into
        // updating it too.
        Assert.Equal(0, sample.Value);
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

        using ManualResetEventSlim recording = new();
        Thread recorder = new(() =>
        {
            recording.Set();
            for (int i = 0; i < records; i++)
            {
                counter.Add(1);
                histogram.Record(0.03);
            }
        });

        // The start signal lines the scraper up with the recorder's first measurements, but how
        // many scrapes land inside the recording window is still the scheduler's call, so there is
        // no assertion on the count: the recorder can finish inside the very first Collect. What
        // every scrape must guarantee, concurrent or not, is a histogram whose count matches its
        // buckets.
        recorder.Start();
        recording.Wait();
        do
        {
            // A series exists from its first measurement, so the opening scrapes can beat the
            // recorder to it. Every scrape that does see the histogram must see it consistent.
            MetricSample? h = aggregator.Collect().SingleOrDefault(s => s.Name == "h_seconds");
            if (h == null)
            {
                continue;
            }

            Assert.Equal(h.Count, h.Buckets.Sum());
        }
        while (recorder.IsAlive);

        recorder.Join();
        Assert.Equal(records, Sample(aggregator.Collect(), "c_total").Value);
        Assert.Equal(records, Sample(aggregator.Collect(), "h_seconds").Count);
    }

    [Fact]
    public void Counter_Keeps_OneSeriesPerTagSet()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{entry}", "C.");

        counter.Add(1, new KeyValuePair<string, object?>("level", "warning"));
        counter.Add(2, new KeyValuePair<string, object?>("level", "error"));
        counter.Add(4, new KeyValuePair<string, object?>("level", "warning"));

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(2, samples.Count);
        Assert.Equal(5, samples.Single(s => s.Labels[0].Value == "warning").Value);
        Assert.Equal(2, samples.Single(s => s.Labels[0].Value == "error").Value);
        Assert.All(samples, s => Assert.Equal("c_total", s.Name));
    }

    [Fact]
    public void Tags_Sort_ByKey_SoCallSiteOrderDoesNotSplitASeries()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");

        counter.Add(1, new KeyValuePair<string, object?>("zone", "b"), new KeyValuePair<string, object?>("kind", "a"));
        counter.Add(1, new KeyValuePair<string, object?>("kind", "a"), new KeyValuePair<string, object?>("zone", "b"));

        MetricSample sample = Assert.Single(aggregator.Collect());
        Assert.Equal(2, sample.Value);
        Assert.Equal(["kind", "zone"], sample.Labels.Select(l => l.Key));
    }

    [Fact]
    public void ObservableCounter_Reports_ARunningTotal_SoTheSeriesTakesItRatherThanAddsIt()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        long collections = 5;
        meter.CreateObservableCounter("oc_total", () => collections, "{collection}", "OC.");

        MetricSample first = Sample(aggregator.Collect(), "oc_total");
        Assert.Equal(MetricKind.Counter, first.Kind);
        Assert.Equal(5, first.Value);

        collections = 9;
        Assert.Equal(9, Sample(aggregator.Collect(), "oc_total").Value);
    }

    [Fact]
    public void UpDownCounter_Adds_ItsDeltas_AndRendersAsAGauge()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        UpDownCounter<long> counter = meter.CreateUpDownCounter<long>("u_current", "{x}", "U.");

        counter.Add(5);
        counter.Add(-2);

        MetricSample sample = Sample(aggregator.Collect(), "u_current");
        Assert.Equal(MetricKind.Gauge, sample.Kind);
        Assert.Equal(3, sample.Value);
    }

    [Fact]
    public void Aggregator_Skips_AnInstrumentShapeItCannotRender()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        List<string> skipped = [];
        using MetricsAggregator aggregator = new(skipped.Add, meterName);

        _ = new UnknownShape(meter);

        Assert.Equal(["odd_thing"], skipped);
        Assert.Empty(aggregator.Collect());
    }

    [Fact]
    public void Aggregator_Renders_TheRuntimeMeter_WhenSubscribedToIt()
    {
        using MetricsAggregator aggregator = new("System.Runtime");

        string text = PrometheusText.Render(aggregator.Collect());

        // The built-in net10 meter, mapped: dotted names, an ObservableCounter that is a counter
        // with a _total suffix, an ObservableUpDownCounter that is a gauge, real tags, and the
        // unit-driven translation for the two families the OTLP path spells differently.
        Assert.Contains("# TYPE dotnet_gc_collections_total counter\n", text);
        Assert.Contains("dotnet_gc_collections_total{gc_heap_generation=\"gen0\"} ", text);
        Assert.Contains("# TYPE dotnet_process_memory_working_set_bytes gauge\n", text);
        Assert.Contains("dotnet_process_cpu_time_seconds_total{cpu_mode=\"user\"} ", text);
    }

    [Fact]
    public void Series_Defaults_MissingHelpAndUnit_ToEmptyStrings()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        // No unit, no description: both are optional on every Create* overload.
        Counter<long> counter = meter.CreateCounter<long>("bare_total");

        counter.Add(1);

        MetricSample sample = Sample(aggregator.Collect(), "bare_total");
        Assert.Equal(string.Empty, sample.Help);
        Assert.Equal(string.Empty, sample.Unit);
    }

    /// <summary>Newtonsoft can hand a log-derived tag a null value (an exception with no message,
    /// say); the label still has to render as something a Prometheus parser accepts, not throw or
    /// silently print the literal word "null".</summary>
    [Fact]
    public void ATagValueOfNull_Renders_AsAnEmptyLabelValue_NotTheWordNull()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");

        counter.Add(1, new KeyValuePair<string, object?>("reason", null));

        MetricSample sample = Sample(aggregator.Collect(), "c_total");
        Assert.Equal("", sample.Labels.Single(l => l.Key == "reason").Value);
    }

    /// <summary>Find() matches a series by instrument reference and label set together; a length
    /// mismatch alone has to be enough to rule two label sets out, or the same counter called once
    /// untagged and once with a tag could be folded into a single, wrongly-labelled series.</summary>
    [Fact]
    public void Counter_KeepsSeriesSeparate_ForTheSameInstrument_AtDifferentTagCounts()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");

        counter.Add(1);
        counter.Add(2, new KeyValuePair<string, object?>("k", "v"));

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(2, samples.Count);
        Assert.Equal(1, samples.Single(s => s.Labels.Length == 0).Value);
        Assert.Equal(2, samples.Single(s => s.Labels.Length == 1).Value);
    }

    /// <summary>An instrument that is none of the seven shapes the aggregator knows.</summary>
    private sealed class UnknownShape : Instrument
    {
        public UnknownShape(Meter meter)
            : base(meter, "odd_thing", null, "Odd.")
        {
            Publish();
        }
    }

    /// <summary>The same kind of unsupported shape as <see cref="UnknownShape"/>, but able to
    /// actually emit a measurement (through the protected RecordMeasurement Instrument{T} itself
    /// exposes), which a shape with no public Add or Record method never could. Needed to prove
    /// InstrumentPublished's own guard is what keeps an unsupported instrument's measurements out,
    /// not the accident of it having no way to measure anything in the first place.</summary>
    private sealed class UnknownMeasurableShape : Instrument<double>
    {
        public UnknownMeasurableShape(Meter meter)
            : base(meter, "odd_measurable_thing", null, "Odd.")
        {
            Publish();
        }

        public void Emit(double value) => RecordMeasurement(value);
    }

    [Fact]
    public void Aggregator_NeverRecords_AMeasurement_FromAnInstrumentShapeItCannotRender()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        UnknownMeasurableShape instrument = new(meter);

        instrument.Emit(5.0);

        Assert.Empty(aggregator.Collect());
    }

    /// <summary>A series belongs to the instrument that measured it, not to a tag set alone: two
    /// counters recording the very same tags keep a series each.</summary>
    [Fact]
    public void TheSameTagSet_OnTwoInstruments_IsTwoSeries()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> first = meter.CreateCounter<long>("first_total", "{x}", "First.");
        Counter<long> second = meter.CreateCounter<long>("second_total", "{x}", "Second.");

        first.Add(1, Tag("k", "v"));
        second.Add(10, Tag("k", "v"));
        first.Add(1, Tag("k", "v"));

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(2, samples.Count);
        Assert.Equal(2, Sample(samples, "first_total").Value);
        Assert.Equal(10, Sample(samples, "second_total").Value);
    }

    /// <summary>One key or one value is enough to make another series, and a tag set that only
    /// starts like another, or spells the same characters with the split between key and value
    /// somewhere else, is not that other one either.</summary>
    [Fact]
    public void TagSets_ThatDifferInOneKeyOrOneValue_StayApart()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");

        counter.Add(1, Tag("a", "1"), Tag("b", "2"), Tag("c", "3"));
        counter.Add(2, Tag("a", "1"), Tag("b", "2"), Tag("c", "4"));    // the last value differs
        counter.Add(4, Tag("a", "1"), Tag("b", "2"), Tag("d", "3"));    // the last key differs
        counter.Add(8, Tag("a", "1"), Tag("b", "2"));                   // the first without its last tag
        counter.Add(16, Tag("ab", "c"));
        counter.Add(32, Tag("a", "bc"));                                // the same characters, split elsewhere
        counter.Add(64, Tag("c", "3"), Tag("b", "2"), Tag("a", "1"));   // the first again, keys the other way round

        Assert.Equal([65, 2, 4, 8, 16, 32], aggregator.Collect().Select(s => s.Value));
    }

    /// <summary>A thousand tag sets of two keys each, recorded in an order unrelated to their
    /// names and once in each key order: every one is served as a series of its own, holding
    /// only its own measurements, in the order the tag sets first appeared.</summary>
    [Fact]
    public void ManyTagSets_AreAllServed_EachWithItsOwnValue_InTheOrderTheyFirstAppeared()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");
        const int tagSets = 1_000;

        // 7 shares no factor with 1,000, so the stride visits every id exactly once per round.
        List<int> order = Enumerable.Range(0, tagSets).Select(i => i * 7 % tagSets).ToList();
        foreach (int id in order)
        {
            counter.Add(id + 1, Tag("id", $"{id}"), Tag("group", $"{id % 10}"));
        }

        foreach (int id in order)
        {
            counter.Add(id + 1, Tag("group", $"{id % 10}"), Tag("id", $"{id}"));
        }

        // Labels come sorted by key: group, then id.
        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(tagSets, samples.Count);
        Assert.Equal(order, samples.Select(s => int.Parse(s.Labels[1].Value)));
        Assert.All(samples, s => Assert.Equal(2 * (int.Parse(s.Labels[1].Value) + 1), s.Value));
        Assert.All(samples, s => Assert.Equal(int.Parse(s.Labels[1].Value) % 10, int.Parse(s.Labels[0].Value)));
    }

    /// <summary>The index is only as good as its key. Equal tag sets held in two arrays have to
    /// hash alike, since the dictionary finds a series by that, and a thousand different ones must
    /// not pile into a few buckets: a hash that collapsed would still serve every value right, only
    /// as slowly as the scan it replaced, which no other test could tell.</summary>
    [Fact]
    public void TheSeriesKey_Compares_ByInstrumentAndTagSetContent_AndSpreadsTagSetsInItsHash()
    {
        using Meter meter = new(UniqueMeterName());
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{x}", "C.");
        Counter<long> other = meter.CreateCounter<long>("other_total", "{x}", "Other.");
        static KeyValuePair<string, string>[] Labels(int id) => [new("group", $"{id % 10}"), new("id", $"{id}")];

        MetricsAggregator.SeriesKey seven = new(counter, Labels(7));
        MetricsAggregator.SeriesKey sevenAgain = new(counter, Labels(7));
        Assert.Equal(seven, sevenAgain);
        Assert.Equal(seven.GetHashCode(), sevenAgain.GetHashCode());

        // The dictionary only asks whether two keys are equal once their hashes are, so each part
        // of the equality is pinned here, on its own, and not only through the aggregator.
        Assert.NotEqual(seven, new MetricsAggregator.SeriesKey(counter, Labels(8)));
        Assert.NotEqual(seven, new MetricsAggregator.SeriesKey(other, Labels(7)));

        // Hash codes are 32 bits, so a few of the thousand may meet by chance; a hash that ignores
        // what tells these tag sets apart is nowhere near that.
        int distinct = Enumerable.Range(0, 1_000)
            .Select(id => new MetricsAggregator.SeriesKey(counter, Labels(id)).GetHashCode())
            .Distinct()
            .Count();
        Assert.True(distinct > 990, $"{distinct} different hashes for 1,000 tag sets");

        // Untagged series, the ones recorded every tick, differ by their instrument alone.
        int untagged = Enumerable.Range(0, 100)
            .Select(i => new MetricsAggregator.SeriesKey(meter.CreateCounter<long>($"u{i}_total"), []).GetHashCode())
            .Distinct()
            .Count();
        Assert.True(untagged > 95, $"{untagged} different hashes for 100 untagged instruments");
    }

    /// <summary>A tag set an observable instrument stops reporting is retired. One it reports
    /// again later is a series like any new one: served again, after the others of its family,
    /// and not recorded into the series that was retired.</summary>
    [Fact]
    public void ObservableGauge_ServesATagSet_ThatReturnsAfterBeingRetired_LastInItsFamily()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        string[] reported = ["a", "b", "c"];
        double reading = 1;
        meter.CreateObservableGauge(
            "g_share", () => reported.Select(modid => new Measurement<double>(reading, Tag("modid", modid))).ToList());

        Assert.Equal(["a", "b", "c"], aggregator.Collect().Select(s => s.Labels[0].Value));

        reported = ["b", "c"];
        Assert.Equal(["b", "c"], aggregator.Collect().Select(s => s.Labels[0].Value));

        reported = ["a", "b", "c"];
        reading = 2;
        IReadOnlyList<MetricSample> back = aggregator.Collect();
        Assert.Equal(["b", "c", "a"], back.Select(s => s.Labels[0].Value));
        Assert.All(back, s => Assert.Equal(2, s.Value));
    }

    /// <summary>The exposition text of a fixed run of measurements, word for word. How a series is
    /// found must never change what is served or in which order, so this holds the whole text of a
    /// run that interleaves its families (a family's series do not arrive together), records one
    /// tag set in both key orders, and measures a histogram with and without tags. The order after
    /// a series is retired is held by ObservableGauge_ServesATagSet_ThatReturnsAfterBeingRetired_LastInItsFamily,
    /// which this text cannot see.</summary>
    [Fact]
    public void TheServedText_ForAFixedRunOfMeasurements_IsPinned()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> entries = meter.CreateCounter<long>("pin_entries_total", "{entry}", "Entries by level and source.");
        Counter<long> bare = meter.CreateCounter<long>("pin_bare_total", "{x}", "No tags.");
        Histogram<double> latency = CreateHistogram(meter, "pin_latency_seconds", "Latency by route.");
        UpDownCounter<long> depth = meter.CreateUpDownCounter<long>("pin_queue_depth", "{item}", "Depth by queue.");
        meter.CreateObservableGauge(
            "pin_share",
            () => new[]
            {
                new Measurement<double>(0.25, Tag("modid", "engine")),
                new Measurement<double>(0.75, Tag("modid", "game")),
            },
            description: "Share by mod.");

        entries.Add(1, Tag("level", "warning"), Tag("source", "a"));
        bare.Add(5);
        latency.Record(0.03, Tag("route", "/a"));
        entries.Add(2, Tag("level", "error"), Tag("source", "a"));
        depth.Add(3, Tag("queue", "x"));
        entries.Add(4, Tag("source", "a"), Tag("level", "warning"));    // the first tag set, keys the other way round
        latency.Record(0.2, Tag("route", "/b"));
        latency.Record(0.01, Tag("route", "/a"));
        entries.Add(8, Tag("level", "warning"), Tag("source", "b"));    // one value away from the first
        depth.Add(-1, Tag("queue", "x"));
        depth.Add(2, Tag("queue", "y"));
        bare.Add(1);
        latency.Record(0.07);                                           // the same histogram, no tags

        Assert.Equal(
            "# HELP pin_entries_total Entries by level and source.\n" +
            "# TYPE pin_entries_total counter\n" +
            "pin_entries_total{level=\"warning\",source=\"a\"} 5\n" +
            "pin_entries_total{level=\"error\",source=\"a\"} 2\n" +
            "pin_entries_total{level=\"warning\",source=\"b\"} 8\n" +
            "# HELP pin_bare_total No tags.\n" +
            "# TYPE pin_bare_total counter\n" +
            "pin_bare_total 6\n" +
            "# HELP pin_latency_seconds Latency by route.\n" +
            "# TYPE pin_latency_seconds histogram\n" +
            "pin_latency_seconds_bucket{route=\"/a\",le=\"0.025\"} 1\n" +
            "pin_latency_seconds_bucket{route=\"/a\",le=\"0.05\"} 2\n" +
            "pin_latency_seconds_bucket{route=\"/a\",le=\"0.1\"} 2\n" +
            "pin_latency_seconds_bucket{route=\"/a\",le=\"+Inf\"} 2\n" +
            "pin_latency_seconds_sum{route=\"/a\"} 0.04\n" +
            "pin_latency_seconds_count{route=\"/a\"} 2\n" +
            "pin_latency_seconds_bucket{route=\"/b\",le=\"0.025\"} 0\n" +
            "pin_latency_seconds_bucket{route=\"/b\",le=\"0.05\"} 0\n" +
            "pin_latency_seconds_bucket{route=\"/b\",le=\"0.1\"} 0\n" +
            "pin_latency_seconds_bucket{route=\"/b\",le=\"+Inf\"} 1\n" +
            "pin_latency_seconds_sum{route=\"/b\"} 0.2\n" +
            "pin_latency_seconds_count{route=\"/b\"} 1\n" +
            "pin_latency_seconds_bucket{le=\"0.025\"} 0\n" +
            "pin_latency_seconds_bucket{le=\"0.05\"} 0\n" +
            "pin_latency_seconds_bucket{le=\"0.1\"} 1\n" +
            "pin_latency_seconds_bucket{le=\"+Inf\"} 1\n" +
            "pin_latency_seconds_sum 0.07\n" +
            "pin_latency_seconds_count 1\n" +
            "# HELP pin_queue_depth Depth by queue.\n" +
            "# TYPE pin_queue_depth gauge\n" +
            "pin_queue_depth{queue=\"x\"} 2\n" +
            "pin_queue_depth{queue=\"y\"} 2\n" +
            "# HELP pin_share Share by mod.\n" +
            "# TYPE pin_share gauge\n" +
            "pin_share{modid=\"engine\"} 0.25\n" +
            "pin_share{modid=\"game\"} 0.75\n",
            PrometheusText.Render(aggregator.Collect()));
    }

    /// <summary>The same shape as the untagged concurrency test above, with series being opened:
    /// several recorders each open tag sets of their own and all keep adding to one they share,
    /// while scrapes keep running. Nothing may be lost, doubled or thrown.</summary>
    [Fact]
    public void TaggedRecords_And_Scrapes_CanRunConcurrently()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> counter = meter.CreateCounter<long>("c_total", "{tick}", "C.");
        const int recorders = 4;
        const int tagSets = 100;
        const int rounds = 100;

        Thread[] threads = Enumerable.Range(0, recorders).Select(recorder => new Thread(() =>
        {
            for (int round = 0; round < rounds; round++)
            {
                for (int id = 0; id < tagSets; id++)
                {
                    counter.Add(1, Tag("recorder", $"{recorder}"), Tag("id", $"{id}"));
                    counter.Add(1, Tag("recorder", "all"));
                }
            }
        })).ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        // A pause between scrapes: Collect holds the lock for its whole copy, and a scrape that
        // came straight back would leave the recorders waiting for it most of the time.
        do
        {
            aggregator.Collect();
            Thread.Sleep(1);
        }
        while (threads.Any(thread => thread.IsAlive));

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal((recorders * tagSets) + 1, samples.Count);
        Assert.Equal(recorders * tagSets * rounds, samples.Single(s => s.Labels.Length == 1).Value);
        Assert.All(samples.Where(s => s.Labels.Length == 2), s => Assert.Equal(rounds, s.Value));
    }
}
