using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Xunit;

namespace Pulse.Otlp.Tests;

/// <summary>The precedence between the config keys and the two environment variables the
/// ecosystem sets service identity with, proved through a real MeterProvider's resource: what the
/// exporter would put on the wire, not what Pulse meant to hand the SDK. The SDK's own resource
/// merging is exactly where this can go wrong (a later AddService beats an earlier detector), so a
/// test that stopped short of building a provider would test the intention and not the outcome.</summary>
/// <remarks>The environment is process-wide, so the class runs alone, never alongside the others,
/// and every test starts from a clean one and puts back what it found, whatever the machine running
/// it happens to export.</remarks>
[CollectionDefinition(nameof(ServiceIdentityTests), DisableParallelization = true)]
[Collection(nameof(ServiceIdentityTests))]
public sealed class ServiceIdentityTests : IDisposable
{
    private const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    private const string AttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";

    private readonly string? originalServiceName = Environment.GetEnvironmentVariable(ServiceNameVariable);
    private readonly string? originalAttributes = Environment.GetEnvironmentVariable(AttributesVariable);

    public ServiceIdentityTests() => SetEnvironment(null, null);

    public void Dispose() => SetEnvironment(originalServiceName, originalAttributes);

    [Fact]
    public void TheConfiguredNameAndInstanceId_Reach_TheResource()
    {
        Resource resource = Build("my-server", "my-instance");

        Assert.Equal("my-server", Name(resource));
        Assert.Equal("my-instance", InstanceId(resource));
    }

    [Fact]
    public void TheConfiguredInstanceId_IsTrimmed_AndTheSameOnEveryStart()
    {
        Assert.Equal("my-instance", InstanceId(Build("my-server", "  my-instance  ")));

        // The old behaviour, a fresh random id on every start, is what this feature exists to end:
        // the same configuration has to give the same id each time it is built.
        Assert.Equal(InstanceId(Build("my-server", "my-instance")), InstanceId(Build("my-server", "my-instance")));
    }

    /// <summary>Never reached by StartServerSide, which has already filled a blank key and written
    /// it to the file by the time the resource is configured; what it proves is that the resource
    /// never goes without an id, nor falls back to the SDK's own automatic one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankInstanceId_StillGivesTheResourceAGuid(string? configured)
        => Assert.True(Guid.TryParse(InstanceId(Build("my-server", configured)), out _));

    [Fact]
    public void TheEnvironmentsInstanceId_Wins_OverTheConfigKey()
    {
        SetEnvironment(null, "service.instance.id=env-instance");

        Resource resource = Build("my-server", "config-instance");

        Assert.Equal("env-instance", InstanceId(resource));
        Assert.Equal("my-server", Name(resource));
    }

    /// <summary>The environment's value is read the way the SDK reads it, percent-decoding and
    /// trimming included, because it is the SDK's own detector that is asked for it.</summary>
    [Fact]
    public void TheEnvironmentsInstanceId_IsRead_TheWayTheSdkReadsIt()
    {
        SetEnvironment(null, "service.instance.id= my%20instance ");

        Assert.Equal("my instance", InstanceId(Build("my-server", "config-instance")));
    }

    /// <summary>An empty value identifies nothing, so it is not an override: the SDK would
    /// otherwise export an empty service.instance.id, and the backend an empty instance label.</summary>
    [Fact]
    public void ABlankEnvironmentInstanceId_Falls_BackToTheConfigKey()
    {
        SetEnvironment(null, "service.instance.id=");

        Assert.Equal("config-instance", InstanceId(Build("my-server", "config-instance")));
    }

    /// <summary>Only the one key. Everything else the variable carries still reaches the resource,
    /// and a service.name in it still loses to the config key, as it did in 0.2.0: only
    /// OTEL_SERVICE_NAME overrides the name.</summary>
    [Fact]
    public void AServiceNameInTheAttributesVariable_StillLoses_ToTheConfigKey()
    {
        SetEnvironment(null, "service.name=env-name,service.instance.id=env-instance,deployment.environment=test");

        Resource resource = Build("my-server", "config-instance");

        Assert.Equal("my-server", Name(resource));
        Assert.Equal("env-instance", InstanceId(resource));
        Assert.Equal("test", OtlpOptions.ResourceAttribute(resource, "deployment.environment"));
    }

    [Fact]
    public void OtelServiceName_LeavesTheWholeIdentity_ToTheEnvironment()
    {
        SetEnvironment("env-name", null);

        Resource resource = Build("my-server", "config-instance");

        Assert.Equal("env-name", Name(resource));

        // Neither config key is used, so the resource carries no instance id at all: what 0.1.0
        // exported, and what the 0.2.0 notes tell an admin to set OTEL_SERVICE_NAME for.
        Assert.Null(InstanceId(resource));
    }

    [Fact]
    public void OtelServiceName_ExportsTheEnvironmentsInstanceId_WhenThereIsOne()
    {
        SetEnvironment("env-name", "service.instance.id=env-instance");

        Resource resource = Build("my-server", "config-instance");

        Assert.Equal("env-name", Name(resource));
        Assert.Equal("env-instance", InstanceId(resource));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankOtelServiceName_IsNotAnOverride(string blank)
    {
        SetEnvironment(blank, null);

        Resource resource = Build("my-server", "config-instance");

        Assert.Equal("my-server", Name(resource));
        Assert.Equal("config-instance", InstanceId(resource));
    }

    private static Resource Build(string? configuredName, string? configuredInstanceId)
    {
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => OtlpOptions.ConfigureServiceIdentity(r, configuredName, configuredInstanceId))
            .Build()!;
        return provider.GetResource();
    }

    private static string? Name(Resource resource) => OtlpOptions.ResourceAttribute(resource, OtlpOptions.ServiceNameKey);

    private static string? InstanceId(Resource resource)
        => OtlpOptions.ResourceAttribute(resource, OtlpOptions.ServiceInstanceIdKey);

    private static void SetEnvironment(string? serviceName, string? attributes)
    {
        Environment.SetEnvironmentVariable(ServiceNameVariable, serviceName);
        Environment.SetEnvironmentVariable(AttributesVariable, attributes);
    }
}
