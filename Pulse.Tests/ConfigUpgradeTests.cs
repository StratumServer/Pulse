using Xunit;

namespace Pulse.Tests;

/// <summary>The comparison an upgrade turns on. Both sides are JSON text here, which is what the
/// two mods hand it: the file as the admin left it, and the config object as it was loaded.</summary>
public class ConfigUpgradeTests
{
    /// <summary>A 0.1.0 file against a 0.2.0 config: the whole Attribution block arrived in the
    /// release the admin is upgrading to.</summary>
    private const string OldFile = """
        {
          "Enabled": true,
          "Bind": "127.0.0.1",
          "Port": 9464,
          "RuntimeMetrics": true,
          "ChunksRefreshSeconds": 30
        }
        """;

    private const string CurrentConfig = """
        {
          "Enabled": true,
          "Bind": "127.0.0.1",
          "Port": 9464,
          "RuntimeMetrics": true,
          "ChunksRefreshSeconds": 30,
          "Attribution": { "Enabled": false, "BurstTicks": 30, "IntervalSeconds": 10 }
        }
        """;

    [Fact]
    public void Compare_Reports_ABlockTheFilePredates()
    {
        ConfigDiff diff = ConfigUpgrade.Compare(OldFile, CurrentConfig);

        // The block by its own name, not its three children: the admin never had any of them, and
        // naming them would only pad the log line.
        Assert.Equal(["Attribution"], diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>The recursive half. A file that has the block but predates one key inside it gets
    /// that one key named, and nothing else.</summary>
    [Fact]
    public void Compare_Reports_AKeyMissingFromAPresentBlock_ByItsPath()
    {
        const string file = """
            {
              "Enabled": true,
              "Attribution": { "Enabled": true, "IntervalSeconds": 10 }
            }
            """;
        const string config = """
            {
              "Enabled": true,
              "Attribution": { "Enabled": true, "BurstTicks": 30, "IntervalSeconds": 10 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Equal(["Attribution.BurstTicks"], diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>"Burstticks" is not a typo Compare should catch: Newtonsoft binds LoadModConfig's
    /// keys case-insensitively, so this file's value reaches BurstTicks exactly as the admin wrote
    /// it. Only "Colour", which matches no key at all regardless of casing, is genuinely
    /// unknown.</summary>
    [Fact]
    public void Compare_Reports_AKeyTheConfigDoesNotKnow_AtEitherDepth()
    {
        const string file = """
            {
              "Enabled": true,
              "Colour": "green",
              "Attribution": { "Enabled": true, "Burstticks": 5 }
            }
            """;
        const string config = """
            {
              "Enabled": true,
              "Attribution": { "Enabled": true, "BurstTicks": 30 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Equal(["Colour"], diff.Unknown);
        Assert.Empty(diff.Duplicated);
    }

    /// <summary>The probe from the release review: a file spelled entirely in whatever casing an
    /// admin's editor produced still carries every one of its own values, because Newtonsoft binds
    /// LoadModConfig's keys case-insensitively. None of that is missing, dropped or a typo.</summary>
    [Fact]
    public void Compare_Ignores_Casing_WhenAKeyIsSpelledDifferently()
    {
        const string file = """
            {
              "port": 9100,
              "bind": "0.0.0.0",
              "Attribution": { "burstticks": 5 }
            }
            """;
        const string config = """
            {
              "Port": 9464,
              "Bind": "127.0.0.1",
              "Attribution": { "BurstTicks": 30 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
        Assert.Empty(diff.Duplicated);
    }

    /// <summary>The other half of the probe: the same key written twice, cased differently.
    /// Newtonsoft applies whichever spelling comes last while reading the document, so that is
    /// the value in effect, not "port" doing nothing the way a case-sensitive diff used to
    /// report it.</summary>
    [Fact]
    public void Compare_Reports_ADuplicateKey_AndWhichSpellingWon()
    {
        const string file = """{"Port": 9464, "port": 9100}""";
        const string config = """{"Port": 9100}""";

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
        string duplicate = Assert.Single(diff.Duplicated);
        Assert.Contains("\"port\"", duplicate);
        Assert.Contains("9100", duplicate);
        Assert.Contains("\"Port\"", duplicate);
        Assert.Contains("9464", duplicate);
    }

    /// <summary>The same duplicate-key rule applies inside a nested block, and it must not also be
    /// misreported as a key the config does not know.</summary>
    [Fact]
    public void Compare_Reports_ADuplicateKey_InsideANestedBlock()
    {
        const string file = """{"Attribution": {"BurstTicks": 30, "burstticks": 5}}""";
        const string config = """{"Attribution": {"BurstTicks": 5}}""";

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
        string duplicate = Assert.Single(diff.Duplicated);
        Assert.StartsWith("Attribution.BurstTicks:", duplicate);
        Assert.Contains("\"burstticks\"", duplicate);
    }

    /// <summary>Key order and whitespace are the serializer's business, not the admin's, and a
    /// reordered file must not read as an upgrade.</summary>
    [Fact]
    public void Compare_Ignores_KeyOrderAndFormatting()
    {
        const string file = """{"Port":9464,"Bind":"127.0.0.1","Attribution":{"BurstTicks":30,"Enabled":false}}""";
        const string config = """
            {
              "Bind": "127.0.0.1",
              "Port": 9464,
              "Attribution": {
                "Enabled": false,
                "BurstTicks": 30
              }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>The admin's own settings are the whole point of the exercise: a file that differs
    /// from the defaults in every value is complete, not out of date.</summary>
    [Fact]
    public void Compare_Ignores_Values()
    {
        const string file = """
            {
              "Enabled": false,
              "Bind": "0.0.0.0",
              "Port": 19464,
              "Attribution": { "Enabled": true, "BurstTicks": 5 }
            }
            """;
        const string config = """
            {
              "Enabled": true,
              "Bind": "127.0.0.1",
              "Port": 9464,
              "Attribution": { "Enabled": false, "BurstTicks": 30 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>A block that is not a block on disk stops the walk there. Whatever the admin put
    /// in its place is theirs, and the rewrite replaces the lot with one default block.</summary>
    [Fact]
    public void Compare_Stops_AtAKeyThatIsAnObjectOnOnlyOneSide()
    {
        ConfigDiff diff = ConfigUpgrade.Compare("""{"Attribution": true}""", """{"Attribution": {"Enabled": false}}""");

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>Newtonsoft loads a file with comments and trailing commas without complaint, so a
    /// server running on one must not silently stop getting new keys.</summary>
    [Fact]
    public void Compare_Reads_AFileWithCommentsAndATrailingComma()
    {
        const string file = """
            {
              // the port the panel scrapes
              "Port": 9464,
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, """{"Port": 9464, "Bind": "127.0.0.1"}""");

        Assert.Equal(["Bind"], diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>Nothing missing means nothing to write, and text that is not a JSON object at all
    /// has to land there too: rewriting a file this cannot read would destroy it.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void Compare_Reports_Nothing_ForTextThatIsNotAJsonObject(string file)
    {
        ConfigDiff diff = ConfigUpgrade.Compare(file, """{"Port": 9464}""");

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
    }
}
