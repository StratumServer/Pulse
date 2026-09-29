using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Pulse;

/// <summary>Keys the config file on disk does not carry, keys it carries that the mod knows
/// nothing about, and keys it carries more than once under different casing. Dotted paths, so a
/// key inside a nested block reads <c>Attribution.BurstTicks</c>.</summary>
internal readonly record struct ConfigDiff(
    IReadOnlyList<string> Missing, IReadOnlyList<string> Unknown, IReadOnlyList<string> Duplicated);

/// <summary>Keeps the config file an admin already has in step with the keys a newer version of the
/// mod introduced.</summary>
/// <remarks>Both mods compile this from the one source file: Pulse.Otlp links it rather than
/// referencing Pulse.dll, because the two mods deliberately share no assembly.</remarks>
internal static class ConfigUpgrade
{
    private const string AddedKeys =
        "{0} added these keys to {1} with their defaults: {2}. Everything already in the file was "
        + "kept as it was.";

    private const string AddedKeysDroppedDuplicates =
        "{0} added these keys to {1} with their defaults: {2}. The same rewrite also dropped the "
        + "duplicate keys below to one spelling each.";

    private const string DroppedKeys =
        "{0} does not know these keys in {1}, and rewriting the file has just dropped them: {2}. "
        + "Check them for typos.";

    private const string IgnoredKeys =
        "{0} does not know these keys in {1}: {2}. They do nothing; check them for typos.";

    private const string DuplicateKeys =
        "{0} found the same key written more than once, cased differently, in {1}: {2}. Remove "
        + "the extra spellings to stop this warning.";

    private const string DuplicateKeysResolved =
        "{0} found the same key written more than once, cased differently, in {1}: {2}. The "
        + "rewrite above already dropped the extra spellings.";

    private const string UpgradeFailed =
        "{0} could not bring {1} up to date ({2}). The server runs on the values the file does "
        + "have, with defaults for the rest.";

    /// <summary>Newtonsoft accepts both when it loads the config, so a file the game read happily
    /// must not be one this refuses to look at.</summary>
    private static readonly JsonDocumentOptions Lenient =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Which added-keys line is true: the plain one, or the one admitting that the same
    /// rewrite also collapsed a duplicate spelling, when <see cref="Compare"/> found one alongside
    /// the missing keys that triggered the rewrite. Returns the template, not a line with the
    /// missing keys already substituted into it: those names come straight off the admin's own
    /// file, and formatting them in here rather than leaving them as <c>Logger.Notification</c>'s
    /// own args would let a key spelled with a literal brace reach <c>string.Format</c> twice.
    /// </summary>
    internal static string AddedKeysTemplate(ConfigDiff diff) =>
        diff.Duplicated.Count > 0 ? AddedKeysDroppedDuplicates : AddedKeys;

    /// <summary>Which duplicate-key line is true: an admin only has to remove the extra spelling by
    /// hand when nothing else rewrote the file for them. When <see cref="Compare"/> also found a
    /// missing key, <see cref="Upgrade{T}"/>'s own rewrite has already collapsed the duplicate
    /// down to one spelling by the time this warning is logged.</summary>
    internal static string DuplicateKeysTemplate(ConfigDiff diff) =>
        diff.Missing.Count > 0 ? DuplicateKeysResolved : DuplicateKeys;

