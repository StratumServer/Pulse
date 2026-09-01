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
    public void SeedCounters_Seeds_EveryDeclaredLevelAndKind_AtZero()
    {
        string meterName = UniqueMeterName();
        using Meter meter = new(meterName);
        using MetricsAggregator aggregator = new(meterName);
        Counter<long> columnsGenerated = meter.CreateCounter<long>("columns_total", "{column}", "C.");
        Counter<long> logEntries = meter.CreateCounter<long>("log_entries_total", "{entry}", "L.");
        Counter<long> engineWarnings = meter.CreateCounter<long>("engine_warnings_total", "{warning}", "W.");

        MethodInfo seedCounters = typeof(PulseModSystem)
            .GetMethod("SeedCounters", BindingFlags.NonPublic | BindingFlags.Static)!;
        seedCounters.Invoke(null, [columnsGenerated, logEntries, engineWarnings]);

        IReadOnlyList<MetricSample> samples = aggregator.Collect();
        Assert.Equal(0, samples.Single(s => s.Name == "columns_total").Value);

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
}
