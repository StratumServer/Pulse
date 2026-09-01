using System.Diagnostics.Metrics;
using System.Globalization;

namespace Pulse;

/// <summary>Accumulates the measurements of a set of Meters into Prometheus-shaped series.</summary>
/// <remarks>Records arrive on the server's main thread (the tick listener), on the worldgen
/// thread and on whatever thread logged, while scrapes read from the HTTP thread, so every path
/// takes the same lock. At a handful of records per tick and one scrape per few seconds, a single
/// lock is not worth refining away.</remarks>
public sealed class MetricsAggregator : IDisposable
{
    /// <summary>Instrument shape to the kind it renders as and whether one measurement carries an
    /// absolute value (replacing the series value) or a delta (added to it). Keyed by generic
    /// type definition rather than by name or by the IsObservable flag alone, because those two
    /// axes do not line up: an UpDownCounter reports deltas and renders as a gauge, an
    /// ObservableCounter reports a running total and renders as a counter.</summary>
    private static readonly Dictionary<Type, (MetricKind Kind, bool Absolute)> Shapes = new()
    {
        [typeof(Counter<>)] = (MetricKind.Counter, false),
        [typeof(ObservableCounter<>)] = (MetricKind.Counter, true),
        [typeof(UpDownCounter<>)] = (MetricKind.Gauge, false),
        [typeof(ObservableUpDownCounter<>)] = (MetricKind.Gauge, true),
        [typeof(ObservableGauge<>)] = (MetricKind.Gauge, true),
        [typeof(Gauge<>)] = (MetricKind.Gauge, true),
        [typeof(Histogram<>)] = (MetricKind.Histogram, false),
    };

    private readonly object gate = new();
    private readonly List<Series> series = [];
    private readonly MeterListener listener = new();

    public MetricsAggregator(params string[] meterNames)
        : this(null, meterNames)
    {
    }

    /// <param name="onUnsupported">Called once, at publish time, with the name of an instrument
    /// whose shape this aggregator cannot render. Such an instrument is then ignored entirely.</param>
    public MetricsAggregator(Action<string>? onUnsupported, params string[] meterNames)
    {
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (Array.IndexOf(meterNames, instrument.Meter.Name) < 0)
            {
                return;
            }

            if (!Shapes.ContainsKey(Definition(instrument)))
            {
                onUnsupported?.Invoke(instrument.Name);
                return;
            }

            active.EnableMeasurementEvents(instrument);
        };

        // One callback per measurement type in use: Counter<long>, ObservableGauge<int>,
        // Histogram<double>, and the runtime meter's long and double observables. Widening to
        // double at the call site keeps the untagged path allocation-free.
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
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
                    Labels = s.Labels,
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

    private static Type Definition(Instrument instrument)
    {
        Type type = instrument.GetType();
        return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }

    /// <summary>Turns the measurement tags into the sorted, stringified label set that identifies
    /// one series of a family.</summary>
    /// <remarks>Sorting by key makes two callers that pass the same tags in a different order land
    /// on the same series.</remarks>
    // ponytail: one small array per tagged measurement. Everything tagged here fires on a log
    // event or at scrape time, never per tick, and the untagged path returns the shared empty
    // array, so the hot tick counter still allocates nothing. Pool it the day something tagged
    // records every tick.
    private static KeyValuePair<string, string>[] Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return [];
        }

        KeyValuePair<string, string>[] labels = new KeyValuePair<string, string>[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            // Invariant, for the same reason the writer formats numbers invariantly: a tag value
            // that stringifies as "0,5" on a French host breaks every parser downstream.
            labels[i] = new(tags[i].Key, Convert.ToString(tags[i].Value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        Array.Sort(labels, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return labels;
    }

    private static bool SameLabels(KeyValuePair<string, string>[] a, KeyValuePair<string, string>[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        // Both are sorted by key, so equal sets compare equal pairwise.
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Key != b[i].Key || a[i].Value != b[i].Value)
            {
                return false;
            }
        }

        return true;
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

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        KeyValuePair<string, string>[] labels = Labels(tags);

        lock (gate)
        {
            Series? s = Find(instrument, labels) ?? Open(instrument, labels);
            if (s == null)
            {
                return;
            }

            if (s.Kind == MetricKind.Histogram)
            {
                s.Count++;
                s.Sum += value;
                int bucket = BucketIndex(s.Bounds, value);
                if (bucket < s.Buckets.Length)
                {
                    s.Buckets[bucket]++;
                }

                return;
            }

            s.Value = s.Absolute ? value : s.Value + value;
        }
    }

    // ponytail: linear scan over the published series, called once per record. Two dozen series
    // and a couple of records per tick make a dictionary index pure ceremony; add one the day a
    // meter with real label cardinality shows up.
    private Series? Find(Instrument instrument, KeyValuePair<string, string>[] labels)
    {
        foreach (Series s in series)
        {
            if (ReferenceEquals(s.Instrument, instrument) && SameLabels(s.Labels, labels))
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Starts the series for a tag set seen for the first time. Series appear on first
    /// measurement, not at publish time, because the tag sets an instrument will use are not
    /// knowable in advance; a counter that should exist from boot seeds itself with Add(0).</summary>
    private Series? Open(Instrument instrument, KeyValuePair<string, string>[] labels)
    {
        if (!Shapes.TryGetValue(Definition(instrument), out (MetricKind Kind, bool Absolute) shape))
        {
            return null;
        }

        // Bucket bounds ride on the instrument itself (InstrumentAdvice), so the aggregator needs
        // no per-instrument configuration of its own. A histogram of any other value type, or one
        // with no advice, keeps no buckets and renders as sum and count alone.
        double[] bounds = (instrument as Histogram<double>)?.Advice?.HistogramBucketBoundaries?.ToArray() ?? [];
        Series s = new()
        {
            Instrument = instrument,
            Name = instrument.Name,
            Kind = shape.Kind,
            Absolute = shape.Absolute,
            Help = instrument.Description ?? string.Empty,
            Labels = labels,
            Bounds = bounds,
            Buckets = new long[bounds.Length],
        };

        series.Add(s);
        return s;
    }

    private sealed class Series
    {
        public required Instrument Instrument { get; init; }

        public required string Name { get; init; }

        public required MetricKind Kind { get; init; }

        /// <summary>True when a measurement carries the instrument's current total rather than a
        /// delta to add to it.</summary>
        public required bool Absolute { get; init; }

        public required string Help { get; init; }

        public KeyValuePair<string, string>[] Labels { get; init; } = [];

        public double[] Bounds { get; init; } = [];

        public long[] Buckets { get; init; } = [];

        public double Value { get; set; }

        public double Sum { get; set; }

        public long Count { get; set; }
    }
}
