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
    public void TryResolveEndpoint_Appends_TheMetricsPath_ForHttpProtobuf(string endpoint, string expected)
    {
        Assert.True(OtlpOptions.TryResolveEndpoint(endpoint, OtlpExportProtocol.HttpProtobuf, out Uri? uri));
        Assert.Equal(expected, uri.AbsoluteUri);
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

    [Fact]
    public void RenderHeaders_Writes_Nothing_ForNoHeaders()
    {
        Assert.Equal(string.Empty, OtlpOptions.RenderHeaders(null));
        Assert.Equal(string.Empty, OtlpOptions.RenderHeaders(new Dictionary<string, string>()));
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
