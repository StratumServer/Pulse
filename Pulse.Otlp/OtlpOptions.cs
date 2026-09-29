using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using OpenTelemetry.Exporter;

namespace Pulse.Otlp;

/// <summary>Turns the config file into what the OTLP exporter actually wants. Every method here is
/// pure, which is the point: the wiring in the mod system is trivial and this is where the sharp
/// edges of the exporter's own option handling are dealt with.</summary>
/// <remarks>Partial: <see cref="QuotedValue"/> is a source-generated regex, which requires the
/// declaring type (and its method) to be partial.</remarks>
public static partial class OtlpOptions
{
    /// <summary>Shortest export interval accepted, in seconds. The exporter polls every observable
    /// instrument on each export, and a PeriodicExportingMetricReader rejects a zero interval
    /// outright, so a config typo cannot be allowed through.</summary>
    public const int MinimumIntervalSeconds = 5;

    /// <summary>Longest export interval accepted, in seconds: 24 hours, far past any real use of a
    /// push exporter and comfortably below the point (a little over 24.8 days) where multiplying by
    /// 1000 would overflow a 32-bit millisecond count. A config value above this used to overflow
    /// silently instead: 3,000,000 seconds, typed for "a lot less often", became a negative
    /// millisecond count the reader's own validation then rejected with an unhandled
    /// ArgumentOutOfRangeException.</summary>
    public const int MaximumIntervalSeconds = 86_400;

    private const string MetricsPath = "/v1/metrics";

    /// <summary>Fallback service.name when the config key is blank. Distinct from the SDK's own
    /// "unknown_service:&lt;processname&gt;" fallback, so a server exports as something a human
    /// would recognise even before anyone edits the config.</summary>
    public const string DefaultServiceName = "vintagestory";

    /// <summary>Export interval in milliseconds, clamped to <see cref="MinimumIntervalSeconds"/> and
    /// <see cref="MaximumIntervalSeconds"/> before the multiply, so neither end of a config typo can
    /// reach the reader unvalidated or overflow on the way there.</summary>
    public static int IntervalMilliseconds(int intervalSeconds)
        => Math.Clamp(intervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds) * 1000;

    /// <summary>Resolves the service.name to export, falling back to <see
    /// cref="DefaultServiceName"/> on a blank config value rather than exporting an empty resource
    /// attribute. Trimmed like the other string config values this class handles.</summary>
    public static string ResolveServiceName(string? configuredName)
        => string.IsNullOrWhiteSpace(configuredName) ? DefaultServiceName : configuredName.Trim();

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
    /// endpoint has to stay bare. Verified in OtlpExportClient's constructor, 1.19.1.</remarks>
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

