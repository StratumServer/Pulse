using System.Diagnostics.Metrics;
using System.Reflection;
using Xunit;

namespace Pulse.Tests;

/// <summary>SeedCounters is the one piece of PulseModSystem that touches nothing but a Meter: no
/// ICoreServerAPI, no world, no server. It stays private (only StartServerSide calls it), so this
/// drives it through reflection rather than widening PulseModSystem's public surface for a test.</summary>
public class PulseModSystemTests
{
    private static string UniqueMeterName([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $"Pulse.Test.{caller}.{Guid.NewGuid():N}";

    [Fact]
    public void SeedCounters_Seeds_EveryUntaggedCounterAndEveryDeclaredLevelAndKind_AtZero()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> columnsGenerated = meter.CreateCounter<long>("columns_total", "{column}", "C.");
        Counter<long> deaths = meter.CreateCounter<long>("deaths_total", "{death}", "D.");
        Counter<long> logEntries = meter.CreateCounter<long>("log_entries_total", "{entry}", "L.");
        Counter<long> engineWarnings = meter.CreateCounter<long>("engine_warnings_total", "{warning}", "W.");
        Counter<double> suspendSeconds = meter.CreateCounter<double>("suspend_seconds_total", "s", "S.");

        MethodInfo seedCounters = typeof(PulseModSystem)
            .GetMethod("SeedCounters", BindingFlags.NonPublic | BindingFlags.Static)!;
        seedCounters.Invoke(null, [logEntries, engineWarnings, suspendSeconds, new[] { columnsGenerated, deaths }]);

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(0, samples.Single(s => s.Name == "columns_total").Value);
        Assert.Equal(0, samples.Single(s => s.Name == "deaths_total").Value);
        Assert.Equal(0, samples.Single(s => s.Name == "suspend_seconds_total").Value);

        List<MetricSample> logSamples = samples.Where(s => s.Name == "log_entries_total").ToList();
        Assert.Equal(LogClassifier.Levels.Count, logSamples.Count);
        foreach (string level in LogClassifier.Levels)
        {
            MetricSample sample = logSamples.Single(s => s.Labels.Single().Value == level);
            Assert.Equal(0, sample.Value);
        }

        List<MetricSample> warningSamples = samples.Where(s => s.Name == "engine_warnings_total").ToList();
        Assert.Equal(LogClassifier.Kinds.Count, warningSamples.Count);
        foreach (string kind in LogClassifier.Kinds)
        {
            MetricSample sample = warningSamples.Single(s => s.Labels.Single().Value == kind);
            Assert.Equal(0, sample.Value);
        }
    }

    [Theory]
    [InlineData(0, 1000)] // The floor: a 0 or negative config value must not clone the chunk map every tick.
    [InlineData(-5, 1000)]
    [InlineData(30, 30_000)] // The documented default, unaffected by either clamp.
    public void ChunksListenerPeriodMs_Clamps_ToTheFloor(int configuredSeconds, int expectedMs)
        => Assert.Equal(expectedMs, PulseModSystem.ChunksListenerPeriodMs(configuredSeconds));

    /// <summary>The overflow the review found: multiplied by 1000 with no ceiling, a value just
    /// above roughly 2.147 million seconds wraps a 32 bit int negative, which the engine then runs
    /// on every single tick instead of never.</summary>
    [Fact]
    public void ChunksListenerPeriodMs_Clamps_BelowTheOverflowBoundary()
    {
        int unclamped = unchecked(2_147_484 * 1000);
        Assert.True(unclamped < 0, "the boundary chosen for this test must actually overflow int");

        int periodMs = PulseModSystem.ChunksListenerPeriodMs(2_147_484);

        Assert.True(periodMs > 0, "the clamped period must never go negative");
        Assert.Equal(86_400_000, periodMs); // Clamped to the documented one day maximum.
    }

    /// <summary>Reproduces the measured regression: a server with three players slips to about
    /// 33.5ms per tick (29.8 TPS) against the 33.33ms budget, a perfectly healthy reading. Before
    /// the 0.035 and 0.04 boundaries existed, those slipped ticks and the rare slower one shared a
    /// single wide (0.0334, 0.05] bucket, and Prometheus's own histogram_quantile linearly
    /// interpolating across that whole span read p99 around 49.8ms. Records through the same
    /// InstrumentAdvice-boundaries path <see cref="PulseModSystem"/> itself uses, so reverting
    /// <see cref="PulseModSystem.TickBuckets"/> to its old layout fails this test.
    /// contrib/alerts/pulse-alerts.test.yml has the matching promtool case for the PromQL side.</summary>
    [Fact]
    public void TickBuckets_KeepP99Honest_ForAHealthySlippedServer()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Histogram<double> tickSeconds = meter.CreateHistogram(
            "tick_seconds", "s", "T.", tags: null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = PulseModSystem.TickBuckets });

        for (int i = 0; i < 995; i++)
        {
            tickSeconds.Record(0.0335);
        }

        for (int i = 0; i < 5; i++)
        {
            tickSeconds.Record(0.045);
        }

        MetricSample sample = aggregator.Collect().Single(s => s.Name == "tick_seconds");
        double p99 = HistogramQuantile(0.99, sample.Bounds, sample.Buckets, sample.Count);

        Assert.True(p99 < 0.036, $"p99 read {p99}s: a wide-bucket interpolation artefact, not a real overrun.");
    }

    /// <summary>Prometheus's own histogram_quantile, linear interpolation over classic buckets,
    /// just enough of it to check boundaries against the same math a Grafana dashboard runs.
    /// <paramref name="buckets"/> is per-bucket, not cumulative, matching
    /// <see cref="MetricSample.Buckets"/>.</summary>
    private static double HistogramQuantile(double q, double[] bounds, long[] buckets, double count)
    {
        double goal = q * count;
        double lowerBound = 0;
        long lowerCumulative = 0;
        long cumulative = 0;
        for (int i = 0; i < bounds.Length; i++)
        {
            cumulative += buckets[i];
            if (cumulative >= goal)
            {
                return lowerBound + (bounds[i] - lowerBound) * (goal - lowerCumulative) / (cumulative - lowerCumulative);
            }

            lowerBound = bounds[i];
            lowerCumulative = cumulative;
        }

        return bounds[^1];
    }
}
