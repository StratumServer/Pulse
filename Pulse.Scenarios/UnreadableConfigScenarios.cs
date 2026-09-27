using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>A pulse.json an admin's editor left broken: present on disk, but not JSON a parser
/// accepts. The mod must run this session on its built-in defaults rather than fail to start, and
/// it must never touch a file it could not make sense of.</summary>
[AtlasDataFiles("data/unreadable/pulse.json", TargetPath = "ModConfig")]
public class UnreadableConfigScenarios : AtlasScenarioBase
{
    private const string ErrorMarker = "Pulse could not read";

    // The default: proof that a malformed file gets no configured values at all, defaults
    // included. Nothing else in either scenario suite binds this port.
    private const int DefaultPort = 9464;
    private const string ServingMarker = "Pulse serving metrics on http://127.0.0.1:9464/metrics";

    // Assembly.Location, not AppContext.BaseDirectory: Atlas repoints the latter at the embedded
    // server's own data path once it boots, which by the time a scenario method runs is no longer
    // where this test assembly (and the fixture AtlasDataFiles copied from) actually lives.
    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(UnreadableConfigScenarios).Assembly.Location)!;

    [AtlasScenario]
    public async Task Server_Runs_OnDefaults_AndLeavesTheBrokenFileAlone()
    {
        string log = await ServerLog.WaitFor(World, ErrorMarker, ServingMarker);

        string seedPath = Path.Combine(TestAssemblyDirectory, "data", "unreadable", "pulse.json");
        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse.json");
        Assert.Equal(File.ReadAllBytes(seedPath), File.ReadAllBytes(configPath));

        Assert.Contains(ErrorMarker, log);
        Assert.Contains(configPath, log);

        // This server's own endpoint came up on the default bind and port, not just some process
        // holding 9464 on a shared machine: a scrape answered by someone else's listener would
        // otherwise let the next assertion pass for the wrong reason.
        Assert.Contains(ServingMarker, log);

        // Defaults took over regardless: the endpoint is up on the port nothing in the broken file
        // could have named, because none of it was ever read.
        Assert.Contains("pulse_server_ticks_total", await Scrape.Metrics(DefaultPort));
    }
}
