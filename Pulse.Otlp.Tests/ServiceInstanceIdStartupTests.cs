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
        Assert.True(first.RewriteAsked);
        Assert.Equal(first.ExportedId, IdIn(first.File));

        // What the admin had is still theirs, only the new key is added.
        Assert.Equal("my-server", Read(first.File).ServiceName);
        Assert.Equal("http://localhost:4318", Read(first.File).Endpoint);

        // What the mod reads back after the upgrade is the id it exports: no warning to give.
        Assert.True(PulseOtlpModSystem.FileHoldsInstanceId(() => Read(first.File), first.ExportedId));

        Start second = Run(first.File);

        Assert.Equal(first.ExportedId, second.ExportedId);
        Assert.False(second.RewriteAsked);
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
        Assert.False(Run(file).RewriteAsked);
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
        Assert.True(first.RewriteAsked);
        Assert.Equal(first.ExportedId, IdIn(first.File));

        Start second = Run(first.File);

        Assert.Equal(first.ExportedId, second.ExportedId);
        Assert.False(second.RewriteAsked);
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
        Assert.False(start.RewriteAsked);
        Assert.Equal(file, start.File);
    }

    /// <summary>A rewrite for another reason (a key the file predates) writes the admin's id back
    /// exactly as they wrote it, whitespace included: it is trimmed where it is used, never in the
    /// file.</summary>
    [Fact]
    public void AnAdminsOwnId_IsWrittenBackAsWritten_WhenTheFileIsRewrittenForAnotherKey()
    {
        const string file = """{ "Enabled": true, "ServiceName": "my-server", "ServiceInstanceId": "  survival-eu-1  " }""";

        Start start = Run(file);

        Assert.True(start.RewriteAsked);
        Assert.Equal("survival-eu-1", start.ExportedId);
        Assert.Equal("  survival-eu-1  ", IdIn(start.File));
    }

    /// <summary>A ModConfig folder mounted read-only, which the README names as a supported setup:
    /// the upgrade finds the key missing, asks for the rewrite and cannot write it, so the file
    /// keeps what it had. The id is lost at the end of the session, and reading the file back is
    /// how the mod finds that out and says so.</summary>
    [Fact]
    public void AnOlderFile_OnAReadOnlyFolder_StaysAsItWas_SoTheReadBackFindsNoId()
    {
        Start start = Run(FileFromAnOlderVersion, writable: false);

        Assert.True(Guid.TryParse(start.ExportedId, out _));
        Assert.True(start.RewriteAsked);
        Assert.Equal(FileFromAnOlderVersion, start.File);
        Assert.False(PulseOtlpModSystem.FileHoldsInstanceId(() => Read(start.File), start.ExportedId));
    }

    /// <summary>A file Newtonsoft reads and System.Text.Json does not (single quotes, unquoted
    /// keys): the upgrade's comparison parses it with the latter, finds nothing, asks for no
    /// rewrite and logs nothing. The generated id is never written, and again only the read-back
    /// finds out.</summary>
    [Fact]
    public void AFileOnlyNewtonsoftCanRead_IsNotRewritten_SoTheReadBackFindsNoId()
    {
        const string file = "{ 'Enabled': true, 'ServiceName': 'my-server' }";

        // What the engine's loader makes of that text: the keys it names, the class defaults for
        // the rest. The next start's loader makes the same of it again.
        PulseOtlpConfig loadedFromFile = new() { ServiceName = "my-server" };
        PulseOtlpConfig config = new() { ServiceName = "my-server" };
        OtlpOptions.FillBlankServiceInstanceId(config);
        string generated = config.ServiceInstanceId;

        bool rewriteAsked = ConfigUpgrade.Compare(file, JsonSerializer.Serialize(config)).Missing.Count > 0;

        Assert.False(rewriteAsked);
        Assert.False(PulseOtlpModSystem.FileHoldsInstanceId(() => loadedFromFile, generated));
    }

    /// <summary>One start of the server on <paramref name="file"/>: the config the loader hands
    /// over, a blank key filled in, and the upgrade's comparison deciding whether the file is
    /// rewritten; <paramref name="writable"/> false is a ModConfig folder that will not take the
    /// write, which leaves the old text in place. Serialised with System.Text.Json here; the engine
    /// uses Newtonsoft, which names the same keys, and the comparison reads keys, not
    /// formatting.</summary>
    private static Start Run(string file, bool writable = true)
    {
        PulseOtlpConfig config = Read(file);
        OtlpOptions.FillBlankServiceInstanceId(config);

        string loaded = JsonSerializer.Serialize(config);
        bool rewrite = ConfigUpgrade.Compare(file, loaded).Missing.Count > 0;
        return new Start(
            OtlpOptions.ResolveServiceInstanceId(config.ServiceInstanceId), rewrite,
            rewrite && writable ? loaded : file);
    }

    private static PulseOtlpConfig Read(string file)
        => JsonSerializer.Deserialize<PulseOtlpConfig>(file, Options)!;

    private static string? IdIn(string file) => Read(file).ServiceInstanceId;

    /// <summary>What a start came to: the id it exports, whether the upgrade's comparison asked for
    /// a rewrite, and what the file says afterwards.</summary>
    private readonly record struct Start(string ExportedId, bool RewriteAsked, string File);
}
