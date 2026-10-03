using System.Text.RegularExpressions;
using Atlas.XUnit;
using Pulse.Scenarios;
using Xunit;

namespace Pulse.Otlp.Scenarios;

/// <summary>A server that generates its service instance id and cannot save it: the id is lost at
/// the end of the session, the next start exports another one, and the backend sees a new server.
/// Nothing but a line in the log can tell the admin, and the line has to be there.</summary>
/// <remarks>The fixture's pulse-otlp.json is written with single quotes, which Newtonsoft loads and
/// the upgrade's comparison (System.Text.Json) cannot parse: it finds nothing missing, asks for no
/// rewrite and logs nothing, so the file keeps no id. That is the same outcome as a ModConfig
/// folder mounted read-only, the case the README names, which a fixture cannot set up: a seeded
/// file cannot be committed read-only, and a permission bit means nothing to a server running as
/// root. The scrape endpoint and the OTLP endpoint share one port number, as in the config upgrade
/// fixture: nothing needs to listen, an export that cannot connect is swallowed by the SDK's own
/// export thread.</remarks>
[AtlasDataFiles("data/unsavedid", TargetPath = "ModConfig")]
public class OtlpUnsavedInstanceIdScenarios : AtlasScenarioBase
{
    private static readonly string TestAssemblyDirectory =
        Path.GetDirectoryName(typeof(OtlpUnsavedInstanceIdScenarios).Assembly.Location)!;

    [AtlasScenario]
    public async Task Server_Warns_WhenTheGeneratedServiceInstanceId_CannotBeSaved()
    {
        string log = await ServerLog.WaitFor(World, "Pulse OTLP exporting", "could not save it to");

        // The file did load: its own service name is on the startup line, which also names the id
        // this session exports.
        Match startup = Regex.Match(
            log, "as service 'pulse-atlas-unsaved-id', instance '(?<id>[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12})'");
        Assert.True(startup.Success, "the startup line does not name a generated instance id");
        string id = startup.Groups["id"].Value;

        // The warning names that same id, the file it could not go into, and both ways to keep one.
        Assert.Contains(
            $"exports the generated service.instance.id '{id}' this session but could not save it to pulse-otlp.json",
            log);
        Assert.Contains("next start will export a different one", log);
        Assert.Contains("set ServiceInstanceId in pulse-otlp.json to any text you like", log);
        Assert.Contains("OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id>", log);

        // Nothing rewrote the file: it is what the admin wrote, single quotes and all.
        string seedPath = Path.Combine(TestAssemblyDirectory, "data", "unsavedid", "pulse-otlp.json");
        string configPath = Path.Combine(World.Api.GetOrCreateDataPath("ModConfig"), "pulse-otlp.json");
        Assert.Equal(File.ReadAllBytes(seedPath), File.ReadAllBytes(configPath));
    }
}
