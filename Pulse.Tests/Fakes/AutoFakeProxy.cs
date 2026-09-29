using System.Reflection;

namespace Pulse.Tests.Fakes;

/// <summary>A <see cref="DispatchProxy"/> that fabricates a harmless implementation of any
/// interface on demand: every member not explicitly stubbed through <see cref="On"/> returns a
/// value built from its own return type alone, rather than throwing or returning null into code
/// that does not check for it. A value type gets its default; a string gets <see
/// cref="string.Empty"/>; an array gets a zero-length instance; an interface gets its own nested
/// auto-fake, created once and reused for every later call that asks for the same interface type
/// on this instance; a concrete class with a public parameterless constructor gets a real instance
/// of it; anything else gets null.</summary>
/// <remarks>What makes <see cref="Pulse.PulseModSystem.StartServerSide"/> drivable against
/// <c>ICoreServerAPI</c> without a live server: the engine's own API surface is almost entirely
/// interfaces, so a property chain this class never heard of (<c>api.Server.Config.TickTime</c>,
/// say) resolves to a real, zeroed object at every step instead of a <see
/// cref="NullReferenceException"/> two calls in. Only the handful of members a test actually
/// cares about need a real stub, through <see cref="On"/>.</remarks>
internal class AutoFakeProxy : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[]?, object?>> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> nestedFakes = new();

    /// <summary>Creates a fake implementing <typeparamref name="T"/>, handing back both the
    /// interface reference to pass to production code and the fake itself, to register stubs on
    /// with <see cref="On"/>.</summary>
    public static (T Proxy, AutoFakeProxy Fake) Create<T>()
        where T : class
    {
        T proxy = DispatchProxy.Create<T, AutoFakeProxy>();
        return (proxy, (AutoFakeProxy)(object)proxy);
    }

    /// <summary>Registers a real implementation for one member, by its plain name (a property
    /// getter is <c>"get_Name"</c>, matching <see cref="MemberInfo.Name"/> conventions). Every
    /// overload of that name shares the handler; none of the members this class stubs are
    /// overloaded, so the handler reads <paramref name="args"/> itself when it needs to tell them
    /// apart.</summary>
    public void On(string memberName, Func<object?[]?, object?> handler) => handlers[memberName] = handler;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            return null;
        }

        return handlers.TryGetValue(targetMethod.Name, out Func<object?[]?, object?>? handler)
            ? handler(args)
            : AutoValue(targetMethod.ReturnType);
    }

    private object? AutoValue(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        if (type == typeof(string))
        {
            return string.Empty;
        }

        if (type.IsArray)
        {
            return Array.CreateInstance(type.GetElementType()!, 0);
        }

        if (type.IsInterface)
        {
            if (!nestedFakes.TryGetValue(type, out object? existing))
            {
                MethodInfo factory = typeof(DispatchProxy)
                    .GetMethod(nameof(DispatchProxy.Create), Type.EmptyTypes)!
                    .MakeGenericMethod(type, typeof(AutoFakeProxy));
                existing = factory.Invoke(null, null)!;
                nestedFakes[type] = existing;
            }

            return existing;
        }

        // A concrete collaborator type the engine hands back (ConcurrentDictionary<>, a config
        // POCO, ...): worth a real, empty instance over null when it has a public parameterless
        // constructor, and worth null rather than a thrown TargetInvocationException when it does
        // not (an abstract Type, say).
        try
        {
            return Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }
}
