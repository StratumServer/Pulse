using System.Diagnostics.CodeAnalysis;
using OpenTelemetry.Exporter;

namespace Pulse.Otlp;

/// <summary>Turns the config file into what the OTLP exporter actually wants. Every method here is
/// pure, which is the point: the wiring in the mod system is trivial and this is where the sharp
/// edges of the exporter's own option handling are dealt with.</summary>
public static class OtlpOptions
{
    /// <summary>Shortest export interval accepted, in seconds. The exporter polls every observable
    /// instrument on each export, and a PeriodicExportingMetricReader rejects a zero interval
    /// outright, so a config typo cannot be allowed through.</summary>
    public const int MinimumIntervalSeconds = 5;

    private const string MetricsPath = "/v1/metrics";

    /// <summary>Export interval in milliseconds, floored.</summary>
    public static int IntervalMilliseconds(int intervalSeconds)
        => Math.Max(MinimumIntervalSeconds, intervalSeconds) * 1000;

    /// <summary>Parses the OTLP specification's two protocol names. Returns false for anything
    /// else, having still produced http/protobuf: an unreadable protocol name is a reason to warn
    /// and carry on, not a reason to leave a server without export.</summary>
    public static bool TryParseProtocol(string? value, out OtlpExportProtocol protocol)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "grpc":
                protocol = OtlpExportProtocol.Grpc;
                return true;
            case "http/protobuf":
                protocol = OtlpExportProtocol.HttpProtobuf;
                return true;
            default:
                protocol = OtlpExportProtocol.HttpProtobuf;
                return false;
        }
    }

    /// <summary>Resolves the endpoint the exporter should be handed, signal path included where the
    /// exporter will not add one itself.</summary>
    /// <remarks>Setting <c>OtlpExporterOptions.Endpoint</c> clears the exporter's internal
    /// AppendSignalPathToEndpoint flag, so an endpoint set from code is used verbatim for
    /// http/protobuf and a bare "http://host:4318" would POST to the collector's root. grpc is the
    /// other way round: the exporter appends its service path unconditionally, so the base
    /// endpoint has to stay bare. Verified in OtlpExportClient's constructor, 1.18.0.</remarks>
    public static bool TryResolveEndpoint(
        string? endpoint, OtlpExportProtocol protocol, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (protocol == OtlpExportProtocol.Grpc)
        {
            uri = parsed;
            return true;
        }

        string text = parsed.AbsoluteUri.TrimEnd('/');
        uri = new Uri(text.EndsWith(MetricsPath, StringComparison.OrdinalIgnoreCase)
            ? text
            : text + MetricsPath);
        return true;
    }

    /// <summary>Renders the header dictionary into the single string the exporter parses, which is
    /// the specification's "k=v,k2=v2" with percent-encoded values.</summary>
    /// <remarks>The exporter unescapes the whole string before splitting it, so encoding is not
    /// cosmetic: a value carrying a literal '%' would otherwise be mangled by that unescape. It
    /// also means a literal comma cannot survive the round trip whatever we do here, since the
    /// split happens after the unescape. No auth scheme in the wild puts a comma in a token, and
    /// the alternative is a header format of our own that the exporter would not read.</remarks>
    public static string RenderHeaders(IDictionary<string, string>? headers)
    {
        if (headers == null)
        {
            return string.Empty;
        }

        return string.Join(
            ",",
            headers
                .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                .Select(h => Uri.EscapeDataString(h.Key.Trim()) + "=" + Uri.EscapeDataString(h.Value ?? string.Empty)));
    }
}