    /// <summary>Adds whatever keys a newer version of the mod introduced to the config file the
    /// admin already has, and says in the log what changed.</summary>
    /// <remarks>The file is rewritten from <paramref name="config"/>, which the loader filled with
    /// defaults wherever the file was silent, so the write only ever adds: every value the admin
    /// set is already in the object. It happens solely when a key is missing, because a complete
    /// file must not be touched at all, not even its modification time, on a host that mounts
    /// ModConfig read-only or tracks it. The same rewrite is what drops a key the config does not
    /// know, which is why an unknown key is worth a warning rather than silence.
    /// <para>Nothing in here may take a server down over a config file, so a read or a write that
    /// fails is one warning and then the server carries on unchanged.</para>
    /// <para>Excluded from coverage for the same reason <c>PulseModSystem</c>,
    /// <c>AttributionMetrics.Server.cs</c> and the two probes are: this is the ordinary
    /// <see cref="ICoreServerAPI"/> wiring around <see cref="Compare"/>, the part a unit test
    /// cannot reach without a live server, and it is what the Atlas scenarios in
    /// <c>ConfigUpgradeScenarios.cs</c> exercise instead. It cannot join the file-level
    /// <c>sonar.coverage.exclusions</c> list the others use: this file also holds
    /// <see cref="Compare"/>, which is exactly the half a unit test can and does drive, and
    /// <c>ConfigUpgrade.cs</c> cannot be split the way <c>AttributionMetrics</c> was without also
    /// touching Pulse.Otlp's own project file, which links this file in and belongs to a separate
    /// PR.</para></remarks>
    [ExcludeFromCodeCoverage]
    public static void Upgrade<T>(ICoreServerAPI api, T config, string filename, string modName)
        where T : class
    {
        ConfigDiff diff;
        try
        {
            // ToPrettyString is the very call StoreModConfig makes to write the file: Newtonsoft
            // with no settings at all, so the keys compared here are the keys a rewrite produces.
            string path = Path.Combine(api.GetOrCreateDataPath("ModConfig"), filename);
            diff = Compare(File.ReadAllText(path), JsonUtil.ToPrettyString(config));
            if (diff.Missing.Count > 0)
            {
                api.StoreModConfig(config, filename);
                api.Logger.Notification(
                    AddedKeysTemplate(diff), modName, filename, string.Join(", ", diff.Missing));
            }
        }
        catch (Exception e)
        {
            api.Logger.Warning(UpgradeFailed, modName, filename, e.Message);
            return;
        }

        if (diff.Unknown.Count > 0)
        {
            api.Logger.Warning(
                diff.Missing.Count > 0 ? DroppedKeys : IgnoredKeys,
                modName, filename, string.Join(", ", diff.Unknown));
        }

        if (diff.Duplicated.Count > 0)
        {
            api.Logger.Warning(
                DuplicateKeysTemplate(diff), modName, filename, string.Join("; ", diff.Duplicated));
        }
    }

    /// <summary>Which keys of <paramref name="loaded"/> are absent from <paramref name="onDisk"/>,
    /// which keys of <paramref name="onDisk"/> are absent from <paramref name="loaded"/>, and which
    /// keys of <paramref name="onDisk"/> repeat the same key under different casing.</summary>
    /// <remarks>Keys only, never values, so key order and formatting make no difference, other than
    /// deciding which of a duplicated key's spellings a rewrite would keep. A missing block is
    /// reported by its own name and not walked: naming its children would only pad the log line
    /// with keys the admin never had. Text that does not parse as a JSON object reports nothing,
    /// which leaves the file alone rather than rewriting something unreadable.
    /// <para>Keys are matched case-insensitively throughout, the same way Newtonsoft binds
    /// <c>LoadModConfig</c>'s keys onto the config object: an ordinal comparison here would report
    /// a key spelled with different casing as both missing and unknown, when Newtonsoft already
    /// applied it exactly as written.</para></remarks>
    public static ConfigDiff Compare(string onDisk, string loaded)
    {
        List<string> missing = [];
        List<string> unknown = [];
        List<string> duplicated = [];
        if (Parse(onDisk) is { } file && Parse(loaded) is { } config)
        {
            Walk(file, config, string.Empty, missing, unknown, duplicated);
        }

        return new ConfigDiff(missing, unknown, duplicated);
    }

