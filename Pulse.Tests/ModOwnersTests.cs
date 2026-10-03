using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

// The game's API declares a Func delegate of its own in Vintagestory.API.Common, so the one this
// file wants gets a name of its own rather than a namespace qualifier on every signature.
using BehaviorFactory = System.Func<Vintagestory.API.Common.Entities.Entity, Vintagestory.API.Common.Entities.EntityBehavior>;

namespace Pulse.Tests;

public class ModOwnersTests
{
    /// <summary>Two types from two different assemblies, which is what the table keys on. The test
    /// assembly stands in for a mod's, and the framework's for something no mod ships.</summary>
    private static readonly Type ModType = typeof(ModOwnersTests);
    private static readonly Type ForeignType = typeof(string);

    /// <summary>A third assembly, standing in for a second mod's.</summary>
    private static readonly Type OtherModType = typeof(Assert);

    /// <summary>Registered as <c>despawn</c> and marking as <c>timeddespawn</c>, the way the game's
    /// own <c>EntityBehaviorDespawn</c> does.</summary>
    private sealed class DespawnLikeBehavior(Entity entity) : EntityBehavior(entity)
    {
        public override string PropertyName() => "timeddespawn";
    }

    /// <summary>Registered as <c>health</c> and marking as <c>health</c>: the common case, where the
    /// name is the registration code.</summary>
    private sealed class HealthLikeBehavior(Entity entity) : EntityBehavior(entity)
    {
        public override string PropertyName() => "health";
    }

    /// <summary>A third-party class sharing the name of the engine's own passive physics, which is
    /// what a subclass that inherits its parent's <c>PropertyName()</c> looks like from a mark.</summary>
    private sealed class SharesAnEngineNameBehavior(Entity entity) : EntityBehavior(entity)
    {
        public override string PropertyName() => "entitypassivephysics";
    }

    /// <summary>An entity is abstract and only the engine ever loads one, so this is the smallest
    /// thing that carries behaviors the way a loaded one does.</summary>
    /// <remarks>The server side of its properties is allocated without running its constructor,
    /// which would load Newtonsoft.Json to parse an entity type's behavior config: these tests need
    /// the list the behaviors sit in, nothing else.</remarks>
    private sealed class BareEntity : Entity
    {
        public BareEntity Carrying(params BehaviorFactory[] behaviors)
        {
            Properties ??= new EntityProperties
            {
                Server = (EntityServerProperties)RuntimeHelpers.GetUninitializedObject(typeof(EntityServerProperties)),
            };
            Properties.Server.Behaviors ??= [];
            foreach (BehaviorFactory make in behaviors)
            {
                Properties.Server.Behaviors.Add(make(this));
            }

            return this;
        }
    }

    private static string Mark(EntityBehavior behavior) => behavior.ProfilerName;

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

    /// <summary>A behavior whose name is the code its class was registered under needs nothing but
    /// the class registry to get from the mark back to an assembly.</summary>
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

    /// <summary>The class registry is keyed by the code a class was registered under, and a mark
    /// carries the behavior's property name. For a class registered as <c>despawn</c> that marks as
    /// <c>timeddespawn</c> the registry has nothing under the name the mark carries, so the only
    /// thing that can say whose it is is a live instance.</summary>
    [Fact]
    public void LearnBehavior_Credits_AMarkWhoseNameIsNotItsRegistrationCode_ToTheModThatShipsTheClass()
    {
        ModOwners owners = Owners(("despawn", typeof(DespawnLikeBehavior)));
        owners.AddSystem("game", ModType);
        DespawnLikeBehavior live = new(new BareEntity());

        Assert.Null(owners.Owner("timeddespawn"));

        owners.LearnBehavior(Mark(live), live.GetType());

        Assert.Equal("game", owners.Owner("timeddespawn"));
    }

    /// <summary>The prefix the table strips is the one the engine really writes: the mark comes off a
    /// real behavior's base class, not off a string typed into the test.</summary>
    [Fact]
    public void LearnBehavior_Reads_TheMarkTheEngineBaseClassStamps()
    {
        HealthLikeBehavior live = new(new BareEntity());

        Assert.Equal(TickAttribution.BehaviorPrefix + "health", Mark(live));
    }

