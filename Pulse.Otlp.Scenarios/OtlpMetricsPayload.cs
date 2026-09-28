using System.Text;

namespace Pulse.Otlp.Scenarios;

/// <summary>Reads back the handful of fields a scenario needs from an OTLP metrics export: which
/// metrics are in it, and what their data points carry.</summary>
/// <remarks>Hand-rolled rather than a protobuf library: OpenTelemetry.Exporter.OpenTelemetryProtocol
/// writes the wire format itself and does not carry Google.Protobuf, so there is no public
/// deserializer for OpenTelemetry.Proto.Metrics.V1 reachable from a test project, and pulling in a
/// general protobuf library to read a handful of counters is not worth a dependency. Field numbers
/// are taken from the OTLP v1 proto definitions (opentelemetry/proto/metrics/v1/metrics.proto and
/// .../common/v1/common.proto): ExportMetricsServiceRequest.resource_metrics=1,
/// ResourceMetrics.scope_metrics=2, ScopeMetrics.metrics=2, Metric.name=1/gauge=5/sum=7,
/// Sum.data_points=1 (Gauge.data_points=1 too), NumberDataPoint.attributes=7/as_double=4/as_int=6,
/// KeyValue.key=1/value=2, AnyValue.string_value=1.</remarks>
internal static class OtlpMetricsPayload
{
    internal readonly record struct Point(IReadOnlyDictionary<string, string> Attributes, double Value);

    /// <summary>Every data point recorded for one metric name, searched across every resource and
    /// scope in the export. Empty when the metric never reached the collector at all, which is
    /// exactly what an OTLP export looks like for an instrument nobody was listening to when it was
    /// measured: no envelope, nothing to find.</summary>
    internal static List<Point> PointsFor(byte[] exportRequest, string metricName)
    {
        List<Point> points = new();
        foreach (byte[] resourceMetrics in SubMessages(exportRequest, 1))
        {
            foreach (byte[] scopeMetrics in SubMessages(resourceMetrics, 2))
            {
                foreach (byte[] metric in SubMessages(scopeMetrics, 2))
                {
                    if (StringField(metric, 1) != metricName)
                    {
                        continue;
                    }

                    // 7 is Sum, which is every counter Pulse publishes; 5 is Gauge. Both carry
                    // their data points as NumberDataPoint the same way, and nothing here needs
                    // to tell the two apart.
                    foreach (int dataField in new[] { 7, 5 })
                    {
                        foreach (byte[] aggregation in SubMessages(metric, dataField))
                        {
                            foreach (byte[] dataPoint in SubMessages(aggregation, 1))
                            {
                                points.Add(ReadDataPoint(dataPoint));
                            }
                        }
                    }
                }
            }
        }

        return points;
    }

    private static Point ReadDataPoint(byte[] dataPoint)
    {
        Dictionary<string, string> attributes = new();
        double value = 0;
        int i = 0;
        while (i < dataPoint.Length)
        {
            (int field, int wireType) = ReadTag(dataPoint, ref i);
            switch (wireType)
            {
                case 0:
                    ReadVarint(dataPoint, ref i);
                    break;
                case 1 when field == 6: // as_int (sfixed64)
                    value = BitConverter.ToInt64(dataPoint, i);
                    i += 8;
                    break;
                case 1 when field == 4: // as_double
                    value = BitConverter.ToDouble(dataPoint, i);
                    i += 8;
                    break;
                case 1: // start_time_unix_nano / time_unix_nano
                    i += 8;
                    break;
                case 5:
                    i += 4;
                    break;
                case 2:
                    int length = (int)ReadVarint(dataPoint, ref i);
                    if (field == 7) // attributes
                    {
                        (string key, string attributeValue) = ReadKeyValue(dataPoint[i..(i + length)]);
                        attributes[key] = attributeValue;
                    }

                    i += length;
                    break;
                default:
                    throw new InvalidOperationException($"unexpected wire type {wireType} in NumberDataPoint");
            }
        }

        return new Point(attributes, value);
    }

    private static (string Key, string Value) ReadKeyValue(byte[] keyValue)
    {
        string key = string.Empty;
        string value = string.Empty;
        int i = 0;
        while (i < keyValue.Length)
        {
            (int field, int wireType) = ReadTag(keyValue, ref i);
            if (wireType != 2)
            {
                throw new InvalidOperationException("KeyValue field was not length-delimited");
            }

            int length = (int)ReadVarint(keyValue, ref i);
            if (field == 1)
            {
                key = Encoding.UTF8.GetString(keyValue, i, length);
            }
            else if (field == 2)
            {
                value = AnyValueString(keyValue[i..(i + length)]);
            }

            i += length;
        }

        return (key, value);
    }

    /// <summary>Only string_value (field 1): the only shape Pulse's own attributes ever use.</summary>
    private static string AnyValueString(byte[] anyValue)
    {
        int i = 0;
        while (i < anyValue.Length)
        {
            (int field, int wireType) = ReadTag(anyValue, ref i);
            if (field == 1 && wireType == 2)
            {
                int length = (int)ReadVarint(anyValue, ref i);
                return Encoding.UTF8.GetString(anyValue, i, length);
            }

            SkipField(anyValue, ref i, wireType);
        }

        return string.Empty;
    }

    private static string? StringField(byte[] message, int fieldNumber)
    {
        int i = 0;
        while (i < message.Length)
        {
            (int field, int wireType) = ReadTag(message, ref i);
            if (field == fieldNumber && wireType == 2)
            {
                int length = (int)ReadVarint(message, ref i);
                return Encoding.UTF8.GetString(message, i, length);
            }

            SkipField(message, ref i, wireType);
        }

        return null;
    }

    private static List<byte[]> SubMessages(byte[] message, int fieldNumber)
    {
        List<byte[]> result = new();
        int i = 0;
        while (i < message.Length)
        {
            (int field, int wireType) = ReadTag(message, ref i);
            if (field == fieldNumber && wireType == 2)
            {
                int length = (int)ReadVarint(message, ref i);
                result.Add(message[i..(i + length)]);
                i += length;
            }
            else
            {
                SkipField(message, ref i, wireType);
            }
        }

        return result;
    }

    private static void SkipField(byte[] message, ref int i, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(message, ref i);
                break;
            case 1:
                i += 8;
                break;
            case 2:
                int length = (int)ReadVarint(message, ref i);
                i += length;
                break;
            case 5:
                i += 4;
                break;
            default:
                throw new InvalidOperationException($"unexpected wire type {wireType}");
        }
    }

    private static (int Field, int WireType) ReadTag(byte[] message, ref int i)
    {
        ulong tag = ReadVarint(message, ref i);
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    private static ulong ReadVarint(byte[] message, ref int i)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = message[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