    private static void Walk(
        JsonObject file, JsonObject config, string prefix,
        List<string> missing, List<string> unknown, List<string> duplicated)
    {
        // Grouped case-insensitively, so a key merely spelled with different casing is not also
        // reported as unknown just because its exact casing does not appear in config.
        ILookup<string, KeyValuePair<string, JsonNode?>> byKey =
            file.ToLookup(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        HashSet<string> known = new(config.Select(entry => entry.Key), StringComparer.OrdinalIgnoreCase);

        // This level before the blocks under it, so both lists read outermost key first.
        ReportUnknown(byKey, known, prefix, unknown);

        // config carrying more than one entry for the same case-insensitive key, at this same
        // level, is only possible for a value Newtonsoft bound as a dictionary rather than a
        // typed property: a class member has one canonical spelling no matter how the file cased
        // it, but a Dictionary<string, TValue> keeps every JSON key it is handed as its own entry
        // (confirmed against a Headers block with "authorization" and "Authorization" both set).
        // That is the signal DescribeDuplicate uses to tell the two apart without reflecting on
        // config's actual C# type, and it is also why a dictionary's collision has to be
        // de-duplicated: the loop below would otherwise visit the same file-side group once per
        // surviving config entry and report it twice.
        ILookup<string, KeyValuePair<string, JsonNode?>> configByKey =
            config.ToLookup(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, JsonNode?> entry in config)
        {
            List<KeyValuePair<string, JsonNode?>> matches = byKey[entry.Key].ToList();
            if (matches.Count == 0)
            {
                missing.Add(prefix + entry.Key);
                continue;
            }

            if (matches.Count > 1 && reported.Add(matches[0].Key))
            {
                duplicated.Add(DescribeDuplicate(prefix, entry.Key, entry.Value, matches, configByKey));
            }

            // A single spelling recurses into its own block as before; more than one merges every
            // spelling's fields into one synthetic block first (see ResolveNestedFile): Newtonsoft
            // populates the same nested object for each occurrence it reads rather than replacing
            // it wholesale, so a field only one spelling set (Attribution.Enabled, block
            // "attribution" only setting BurstTicks, say) must not be reported missing just
            // because the last spelling alone did not carry it.
            if (entry.Value is JsonObject nested && ResolveNestedFile(matches) is { } nestedFile)
            {
                Walk(nestedFile, nested, prefix + entry.Key + ".", missing, unknown, duplicated);
            }
        }
    }

    private static void ReportUnknown(
        ILookup<string, KeyValuePair<string, JsonNode?>> byKey, HashSet<string> known, string prefix,
        List<string> unknown)
    {
        foreach (IGrouping<string, KeyValuePair<string, JsonNode?>> group in byKey.Where(g => !known.Contains(g.Key)))
        {
            foreach (KeyValuePair<string, JsonNode?> entry in group)
            {
                unknown.Add(prefix + entry.Key);
            }
        }
    }

    /// <summary>Never the values themselves: one spelling can be an OTLP header's own secret, and
    /// this file is linked into Pulse.Otlp unchanged. A "winner" is also only ever true for a
    /// scalar bound to one property: Newtonsoft merges a duplicated block's fields onto the same
    /// nested object (see <see cref="ResolveNestedFile"/>) and keeps every one of a dictionary's
    /// colliding keys (<paramref name="configByKey"/> then holds more than one entry for
    /// <paramref name="key"/>), so nothing there ever simply replaces something else.</summary>
    private static string DescribeDuplicate(
        string prefix, string key, JsonNode? configValue, List<KeyValuePair<string, JsonNode?>> matches,
        ILookup<string, KeyValuePair<string, JsonNode?>> configByKey)
    {
        string spellings = string.Join(", ", matches.Select(m => $"\"{m.Key}\""));
        if (configValue is JsonObject || configByKey[key].Count() > 1)
        {
            return $"{prefix}{key}: written as {spellings}; every spelling is read, none of them alone";
        }

        string others = string.Join(", ", matches.Take(matches.Count - 1).Select(m => $"\"{m.Key}\""));
        return $"{prefix}{key}: \"{matches[^1].Key}\" wins over {others}";
    }

    private static JsonObject? ResolveNestedFile(List<KeyValuePair<string, JsonNode?>> matches) =>
        matches.Count > 1 ? Merge(matches) : matches[^1].Value as JsonObject;

    /// <summary>Unions every matched spelling's own keys into one block, the way Newtonsoft's
    /// reused-object binding leaves a class property after reading it more than once. Values are
    /// cloned rather than moved: a <see cref="JsonNode"/> can belong to only one
    /// <see cref="JsonObject"/> at a time, and <paramref name="matches"/> still belongs to
    /// <c>file</c>.</summary>
    private static JsonObject Merge(IEnumerable<KeyValuePair<string, JsonNode?>> matches)
    {
        JsonObject merged = [];
        foreach (KeyValuePair<string, JsonNode?> match in matches)
        {
            if (match.Value is JsonObject block)
            {
                foreach (KeyValuePair<string, JsonNode?> property in block)
                {
                    merged[property.Key] = property.Value?.DeepClone();
                }
            }
        }

        return merged;
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, nodeOptions: null, Lenient) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
