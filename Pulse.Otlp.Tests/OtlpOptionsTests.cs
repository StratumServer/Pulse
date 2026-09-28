using OpenTelemetry.Exporter;
using Xunit;

namespace Pulse.Otlp.Tests;

public class OtlpOptionsTests
{
    [Theory]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    [InlineData("  grpc  ", OtlpExportProtocol.Grpc)]
    [InlineData("HTTP/protobuf", OtlpExportProtocol.HttpProtobuf)]
    public void TryParseProtocol_Reads_TheTwoSpecifiedNames(string value, OtlpExportProtocol expected)
    {
        Assert.True(OtlpOptions.TryParseProtocol(value, out OtlpExportProtocol protocol));
        Assert.Equal(expected, protocol);
    }

    [Theory]
    [InlineData("thrift")]
    [InlineData("http")]
    [InlineData("http/json")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseProtocol_Falls_BackToHttpProtobuf_OnAnythingElse(string? value)
    {
        Assert.False(OtlpOptions.TryParseProtocol(value, out OtlpExportProtocol protocol));

        // The false is the caller's cue to warn. The protocol is still usable, because a typo in a
        // config file should cost a log line, not the whole export.
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, protocol);
    }

    [Theory]
    [InlineData("http://localhost:4318", "http://localhost:4318/v1/metrics")]
    [InlineData("http://localhost:4318/", "http://localhost:4318/v1/metrics")]
    [InlineData("https://otlp.example.com/otlp", "https://otlp.example.com/otlp/v1/metrics")]
    [InlineData("http://localhost:4318/v1/metrics", "http://localhost:4318/v1/metrics")]
    [InlineData("http://localhost:4318/v1/metrics/", "http://localhost:4318/v1/metrics")]
    // Starts with, but does not end with, the metrics path: distinct from the "already has it"
    // case above, and the one shape that tells an EndsWith check apart from a StartsWith one.
    [InlineData("http://localhost:4318/v1/metrics/extra", "http://localhost:4318/v1/metrics/extra/v1/metrics")]
    public void TryResolveEndpoint_Appends_TheMetricsPath_ForHttpProtobuf(string endpoint, string expected)
    {
        Assert.True(OtlpOptions.TryResolveEndpoint(endpoint, OtlpExportProtocol.HttpProtobuf, out Uri? uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    /// <summary>A backend that authenticates through a signed URL puts its own secret in the query
    /// string. Appending the signal path to the endpoint as a whole, rather than to its path alone,
    /// would land "/v1/metrics" after that query instead of before it.</summary>
    [Fact]
    public void TryResolveEndpoint_AppendsTheMetricsPath_BeforeAnExistingQueryString()
    {
        Assert.True(OtlpOptions.TryResolveEndpoint(
            "https://host/otlp?key=abc", OtlpExportProtocol.HttpProtobuf, out Uri? uri));
        Assert.Equal("https://host/otlp/v1/metrics?key=abc", uri.AbsoluteUri);
    }

    [Fact]
    public void TryResolveEndpoint_Leaves_AGrpcEndpointBare()
    {
        // The exporter appends the grpc service path itself, unconditionally. Appending anything
        // here would produce a path no collector serves.
        Assert.True(OtlpOptions.TryResolveEndpoint("http://localhost:4317", OtlpExportProtocol.Grpc, out Uri? uri));
        Assert.Equal("http://localhost:4317/", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("localhost:4318")]
    [InlineData("not a url")]
    [InlineData("ftp://localhost:4318")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveEndpoint_Rejects_WhatIsNotAnHttpEndpoint(string? endpoint)
    {
        Assert.False(OtlpOptions.TryResolveEndpoint(endpoint, OtlpExportProtocol.HttpProtobuf, out Uri? uri));
        Assert.Null(uri);
    }

    [Theory]
    [InlineData("pulse-atlas-test", "pulse-atlas-test")]
    [InlineData("  my-server  ", "my-server")]
    public void ResolveServiceName_Keeps_AConfiguredName(string configured, string expected)
        => Assert.Equal(expected, OtlpOptions.ResolveServiceName(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveServiceName_Falls_BackToTheDefault_OnBlank(string? configured)
        => Assert.Equal(OtlpOptions.DefaultServiceName, OtlpOptions.ResolveServiceName(configured));

    [Fact]
    public void RenderHeaders_Writes_Nothing_ForNoHeaders()
    {
        Assert.Equal(string.Empty, OtlpOptions.RenderHeaders(null));
        Assert.Equal(string.Empty, OtlpOptions.RenderHeaders(new Dictionary<string, string>()));
    }

    [Fact]
    public void LoggableEndpoint_NeverIncludes_UserinfoOrAQueryString()
    {
        Uri endpoint = new("https://user:s3cret@host:8443/otlp/path?api_key=alsosecret");

        string loggable = OtlpOptions.LoggableEndpoint(endpoint);

        Assert.DoesNotContain("user", loggable);
        Assert.DoesNotContain("s3cret", loggable);
        Assert.DoesNotContain("alsosecret", loggable);
        Assert.DoesNotContain("api_key", loggable);
        Assert.StartsWith("https://host:8443/otlp/path", loggable, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggableEndpoint_SaysAQueryStringExists_WithoutIncludingIt()
    {
        Assert.Equal(
            "https://host/otlp",
            OtlpOptions.LoggableEndpoint(new Uri("https://host/otlp")));
        Assert.Equal(
            "https://host/otlp (query string kept, not logged)",
            OtlpOptions.LoggableEndpoint(new Uri("https://host/otlp?key=abc")));
    }

    /// <summary>Newtonsoft accepts "Headers": null, and a null value for any one key inside it,
    /// despite PulseOtlpConfig.Headers's own non-nullable C# type; a null value used to make
    /// ExportFailureLog.Redact throw at secret.Length, silently losing every failure line for the
    /// rest of the session.</summary>
    [Fact]
    public void SecretValues_IsNullSafe_ForANullHeadersDictionaryOrANullOrEmptyValueWithinIt()
    {
        Assert.Empty(OtlpOptions.SecretValues(null));

        Dictionary<string, string> headers = new()
        {
            ["x-api-key"] = null!,
            ["x-empty"] = string.Empty,
            ["Authorization"] = "Bearer abc",
        };
        Assert.Equal(["Bearer abc"], OtlpOptions.SecretValues(headers));
    }

    [Fact]
    public void RenderHeaders_Joins_PairsWithCommas()
    {
        string rendered = OtlpOptions.RenderHeaders(new Dictionary<string, string>
        {
            ["x-scope-orgid"] = "tenant1",
            ["x-honeycomb-team"] = "abc123",
        });

        Assert.Equal("x-scope-orgid=tenant1,x-honeycomb-team=abc123", rendered);
    }

    [Fact]
    public void RenderHeaders_Skips_AnEntryWithNoName()
    {
        string rendered = OtlpOptions.RenderHeaders(new Dictionary<string, string>
        {
            ["  "] = "orphan",
            ["Authorization"] = "Bearer t",
        });

        Assert.Equal("Authorization=Bearer%20t", rendered);
    }

    /// <summary>Newtonsoft accepts a null value for any one key inside Headers despite
    /// PulseOtlpConfig.Headers's own non-nullable C# type (see SecretValues's own null-safety
    /// test); RenderHeaders defaults that same null to an empty string rather than passing it into
    /// Uri.EscapeDataString, which throws on a null argument.</summary>
    [Fact]
    public void RenderHeaders_Defaults_ANullValue_ToAnEmptyString()
    {
        string rendered = OtlpOptions.RenderHeaders(new Dictionary<string, string> { ["x-api-key"] = null! });

        Assert.Equal("x-api-key=", rendered);
    }

    /// <summary>The characters that need care, each with the reason it needs it.</summary>
    [Theory]
    // A Grafana Cloud token is base64, so it carries padding '=' and a space after the scheme.
    [InlineData("Authorization", "Basic MTIzNDU2OnRva2Vu==")]
    // A percent would be eaten by the exporter's unescape if it were written through raw.
    [InlineData("Authorization", "Basic 100%pure")]
    // A '+' and a '/' are in the base64 alphabet and are both reserved in a URI.
    [InlineData("x-api-key", "a+b/c=")]
    // Nothing exotic, to prove the encoding does not disturb the ordinary case.
    [InlineData("x-honeycomb-team", "abc123")]
    public void RenderHeaders_RoundTrips_ThroughTheExportersOwnParser(string name, string value)
    {
        string rendered = OtlpOptions.RenderHeaders(new Dictionary<string, string> { [name] = value });

        Dictionary<string, string> parsed = ParseTheWayTheExporterDoes(rendered);

        Assert.Equal(value, Assert.Contains(name, parsed));
    }

    [Fact]
    public void RenderHeaders_RoundTrips_SeveralHeadersAtOnce()
    {
        Dictionary<string, string> headers = new()
        {
            ["Authorization"] = "Basic dXNlcjpwYXNz==",
            ["x-scope-orgid"] = "tenant one",
        };

        Dictionary<string, string> parsed = ParseTheWayTheExporterDoes(OtlpOptions.RenderHeaders(headers));

        Assert.Equal(headers, parsed);
    }

    /// <summary>The exact shape Newtonsoft produces for a Headers value typed as
    /// OTEL_EXPORTER_OTLP_HEADERS's "k=v,k2=v2" string instead of the config's own JSON object: the
    /// offending value, a real bearer token here, travels inside the message in double quotes.</summary>
    [Fact]
    public void RedactQuotedValues_Blanks_ADoubleQuotedValue_ButKeepsThePathLineAndPosition()
    {
        const string message =
            "Error converting value \"Authorization=Bearer abc123\" to type "
            + "'System.Collections.Generic.Dictionary`2[System.String,System.String]'. "
            + "Path 'Headers', line 4, position 42.";

        string redacted = OtlpOptions.RedactQuotedValues(message);

        Assert.DoesNotContain("abc123", redacted);
        Assert.Contains("<redacted>", redacted);
        Assert.Contains("Path 'Headers', line 4, position 42.", redacted);
    }

    [Fact]
    public void RedactQuotedValues_Leaves_AMessageWithNoDoubleQuotedValue_Unchanged()
    {
        const string message = "Invalid property identifier character: ,. Path 'Enabled', line 2, position 18.";

        Assert.Equal(message, OtlpOptions.RedactQuotedValues(message));
    }

    /// <summary>OpenTelemetry.Exporter.OtlpExporterOptionsExtensions.GetHeaders, 1.18.0, reproduced
    /// because it is internal to the exporter assembly. Unescaping the whole string before the
    /// split is the detail that dictates how RenderHeaders encodes.</summary>
    private static Dictionary<string, string> ParseTheWayTheExporterDoes(string rendered)
    {
        Dictionary<string, string> headers = [];
        if (rendered.Length == 0)
        {
            return headers;
        }

        foreach (string pair in Uri.UnescapeDataString(rendered).Split(','))
        {
            int split = pair.IndexOf('=');
            Assert.True(split >= 0, $"'{pair}' is not a header the exporter would accept");
            headers.Add(pair[..split].Trim(), pair[(split + 1)..].Trim());
        }

        return headers;
    }
}
