using System.Text.Json;
using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>What a server start does with <c>ServiceInstanceId</c>, played against the text of
/// pulse-otlp.json: the id it exports, and what the file says afterwards. The same pieces
/// StartServerSide uses decide it (the config class, <see cref="OtlpOptions.ResolveServiceInstanceId"/>
/// and <see cref="ConfigUpgrade.Compare"/>, the very comparison a live server's upgrade turns on);
/// only the engine calls around them, reading and writing the file, are replaced by plain
/// strings, which is also where the Atlas scenarios take over.</summary>
public class ServiceInstanceIdStartupTests
{
    /// <summary>A file 0.2.0 wrote: every key it knew, and none of the one that came after.</summary>
    private const string FileFromAnOlderVersion = """
        {
          "Enabled": true,
          "Endpoint": "http://localhost:4318",
          "Protocol": "http/protobuf",
          "Headers": {},
          "IntervalSeconds": 60,
          "IncludeRuntimeMetrics": true,
          "ServiceName": "my-server"
        }
        """;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void AnOlderFile_GetsAGeneratedId_WrittenOnce_AndKeptOnTheNextStart()
    {
        Start first = Run(FileFromAnOlderVersion);

        Assert.True(Guid.TryParse(first.ExportedId, out _));
        Assert.True(first.Rewrote);
        Assert.Equal(first.ExportedId, IdIn(first.File));

        // What the admin had is still theirs, only the new key is added.
        Assert.Equal("my-server", Read(first.File).ServiceName);
        Assert.Equal("http://localhost:4318", Read(first.File).Endpoint);

        Start second = Run(first.File);

        Assert.Equal(first.ExportedId, second.ExportedId);
        Assert.False(second.Rewrote);
        Assert.Equal(first.File, second.File);
    }

    /// <summary>The first boot's own file: the class defaults, blank key included, written by the
    /// mod after it filled the key in, so the very first start already holds a stable id.</summary>
    [Fact]
    public void AFreshDefaultConfig_IsWritten_WithItsGeneratedId()
    {
        PulseOtlpConfig config = new();
        config.ServiceInstanceId = OtlpOptions.ResolveServiceInstanceId(config.ServiceInstanceId);

        string file = JsonSerializer.Serialize(config);

        Assert.True(Guid.TryParse(IdIn(file), out _));
        Assert.False(Run(file).Rewrote);
    }

    /// <summary>The key can be there and empty (a template with the value left out, or an admin
    /// clearing it to get a new id): not a missing key, but just as much in need of the rewrite.</summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void ABlankKey_IsFilledWithAGeneratedId_WrittenOnce_AndKeptOnTheNextStart(string blank)
    {
        string file = FileFromAnOlderVersion.Replace(
            "\"ServiceName\": \"my-server\"", "\"ServiceName\": \"my-server\", \"ServiceInstanceId\": " + blank);

        Start first = Run(file);

        Assert.True(Guid.TryParse(first.ExportedId, out _));
        Assert.True(first.Rewrote);
        Assert.Equal(first.ExportedId, IdIn(first.File));

        Start second = Run(first.File);

        Assert.Equal(first.ExportedId, second.ExportedId);
        Assert.False(second.Rewrote);
    }

    /// <summary>Whatever the admin wrote is used as it is, and never touched on disk: not even to
    /// trim it, since a complete file is left alone, modification time included.</summary>
    [Fact]
    public void AnAdminsOwnId_IsUsedTrimmed_AndTheFileIsLeftAlone()
    {
        string file = FileFromAnOlderVersion.Replace(
            "\"ServiceName\": \"my-server\"", "\"ServiceName\": \"my-server\", \"ServiceInstanceId\": \"  survival-eu-1  \"");

        Start start = Run(file);

        Assert.Equal("survival-eu-1", start.ExportedId);
        Assert.False(start.Rewrote);
        Assert.Equal(file, start.File);
    }

    /// <summary>One start of the server on <paramref name="file"/>: the config the loader hands
    /// over, the blank key filled in, and the upgrade's comparison deciding whether the file is
    /// rewritten. Serialised with System.Text.Json here; the engine uses Newtonsoft, which names
    /// the same keys, and the comparison reads keys, not formatting.</summary>
    private static Start Run(string file)
    {
        PulseOtlpConfig config = Read(file);
        config.ServiceInstanceId = OtlpOptions.ResolveServiceInstanceId(config.ServiceInstanceId);

        string loaded = JsonSerializer.Serialize(config);
        bool rewrite = ConfigUpgrade.Compare(file, loaded).Missing.Count > 0;
        return new Start(config.ServiceInstanceId, rewrite, rewrite ? loaded : file);
    }

    private static PulseOtlpConfig Read(string file)
        => JsonSerializer.Deserialize<PulseOtlpConfig>(file, Options)!;

    private static string? IdIn(string file) => Read(file).ServiceInstanceId;

    private readonly record struct Start(string ExportedId, bool Rewrote, string File);
}