        // Built from AbsolutePath through UriBuilder, not from AbsoluteUri with a string
        // concatenation: a configured endpoint can carry a query string (a backend that
        // authenticates through a signed URL, say), and appending the signal path to the whole
        // URI string would land it after the query instead of before it.
        string path = parsed.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith(MetricsPath, StringComparison.OrdinalIgnoreCase))
        {
            path += MetricsPath;
        }

        uri = new UriBuilder(parsed) { Path = path }.Uri;
        return true;
    }

    /// <summary>Everything about <paramref name="endpoint"/> that is safe to put in a log line at
    /// any level: scheme, host, port and path. A query string is never included, only noted as
    /// present, since a backend that authenticates through a signed URL keeps its own secret
    /// there.</summary>
    public static string LoggableEndpoint(Uri endpoint)
    {
        string safe = endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return endpoint.Query.Length > 0 ? safe + " (query string kept, not logged)" : safe;
    }

    /// <summary>Every secret <paramref name="endpoint"/> itself carries: its userinfo, user and
    /// password both where both are present, every one of its query parameter values, and a
    /// value-less query parameter's own name (a bare "?BareKeySecret777" carries the secret in
    /// the key, since there is no value to hold it). <see cref="LoggableEndpoint"/> already keeps
    /// all of this out of every log line Pulse writes on its own, but a 4xx body that echoes the
    /// request target back (a reverse proxy's own error page, say) would otherwise put it
    /// straight into the log through <see cref="ExportFailureLog"/>'s redaction of the backend's
    /// own words, which only ever knew about the configured header values until now. Both the
    /// escaped form (what <see cref="Uri.UserInfo"/> and <see cref="Uri.Query"/> return, and so
    /// what a raw request target on the wire, and any echo of it, actually carries) and the
    /// unescaped one are yielded: a real signed URL (an Azure SAS "sig", an AWS
    /// X-Amz-Credential or X-Amz-Security-Token) is base64 and always carries '+', '/' or '='
    /// escaped as %2B, %2F or %3D, so redacting only the unescaped form leaves the form that
    /// actually appears on the wire unmatched. Not floored or ordered here: <see
    /// cref="ExportFailureLog"/> applies <c>MinimumSecretLength</c> and longest-first ordering
    /// once every source's secrets are merged into one list, the same as it already does for a
    /// header value.</summary>
    public static IEnumerable<string> EndpointSecrets(Uri endpoint)
    {
        string userInfo = endpoint.UserInfo;
        if (userInfo.Length > 0)
        {
            foreach (string part in userInfo.Split(':', 2))
            {
                foreach (string secret in EscapedAndUnescaped(part))
                {
                    yield return secret;
                }
            }
        }

        string query = endpoint.Query;
        if (query.Length > 1) // more than just the leading '?'
        {
            foreach (string pair in query[1..].Split('&'))
            {
                int equals = pair.IndexOf('=');

                // A value-less parameter ("?BareKeySecret777") carries the secret in its own
                // name, since there is nothing after an '=' to hold it; otherwise only the
                // value is a candidate secret, never an ordinary parameter name like "token".
                string candidate = equals >= 0 ? pair[(equals + 1)..] : pair;
                foreach (string secret in EscapedAndUnescaped(candidate))
                {
                    yield return secret;
                }
            }
        }
    }

    /// <summary>A URL-escaped candidate exactly as it sits on the wire, plus its unescaped form
    /// when unescaping actually changes it. Yielding only one used to be the bug: a collector's
    /// echo of the raw request target carries whichever form was actually sent.</summary>
    private static IEnumerable<string> EscapedAndUnescaped(string candidate)
    {
        if (candidate.Length == 0)
        {
            yield break;
        }

        yield return candidate;

        string unescaped = Uri.UnescapeDataString(candidate);
        if (unescaped != candidate)
        {
            yield return unescaped;
        }
    }

    /// <summary>Whether <paramref name="headers"/> is safe to hand the exporter: no value carries a
    /// comma, and no two names collide once trimmed. Both shapes reach the exporter's own option
    /// validation otherwise and throw there instead of here: a comma cannot survive
    /// <see cref="RenderHeaders"/>'s round trip (see its own remarks) and corrupts the rendered
    /// string at whatever pair follows it, and two names equal after trimming both render to the
    /// same key, which the exporter's own header parser rejects as a duplicate. <paramref
    /// name="offendingHeader"/> is the trimmed name of the first header either check does not
    /// like, never its value, so the log line this drives can name what to fix without repeating a
    /// credential into it. An entry with no name is skipped, the same as <see cref="RenderHeaders"/>
    /// already skips one.</summary>
    public static bool TryValidateHeaders(
        IDictionary<string, string>? headers, [NotNullWhen(false)] out string? offendingHeader)
    {
        offendingHeader = null;
        if (headers == null)
        {
            return true;
        }

        HashSet<string> seenNames = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key))
            {
                continue;
            }

            string name = header.Key.Trim();
            if (!seenNames.Add(name) || (header.Value?.Contains(',') ?? false))
            {
                offendingHeader = name;
                return false;
            }
        }

        return true;
    }

    /// <summary>The header values ExportFailureLog should treat as secrets: null-safe against
    /// both a null Headers block and a null value inside it, either of which Newtonsoft accepts
    /// ("Headers": null, or an entry with a null value) despite the property's own non-nullable C#
    /// type. Blank values are dropped too, the same as RenderHeaders already drops a blank
    /// key.</summary>
    public static string[] SecretValues(IDictionary<string, string>? headers) =>
        headers?.Values.Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? [];

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

    /// <summary>Stands in for the message when redaction itself could not finish in time. Fixed and
    /// generic on purpose: the message that timed out is exactly the message most likely to be
    /// pathological, and letting it through unredacted "just this once" would defeat the whole
    /// point of redacting at all.</summary>
    private const string RedactionTimedOut = "<could not check this message for secrets in time; withheld>";

    /// <summary>A one second ceiling on matching a bounded, linear pattern against a log message:
    /// generous for the real case and small enough that a pathological message cannot hold a log
    /// call open. csharpsquid:S6444 requires an explicit timeout on every regex match.</summary>
    [GeneratedRegex("\"[^\"]*\"", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedValue();

    /// <summary>Blanks every double-quoted span in a parser's error message, keeping everything
    /// else. Newtonsoft quotes the offending value verbatim when a field is the wrong shape
    /// ("Error converting value "..." to type ..."), and a misconfigured Headers field is exactly
    /// where a real secret can end up quoted that way: the most likely mistake is typing the
    /// OTEL_EXPORTER_OTLP_HEADERS environment variable's comma-separated "k=v,k2=v2" shape into the
    /// config's own JSON object field. Path, line and position are reported in single quotes and
    /// are left alone.</summary>
    /// <remarks><see cref="QuotedValue"/> is <c>[^"]*</c> between two literal quotes: linear in the
    /// input length, with no ambiguous repetition for the backtracker to stall on, so the timeout
    /// is a defensive ceiling rather than something normal input is expected to reach. If it is
    /// ever reached anyway, <see cref="RedactionTimedOut"/> stands in for the whole message rather
    /// than the raw, unredacted text: on a timeout there is no way to know whether the very thing
    /// that made matching slow is also the secret being protected against.</remarks>
    public static string RedactQuotedValues(string message)
    {
        try
        {
            return QuotedValue().Replace(message, "<redacted>");
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactionTimedOut;
        }
    }
}
