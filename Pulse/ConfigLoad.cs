namespace Pulse;

/// <summary>How a config load attempt played out.</summary>
internal enum ConfigLoadStatus
{
    /// <summary>The file existed and was read. Safe to run <see cref="ConfigUpgrade.Upgrade{T}"/> on it.</summary>
    Loaded,

    /// <summary>No file existed. <see cref="Config"/> is a fresh default; the caller still has to
    /// write it.</summary>
    Absent,

    /// <summary>The file existed but raised while being read or parsed. <see cref="Config"/> is a
    /// fresh, in-memory default; the caller must not write it and must not run
    /// <see cref="ConfigUpgrade"/> on it, or an admin's own edits are exactly what gets destroyed.</summary>
    Unreadable,
}

/// <summary>The outcome of one config load attempt: the config to run with, what happened, and, for
/// <see cref="ConfigLoadStatus.Unreadable"/>, the reason.</summary>
internal readonly record struct ConfigLoadResult<T>(T Config, ConfigLoadStatus Status, string? FailureMessage)
    where T : class;

/// <summary>Decides what a config load attempt means, tolerating a file that exists but will not
/// read or parse.</summary>
/// <remarks>Both mods compile this from the one source file, the same arrangement as
/// <see cref="ConfigUpgrade"/>: Pulse.Otlp links it rather than referencing Pulse.dll.
/// <para><c>ICoreServerAPI.LoadModConfig&lt;T&gt;</c> returns null for a file that does not exist,
/// but throws for one that exists and will not deserialize: a <c>Newtonsoft.Json.JsonException</c>
/// (a <c>JsonReaderException</c> for a syntax error such as a stray comma, or a
/// <c>JsonSerializationException</c> for a value of the wrong shape) when the text itself is bad,
/// or an <c>IOException</c>/<c>UnauthorizedAccessException</c> if the file cannot even be read.
/// Left uncaught, that exception escapes StartServerSide and the mod never starts; worse, the file
/// already exists, so nothing regenerates it. Confirmed against APIBase.LoadModConfig in
/// VintagestoryLib, which is <c>JsonConvert.DeserializeObject&lt;T&gt;(File.ReadAllText(path))</c>
/// behind the null-for-absent check.</para>
/// <para>Pure and delegate-driven on purpose: the attempt already happened by the time
/// <see cref="Resolve{T}"/> runs, so it needs nothing from the engine and is unit-tested directly
/// with a fake success or failure. Calling <c>LoadModConfig</c>, writing defaults for an absent
/// file, running <see cref="ConfigUpgrade"/>, and logging the one line for an unreadable file are
/// all the caller's job, kept in each ModSystem.</para></remarks>
internal static class ConfigLoad
{
    /// <summary>The one line an admin sees for a config file that exists but will not load. Args:
    /// the mod's name, the file's full path, the parser's own message, and what the mod does
    /// instead for this session.</summary>
    internal const string UnreadableMessage =
        "{0} could not read {1} ({2}). Fix the file, or delete it to get a fresh one with defaults; "
        + "{3} for this session.";

    /// <param name="attempt">Calls <c>ICoreServerAPI.LoadModConfig&lt;T&gt;</c>.</param>
    /// <param name="makeDefaults">Builds a fresh, in-memory default. Called for an absent file or an
    /// unreadable one, never for one that loaded; never writes anything itself, so it is safe to
    /// call for a file this must not touch.</param>
    internal static ConfigLoadResult<T> Resolve<T>(Func<T?> attempt, Func<T> makeDefaults)
        where T : class
    {
        try
        {
            T? existing = attempt();
            return existing != null
                ? new ConfigLoadResult<T>(existing, ConfigLoadStatus.Loaded, null)
                : new ConfigLoadResult<T>(makeDefaults(), ConfigLoadStatus.Absent, null);
        }
        catch (Exception e)
        {
            return new ConfigLoadResult<T>(makeDefaults(), ConfigLoadStatus.Unreadable, e.Message);
        }
    }
}
