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

    private const string DroppedKeys =
        "{0} does not know these keys in {1}, and rewriting the file has just dropped them: {2}. "
        + "Check them for typos.";

    private const string IgnoredKeys =
        "{0} does not know these keys in {1}: {2}. They do nothing; check them for typos.";

    private const string DuplicateKeys =
        "{0} found the same key written more than once, cased differently, in {1}: {2}. The "
        + "spelling that comes last in the file is the value actually in effect; remove the "
        + "others to stop this warning.";

    private const string UpgradeFailed =
        "{0} could not bring {1} up to date ({2}). The server runs on the values the file does "
        + "have, with defaults for the rest.";

    /// <summary>Newtonsoft accepts both when it loads the config, so a file the game read happily
    /// must not be one this refuses to look at.</summary>
    private static readonly JsonDocumentOptions Lenient =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Adds whatever keys a newer version of the mod introduced to the config file the
    /// admin already has, and says in the log what changed.</summary>
    /// <remarks>The file is rewritten from <paramref name="config"/>, which the loader filled with
    /// defaults wherever the file was silent, so the write only ever adds: every value the admin
    /// set is already in the object. It happens solely when a key is missing, because a complete
    /// file must not be touched at all, not even its modification time, on a host that mounts
    /// ModConfig read-only or tracks it. The same rewrite is what drops a key the config does not
    /// know, which is why an unknown key is worth a warning rather than silence.
    /// <para>Nothing in here may take a server down over a config file, so a read or a write that
    /// fails is one warning and then the server carries on unchanged.</para></remarks>
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
                api.Logger.Notification(AddedKeys, modName, filename, string.Join(", ", diff.Missing));
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
            api.Logger.Warning(DuplicateKeys, modName, filename, string.Join("; ", diff.Duplicated));
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
        foreach (IGrouping<string, KeyValuePair<string, JsonNode?>> group in byKey)
        {
            if (!known.Contains(group.Key))
            {
                foreach (KeyValuePair<string, JsonNode?> entry in group)
                {
                    unknown.Add(prefix + entry.Key);
                }
            }
        }

        foreach (KeyValuePair<string, JsonNode?> entry in config)
        {
            List<KeyValuePair<string, JsonNode?>> matches = byKey[entry.Key].ToList();
            if (matches.Count == 0)
            {
                missing.Add(prefix + entry.Key);
                continue;
            }

            if (matches.Count > 1)
            {
                // The last spelling in document order is the one Newtonsoft actually applied: it
                // populates the same bound property again for every later occurrence it reads.
                (string winnerKey, JsonNode? winnerValue) = matches[^1];
                string others = string.Join(
                    ", ", matches.Take(matches.Count - 1).Select(m => $"\"{m.Key}\" ({Literal(m.Value)})"));
                duplicated.Add(
                    $"{prefix}{entry.Key}: \"{winnerKey}\" ({Literal(winnerValue)}) wins over {others}");
            }

            (_, JsonNode? theirs) = matches[^1];
            if (entry.Value is JsonObject nested && theirs is JsonObject nestedFile)
            {
                Walk(nestedFile, nested, prefix + entry.Key + ".", missing, unknown, duplicated);
            }
        }
    }

    private static string Literal(JsonNode? value) => value?.ToJsonString() ?? "null";

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