    [Fact]
    public void LearnBehavior_Keeps_ANameThatIsItsRegistrationCode_Resolving()
    {
        int asked = 0;
        ModOwners owners = new(_ =>
        {
            asked++;
            return typeof(HealthLikeBehavior);
        });
        owners.AddSystem("mymod", ModType);
        HealthLikeBehavior live = new(new BareEntity());

        owners.LearnBehavior(Mark(live), live.GetType());

        Assert.Equal("mymod", owners.Owner("health"));
        Assert.Equal(0, asked);
    }

    /// <summary>Two mods, two classes: each name goes to the mod that ships the class marking with
    /// it, which is what the game's own behaviors and a third party's both need.</summary>
    [Fact]
    public void LearnBehavior_Credits_EachClass_ToItsOwnMod()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        owners.AddSystem("game", OtherModType);

        owners.LearnBehavior(Mark(new DespawnLikeBehavior(new BareEntity())), typeof(DespawnLikeBehavior));
        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "displayname", OtherModType);

        Assert.Equal("mymod", owners.Owner("timeddespawn"));
        Assert.Equal("game", owners.Owner("displayname"));
    }

    /// <summary>A behavior the game's API assembly declares belongs to the engine, as the listeners
    /// the engine registers itself do: it is neither a mod's work nor work nobody claims.</summary>
    [Fact]
    public void LearnBehavior_Credits_ABehaviorTheGameApiDeclares_ToTheEngine()
    {
        ModOwners owners = Owners();
        EntityBehaviorPassivePhysics live = new(new BareEntity());

        owners.LearnBehavior(Mark(live), live.GetType());

        Assert.Equal(TickAttribution.Engine, owners.Owner("entitypassivephysics"));
    }

    /// <summary>Unattributed stays for what has no owner: a class from an assembly no mod claims.</summary>
    [Fact]
    public void LearnBehavior_Leaves_AClassNothingClaims_WithoutAnOwner()
    {
        ModOwners owners = Owners(("despawn", typeof(DespawnLikeBehavior)));
        DespawnLikeBehavior live = new(new BareEntity());

        owners.LearnBehavior(Mark(live), live.GetType());

        Assert.Null(owners.Owner("timeddespawn"));
    }

    /// <summary>Learning overrides what the registry guessed, including a miss it remembered: the
    /// guess is only ever the fallback for a name no entity has shown yet.</summary>
    [Fact]
    public void LearnBehavior_Overrides_WhatTheClassRegistryGuessed()
    {
        ModOwners owners = Owners(("health", ForeignType));
        owners.AddSystem("mymod", ModType);
        Assert.Null(owners.Owner("health"));

        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "health", ModType);

        Assert.Equal("mymod", owners.Owner("health"));
    }

    /// <summary>Two classes can mark with one name, because a subclass inherits its parent's
    /// property name and the mark carries nothing else. The first class with an owner keeps it, so
    /// the answer cannot depend on which entity the next walk meets first.</summary>
    [Fact]
    public void LearnBehavior_Keeps_TheFirstOwner_WhenTwoClassesShareAName()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity entity = new();

        owners.LearnBehavior(Mark(new EntityBehaviorPassivePhysics(entity)), typeof(EntityBehaviorPassivePhysics));
        owners.LearnBehavior(Mark(new SharesAnEngineNameBehavior(entity)), typeof(SharesAnEngineNameBehavior));

        Assert.Equal(TickAttribution.Engine, owners.Owner("entitypassivephysics"));
    }

    [Fact]
    public void LearnBehavior_Keeps_TheFirstOwner_WhateverTheOrder()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity entity = new();

        owners.LearnBehavior(Mark(new SharesAnEngineNameBehavior(entity)), typeof(SharesAnEngineNameBehavior));
        owners.LearnBehavior(Mark(new EntityBehaviorPassivePhysics(entity)), typeof(EntityBehaviorPassivePhysics));

        Assert.Equal("mymod", owners.Owner("entitypassivephysics"));
    }

    [Fact]
    public void LearnBehavior_GivesTheNameToAnOwner_OverAClassNothingClaims()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "shared", ForeignType);
        Assert.Null(owners.Owner("shared"));

        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "shared", ModType);
        Assert.Equal("mymod", owners.Owner("shared"));
    }

    [Fact]
    public void LearnBehavior_DoesNotTakeAName_FromAnOwner_ForAClassNothingClaims()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "shared", ModType);
        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "shared", ForeignType);

        Assert.Equal("mymod", owners.Owner("shared"));
    }

    /// <summary>The walk meets every instance of every class, thousands of them for a handful of
    /// classes, so a class it has looked at once costs one set lookup and nothing else.</summary>
    [Fact]
    public void LearnBehavior_Reads_AClassOnce()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "first", ModType);
        owners.LearnBehavior(TickAttribution.BehaviorPrefix + "second", ModType);

        Assert.Equal("mymod", owners.Owner("first"));
        Assert.Null(owners.Owner("second"));
    }

    /// <summary>A name that does not start with the behavior prefix is not a behavior mark, and
    /// must not pin anything.</summary>
    [Fact]
    public void LearnBehavior_Ignores_ANameWithoutTheBehaviorPrefix()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);

        owners.LearnBehavior("gmleSome.Mod.Ticker", ModType);

        Assert.Null(owners.Owner("gmleSome.Mod.Ticker"));
        Assert.Null(owners.Owner("Some.Mod.Ticker"));
    }

    [Fact]
    public void LearnBehaviors_Reads_EveryBehaviorOfEveryLoadedEntity()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity first = new BareEntity().Carrying(e => new DespawnLikeBehavior(e));
        BareEntity second = new BareEntity().Carrying(e => new HealthLikeBehavior(e), e => new EntityBehaviorPassivePhysics(e));

        owners.LearnBehaviors([KeyValuePair.Create(1L, (Entity)first), KeyValuePair.Create(2L, (Entity)second)]);

        Assert.Equal("mymod", owners.Owner("timeddespawn"));
        Assert.Equal("mymod", owners.Owner("health"));
        Assert.Equal(TickAttribution.Engine, owners.Owner("entitypassivephysics"));
    }

    /// <summary>An entity is read the first time the walk sees it and not again, which is what keeps
    /// a burst on a server with thousands of entities from visiting every behavior of every one.</summary>
    [Fact]
    public void LearnBehaviors_Reads_AnEntityOnlyOnce()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity entity = new BareEntity().Carrying(e => new HealthLikeBehavior(e));
        KeyValuePair<long, Entity>[] loaded = [KeyValuePair.Create(7L, (Entity)entity)];

        owners.LearnBehaviors(loaded);
        entity.Carrying(e => new DespawnLikeBehavior(e));
        owners.LearnBehaviors(loaded);

        Assert.Equal("mymod", owners.Owner("health"));
        Assert.Null(owners.Owner("timeddespawn"));
    }

    /// <summary>An entity that has unloaded is forgotten, so the ids of the entities a server has
    /// ever loaded do not pile up for the life of the process, and one that comes back is read
    /// again.</summary>
    [Fact]
    public void LearnBehaviors_Forgets_AnEntityThatHasUnloaded()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity entity = new BareEntity().Carrying(e => new HealthLikeBehavior(e));
        KeyValuePair<long, Entity>[] loaded = [KeyValuePair.Create(7L, (Entity)entity)];

        // Twice, so that both sets the walk swaps between have held the entity.
        owners.LearnBehaviors(loaded);
        owners.LearnBehaviors(loaded);
        owners.LearnBehaviors([]);
        entity.Carrying(e => new DespawnLikeBehavior(e));
        owners.LearnBehaviors(loaded);

        Assert.Equal("mymod", owners.Owner("timeddespawn"));
    }

    /// <summary>An entity that has not been initialised has no properties, and one for a client has
    /// no server side: neither is a reason to stop reading the rest.</summary>
    [Fact]
    public void LearnBehaviors_Skips_AnEntityThatCarriesNoServerBehaviors()
    {
        ModOwners owners = Owners();
        owners.AddSystem("mymod", ModType);
        BareEntity uninitialised = new();
        BareEntity noServerSide = new BareEntity().Carrying();
        noServerSide.Properties.Server = null!;
        BareEntity loaded = new BareEntity().Carrying(e => new DespawnLikeBehavior(e));

        owners.LearnBehaviors(
        [
            KeyValuePair.Create(1L, (Entity)uninitialised),
            KeyValuePair.Create(2L, (Entity)noServerSide),
            KeyValuePair.Create(3L, (Entity)loaded),
        ]);

        Assert.Equal("mymod", owners.Owner("timeddespawn"));
    }
}
