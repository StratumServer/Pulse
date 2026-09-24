using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Pulse.Scenarios;

/// <summary>What attribution serves the moment it is armed, before any burst has run.</summary>
/// <remarks>Its own class, and its own world: AttributionScenarios' other scenarios share one
/// world and run several bursts through it before this class ever gets a turn, at which point
/// "from boot" would no longer be true and modids no live mark has produced in this run, such as
/// unattributed, would have no reason to be seeded any more.</remarks>
[AtlasDataFiles("data/attribution/pulse.json", TargetPath = "ModConfig")]
public class AttributionBootScenarios : AtlasScenarioBase
{
    private const int Port = 39465;

    private static readonly string[] Families =
    [
        "pulse_mod_tick_share",
        "pulse_mod_tick_seconds_total",
        "pulse_attribution_ticks_total",
        "pulse_attribution_dropped_samples_total",
    ];

    [AtlasScenario]
    public async Task Attribution_Serves_ItsFamilies_FromBoot()
    {
        await World.Ticks(5);

        string body = await Scrape.Metrics(Port);

        // Seeded at zero, so the families are on the wire before the first burst rather than
        // appearing minutes into a dashboard's life.
        foreach (string family in Families)
        {
            Assert.Contains("# TYPE " + family + " ", body);
        }

        Assert.Contains("pulse_mod_tick_share{modid=\"engine\"} ", body);
        Assert.Contains("pulse_mod_tick_share{modid=\"unattributed\"} ", body);
    }
}
