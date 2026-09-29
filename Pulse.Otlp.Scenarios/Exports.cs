using System.Diagnostics;

namespace Pulse.Otlp.Scenarios;

/// <summary>The one wait both collector scenarios share: pump the world until an export lands.</summary>
internal static class Exports
{
    /// <summary>Pumps the world until <paramref name="first"/> hands back an export, or the
    /// deadline passes.</summary>
    /// <remarks>The bound is wall clock rather than a tick count, which is why this is not
    /// <c>World.Until</c>: the exporter waits on a real timer on its own thread, and it owes the
    /// game loop nothing. Ticking is how the scenario passes that time without sleeping the thread
    /// the world runs on.</remarks>
    public static async Task<T> WaitFor<T>(Func<T?> first, Func<Task> pump, TimeSpan deadline, int port)
        where T : class
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (first() == null && clock.Elapsed < deadline)
        {
            await pump();
        }

        return first()
            ?? throw new InvalidOperationException(
                $"no export reached the collector on port {port} within {deadline.TotalSeconds:0}s");
    }
}
