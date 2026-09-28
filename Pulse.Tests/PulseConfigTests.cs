using Xunit;

namespace Pulse.Tests;

/// <summary>The defaults ModConfig/pulse.json ships with before an admin ever edits it: enabled,
/// loopback-only, runtime metrics on.</summary>
public class PulseConfigTests
{
    [Fact]
    public void Defaults_Enable_TheEndpoint_OnLoopback_WithRuntimeMetricsOn()
    {
        PulseConfig config = new();

        Assert.True(config.Enabled);
        Assert.Equal("127.0.0.1", config.Bind);
        Assert.True(config.RuntimeMetrics);
    }
}
