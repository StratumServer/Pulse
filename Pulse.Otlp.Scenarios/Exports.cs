using System.Diagnostics;
using System.Text;

namespace Pulse.Otlp.Scenarios;

/// <summary>The one wait both collector scenarios share: pump the world until an export lands.</summary>
internal static class Exports
{
    /// <summary>Pumps the world until <paramref name="find"/> hands back an export, or the
    /// deadline passes.</summary>
    /// <remarks>The bound is wall clock rather than a tick count, which is why this is not
    /// <c>World.Until</c>: the exporter waits on a real timer on its own thread, and it owes the
    /// game loop nothing. Ticking is how the scenario passes that time without sleeping the thread
    /// the world runs on. <paramref name="what"/> names the export being waited for and
    /// <paramref name="count"/> says how many the collector has received, both for the failure
    /// message: a collector nothing reached and one that only got exports without what was asked
    /// for are different failures.</remarks>
    public static async Task<T> WaitFor<T>(
        Func<T?> find, Func<int> count, Func<Task> pump, TimeSpan deadline, int port, string what = "export")
        where T : class
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (find() == null && clock.Elapsed < deadline)
        {
            await pump();
        }

        return find()
            ?? throw new InvalidOperationException(
                $"no {what} reached the collector on port {port} within {deadline.TotalSeconds:0}s "
                + $"(exports received: {count()})");
    }

    /// <summary>Whether an export's raw payload names <paramref name="instrument"/>. Instrument
    /// names travel as plain UTF-8 inside the protobuf, which is all this needs, for either
    /// protocol.</summary>
    public static bool Carries(byte[] body, string instrument)
        => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(instrument)) >= 0;
}
