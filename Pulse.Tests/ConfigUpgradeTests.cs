using System.Text.Json;
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

    /// <summary>The Stratum timings block arrived after the Attribution one, so a file written by the
    /// release before it has the one and not the other. Read off the real config object, not a copy
    /// of its JSON: a block the class did not declare would never be added to anybody's file. The
    /// framework's serializer stands in for the game's, which needs a Newtonsoft this project does not
    /// carry: both write a plain class's properties under their own names.</summary>
    [Fact]
    public void Compare_Reports_TheStratumTimingsBlock_AsMissing_FromAFileThatPredatesIt()
    {
        const string file = """
            {
              "Enabled": true,
              "Bind": "127.0.0.1",
              "Port": 9464,
              "RuntimeMetrics": true,
              "ChunksRefreshSeconds": 30,
              "Attribution": { "Enabled": true, "BurstTicks": 30, "IntervalSeconds": 10 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, JsonSerializer.Serialize(new PulseConfig()));

        Assert.Equal(["StratumTimings"], diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    [Fact]
    public void Compare_Reports_TheKeysAStratumTimingsBlockPredates_ByTheirPath()
    {
        const string file = """
            {
              "Enabled": true,
              "Bind": "127.0.0.1",
              "Port": 9464,
              "RuntimeMetrics": true,
              "ChunksRefreshSeconds": 30,
              "Attribution": { "Enabled": true, "BurstTicks": 30, "IntervalSeconds": 10 },
              "StratumTimings": { "Enabled": true }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, JsonSerializer.Serialize(new PulseConfig()));

        Assert.Equal(["StratumTimings.BurstTicks", "StratumTimings.IntervalSeconds"], diff.Missing);
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
    /// report it. Never the values themselves: this file is linked into Pulse.Otlp unchanged, and
    /// the same duplicate-key warning fires there for a config carrying header credentials.</summary>
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
        Assert.Contains("\"Port\"", duplicate);
        Assert.Contains("wins over", duplicate);
        Assert.DoesNotContain("9100", duplicate);
        Assert.DoesNotContain("9464", duplicate);
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

    /// <summary>The release review's still-false variant of the duplicate-key report: the same
    /// block written twice under different casing, each spelling setting a different field.
    /// Newtonsoft does not pick one spelling as a "winner" here, it merges both into the one bound
    /// object (Enabled from "Attribution", BurstTicks from "attribution"), so neither the
    /// duplicate-key line nor the added-keys line may claim otherwise: only IntervalSeconds, which
    /// neither spelling set, is genuinely missing.</summary>
    [Fact]
    public void Compare_Merges_ABlockDuplicatedUnderTwoSpellings_InsteadOfPickingOneAsTheWinner()
    {
        const string file = """{"Attribution": {"Enabled": true}, "attribution": {"BurstTicks": 7}}""";
        const string config = """
            {
              "Attribution": { "Enabled": true, "BurstTicks": 7, "IntervalSeconds": 10 }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        // Not Attribution.Enabled: the admin's own "Attribution" spelling set it, and the merge
        // must see that even though "attribution" is the spelling written last.
        Assert.Equal(["Attribution.IntervalSeconds"], diff.Missing);
        Assert.Empty(diff.Unknown);

        string duplicate = Assert.Single(diff.Duplicated);
        Assert.Contains("\"Attribution\"", duplicate);
        Assert.Contains("\"attribution\"", duplicate);
        Assert.DoesNotContain("wins over", duplicate);
        Assert.DoesNotContain("true", duplicate);
        Assert.DoesNotContain("7", duplicate);
    }

    /// <summary>The security half of the same defect: Pulse.Otlp links this exact file in, and a
    /// header block duplicated under two casings must never put either header's value in the log,
    /// nor claim one replaces the other. Newtonsoft keeps every one of a dictionary's colliding
    /// keys (unlike a class property, where the last spelling read wins), so both survive into
    /// config here, once under each casing; Walk must report the collision once, not once per
    /// surviving entry.</summary>
    [Fact]
    public void Compare_Reports_ADictionaryKeyCollision_Once_WithNoValueAndNoWinner()
    {
        const string file = """
            {
              "Headers": { "authorization": "Basic secret-a", "Authorization": "Basic secret-b" }
            }
            """;
        const string config = """
            {
              "Headers": { "authorization": "Basic secret-a", "Authorization": "Basic secret-b" }
            }
            """;

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Unknown);
        string duplicate = Assert.Single(diff.Duplicated);
        Assert.StartsWith("Headers.", duplicate);
        Assert.Contains("\"authorization\"", duplicate);
        Assert.Contains("\"Authorization\"", duplicate);
        Assert.DoesNotContain("wins over", duplicate);
        Assert.DoesNotContain("secret", duplicate);
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

    /// <summary>The one value Compare does look at. A key the file carries empty, null, an empty
    /// string or whitespace, for which the loaded config now holds a real string: nothing but a mod
    /// filling in a value of its own after loading can make the two differ that way, and only the
    /// rewrite puts the new value into the file, so the key counts as missing even though the file
    /// has it. (The OTLP mod's generated service instance id is the case this exists for: lost on
    /// the next restart if the file were left alone.)</summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void Compare_Reports_ABlankKey_TheConfigHasSinceFilledIn(string blank)
    {
        string file = "{ \"Enabled\": true, \"ServiceInstanceId\": " + blank + " }";
        const string config = """{ "Enabled": true, "ServiceInstanceId": "3f2a8c1e" }""";

        ConfigDiff diff = ConfigUpgrade.Compare(file, config);

        Assert.Equal(["ServiceInstanceId"], diff.Missing);
        Assert.Empty(diff.Unknown);
    }

    /// <summary>The same key one block down is named by its path, like any other missing key.</summary>
    [Fact]
    public void Compare_Reports_ABlankKeyInsideABlock_ByItsPath()
    {
        ConfigDiff diff = ConfigUpgrade.Compare(
            """{ "Attribution": { "Enabled": true, "Label": "" } }""",
            """{ "Attribution": { "Enabled": true, "Label": "x" } }""");

        Assert.Equal(["Attribution.Label"], diff.Missing);
    }

    /// <summary>A key spelled twice under different casing: Newtonsoft keeps the last spelling's
    /// value for a scalar, so the last one decides whether the key is blank. "Id" holding a value
    /// and "id" after it holding none loads blank, and the fill the config now holds has to be
    /// written; the other way round loads the value, and there is nothing to write.</summary>
    [Theory]
    [InlineData("""{"Id":"abc","id":""}""", true)]
    [InlineData("""{"Id":"","id":"abc"}""", false)]
    public void Compare_Takes_TheLastSpellingOfADuplicatedKey_AsTheOneThatDecidesWhetherItIsBlank(
        string file, bool reported)
    {
        ConfigDiff diff = ConfigUpgrade.Compare(file, """{"Id":"g"}""");

        Assert.Equal(reported ? ["Id"] : [], diff.Missing);
    }

    /// <summary>Blank on both sides is the admin's own empty value, loaded as written and left
    /// alone: a blank the mod did not fill in is no reason to rewrite their file. A value on disk
    /// stays theirs whatever the config holds, which is what <see cref="Compare_Ignores_Values"/>
    /// already says about every other value.</summary>
    [Theory]
    [InlineData("\"\"", "\"\"")]
    [InlineData("\"\"", "null")]
    [InlineData("null", "null")]
    [InlineData("\"admin's own\"", "\"3f2a8c1e\"")]
    [InlineData("\"admin's own\"", "\"\"")]
    public void Compare_Leaves_AKeyAlone_UnlessTheFileIsBlankAndTheConfigIsNot(string onDisk, string loaded)
    {
        ConfigDiff diff = ConfigUpgrade.Compare(
            "{ \"Label\": " + onDisk + " }", "{ \"Label\": " + loaded + " }");

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

    /// <summary>The line is written for a key the file held empty, which the mod filled in rather
    /// than added, and for a value that is no default (the OTLP mod's generated service instance
    /// id): it names what was written, and calls neither an addition nor a default.</summary>
    [Fact]
    public void AddedKeysTemplates_DoNotCallAWrittenKeyAnAddedOne_OrItsValueADefault()
    {
        foreach (ConfigDiff diff in new ConfigDiff[]
        {
            new(Missing: ["ServiceInstanceId"], Unknown: [], Duplicated: []),
            new(Missing: ["ServiceInstanceId"], Unknown: [], Duplicated: ["Port: \"port\" wins over \"Port\""]),
        })
        {
            string template = ConfigUpgrade.AddedKeysTemplate(diff);

            Assert.Contains("wrote these keys into {1}: {2}.", template);
            Assert.DoesNotContain("added", template);
            Assert.DoesNotContain("default", template);
        }
    }

    /// <summary>The release review's still-false variant of the added-keys line: the plain "kept
    /// as it was" claim is only true when the rewrite that added a missing key did not also have
    /// to collapse a duplicate spelling.</summary>
    [Fact]
    public void AddedKeysTemplate_Claims_EverythingWasKept_WhenCompareFoundNoDuplicate()
    {
        ConfigDiff diff = new(Missing: ["Attribution"], Unknown: [], Duplicated: []);

        Assert.Contains("kept as it was", ConfigUpgrade.AddedKeysTemplate(diff));
    }

    /// <summary>Duplicate and rewrite together: the same rewrite that wrote the missing key also
    /// collapsed "Port"/"port" down to one spelling, so the plain "kept as it was" line would be
    /// false here.</summary>
    [Fact]
    public void AddedKeysTemplate_AdmitsTheRewriteAlsoDroppedADuplicate_WhenCompareFoundOne()
    {
        ConfigDiff diff = new(
            Missing: ["Attribution"], Unknown: [], Duplicated: ["Port: \"port\" wins over \"Port\""]);

        string template = ConfigUpgrade.AddedKeysTemplate(diff);
        Assert.DoesNotContain("kept as it was", template);
        Assert.Contains("dropped", template);
    }

    /// <summary>Duplicate without a rewrite: nothing collapsed "Port"/"port" for the admin, so the
    /// warning still has to ask them to do it themselves.</summary>
    [Fact]
    public void DuplicateKeysTemplate_AsksToRemoveThem_WhenNothingElseRewroteTheFile()
    {
        ConfigDiff diff = new(Missing: [], Unknown: [], Duplicated: ["Port: \"port\" wins over \"Port\""]);

        Assert.Contains("Remove the extra spellings", ConfigUpgrade.DuplicateKeysTemplate(diff));
    }

    /// <summary>Duplicate and rewrite together, the other side of the same release-review defect:
    /// the rewrite already dropped "Port"/"port" down to one spelling, so telling the admin to
    /// remove it themselves would be false.</summary>
    [Fact]
    public void DuplicateKeysTemplate_SaysTheRewriteAboveAlreadyDroppedThem_WhenOneHappened()
    {
        ConfigDiff diff = new(
            Missing: ["Attribution"], Unknown: [], Duplicated: ["Port: \"port\" wins over \"Port\""]);

        string template = ConfigUpgrade.DuplicateKeysTemplate(diff);
        Assert.DoesNotContain("Remove the extra spellings", template);
        Assert.Contains("already dropped", template);
    }
}
