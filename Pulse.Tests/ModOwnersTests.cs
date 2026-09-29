using Xunit;

namespace Pulse.Tests;

public class ModOwnersTests
{
    /// <summary>Two types from two different assemblies, which is what the table keys on. The test
    /// assembly stands in for a mod's, and the framework's for something no mod ships.</summary>
    private static readonly Type ModType = typeof(ModOwnersTests);
    private static readonly Type ForeignType = typeof(string);

    private static ModOwners Owners(params (string Code, Type Behavior)[] registry)
    {
        Dictionary<string, Type> classes = registry.ToDictionary(entry => entry.Code, entry => entry.Behavior);
        return new ModOwners(code => classes.GetValueOrDefault(code));
    }

    [Fact]
    public void Owner_Maps_AModSystemsOwnTypeName()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        Assert.Equal("mymod", owners.Owner(ModType.ToString()));
    }

    [Fact]
    public void Owner_Returns_Null_ForANameNothingClaims()
        => Assert.Null(Owners().Owner("Some.Unknown.Type"));

    /// <summary>Entity behaviors are marked with the code the class was registered under, so the
    /// class registry is the only bridge from the mark back to an assembly.</summary>
    [Fact]
    public void Owner_Resolves_ABehaviorCode_ThroughTheClassRegistry()
    {
        ModOwners owners = Owners(("health", ModType));
        owners.AddSystem("mymod", ModType);

        Assert.Equal("mymod", owners.Owner("health"));
    }

    [Fact]
    public void Owner_Returns_Null_ForABehaviorFromAnAssemblyNoModClaims()
    {
        ModOwners owners = Owners(("health", ForeignType));
        owners.AddSystem("mymod", ModType);

        Assert.Null(owners.Owner("health"));
    }

    /// <summary>The registry lookup is the expensive half, and it runs on every profiled tick, so a
    /// miss has to be remembered as firmly as a hit.</summary>
    [Fact]
    public void Owner_Asks_TheClassRegistryOncePerName()
    {
        int asked = 0;
        ModOwners owners = new(_ =>
        {
            asked++;
            return null;
        });

        owners.Owner("health");
        owners.Owner("health");

        Assert.Equal(1, asked);
    }

    [Fact]
    public void OfAssembly_Answers_ForAnAssemblyAModSystemWasDeclaredIn()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        Assert.Equal("mymod", owners.OfAssembly(ModType.Assembly));
        Assert.Null(owners.OfAssembly(ForeignType.Assembly));
    }

    /// <summary>What the listener walk contributes: a handler whose target type belongs to a mod but
    /// is not that mod's ModSystem, which the mod loader alone cannot map.</summary>
    [Fact]
    public void Learn_Pins_ANameTheTableWouldNotHaveWorkedOut()
    {
        ModOwners owners = Owners();
        owners.Learn("Some.Mod.Internal.Ticker", "mymod");

        Assert.Equal("mymod", owners.Owner("Some.Mod.Internal.Ticker"));
    }

    [Fact]
    public void Learn_Overrides_ARememberedMiss()
    {
        ModOwners owners = Owners();
        Assert.Null(owners.Owner("Some.Mod.Internal.Ticker"));

        owners.Learn("Some.Mod.Internal.Ticker", "mymod");

        Assert.Equal("mymod", owners.Owner("Some.Mod.Internal.Ticker"));
    }
}
