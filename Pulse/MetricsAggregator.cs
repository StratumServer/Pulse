using System.Diagnostics.Metrics;

namespace Pulse;

/// <summary>Accumulates the measurements of one Meter into Prometheus-shaped series.</summary>
/// <remarks>Records arrive on the server's main thread (the tick listener) while scrapes read
/// from the HTTP thread, so both paths take the same lock. At one record per tick and one scrape
/// per few seconds, a single lock is not worth refining away.</remarks>
public sealed class MetricsAggregator : IDisposable
{
    private readonly object gate = new();
    private readonly List<Series> series = [];
    private readonly MeterListener listener = new();

    public MetricsAggregator(string meterName)
    {
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name != meterName)
            {
                return;
            }

            lock (gate)
            {
                series.Add(Series.For(instrument));
            }

            active.EnableMeasurementEvents(instrument);
        };

        // One callback per measurement type in use: Counter<long>, ObservableGauge<int>,
        // Histogram<double>. Widening to double at the call site keeps the path allocation-free.
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();
    }

    /// <summary>Takes a scrape: observable instruments are polled, then every series is copied out.</summary>
    /// <remarks>The observable callbacks run on THIS thread, which in the mod is the HTTP thread.
    /// They must therefore read only data the main thread has already published.</remarks>
    public IReadOnlyList<MetricSample> Collect()
    {
        listener.RecordObservableInstruments();

        lock (gate)
        {
            List<MetricSample> samples = new(series.Count);
            foreach (Series s in series)
            {
                samples.Add(new MetricSample(s.Name, s.Kind, s.Help, s.Value)
                {
                    Bounds = s.Bounds,
                    Buckets = (long[])s.Buckets.Clone(),
                    Sum = s.Sum,
                    Count = s.Count,
                });
            }

            return samples;
        }
    }

    public void Dispose() => listener.Dispose();

    private void Record(Instrument instrument, double value)
    {
        lock (gate)
        {
            Series? s = Find(instrument);
            if (s == null)
            {
                return;
            }

            switch (s.Kind)
            {
                case MetricKind.Counter:
                    s.Value += value;
                    break;
                case MetricKind.Gauge:
                    s.Value = value;
                    break;
                default:
                    s.Count++;
                    s.Sum += value;
                    int bucket = BucketIndex(s.Bounds, value);
                    if (bucket < s.Buckets.Length)
                    {
                        s.Buckets[bucket]++;
                    }

                    break;
            }
        }
    }

    // ponytail: linear scan over a handful of instruments, called once per record. A dictionary
    // the day Pulse publishes more than a dozen.
    private Series? Find(Instrument instrument)
    {
        foreach (Series s in series)
        {
            if (ReferenceEquals(s.Instrument, instrument))
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Index of the first bucket whose upper bound covers the value, or
    /// <c>bounds.Length</c> for the implicit +Inf bucket. Prometheus buckets are inclusive of
    /// their bound, so a value sitting exactly on one lands in it.</summary>
    private static int BucketIndex(double[] bounds, double value)
    {
        for (int i = 0; i < bounds.Length; i++)
        {
            if (value <= bounds[i])
            {
                return i;
            }
        }

        return bounds.Length;
    }

    private sealed class Series
    {
        public required Instrument Instrument { get; init; }

        public required string Name { get; init; }

        public required MetricKind Kind { get; init; }

        public required string Help { get; init; }

        public double[] Bounds { get; init; } = [];

        public long[] Buckets { get; init; } = [];

        public double Value { get; set; }

        public double Sum { get; set; }

        public long Count { get; set; }

        public static Series For(Instrument instrument)
        {
            // Bucket bounds ride on the instrument itself (InstrumentAdvice), so the aggregator
            // needs no per-instrument configuration of its own.
            double[] bounds = (instrument as Histogram<double>)?.Advice?.HistogramBucketBoundaries?.ToArray() ?? [];
            MetricKind kind = instrument is Histogram<double> ? MetricKind.Histogram
                : instrument.IsObservable ? MetricKind.Gauge
                : MetricKind.Counter;

            return new Series
            {
                Instrument = instrument,
                Name = instrument.Name,
                Kind = kind,
                Help = instrument.Description ?? string.Empty,
                Bounds = bounds,
                Buckets = new long[bounds.Length],
            };
        }
    }
}
