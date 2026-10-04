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

    /// <summary>Whether an export's raw payload holds <paramref name="key"/> set to <paramref
    /// name="value"/>, as one string attribute. An attribute is a KeyValue message: the key as
    /// field 1, then the value as field 2, itself an AnyValue holding the string as its field 1,
    /// each with a length in front. The pair is therefore one unbroken run of bytes, which is worth
    /// matching whole: two loose searches for the key and for the value could each be satisfied by
    /// something else in the payload (the value is also the service name, say), and neither would
    /// say the id sits under its own key. Parsing the message to find out would only test a
    /// protobuf library, which is the reason the metric names above are found the same way.</summary>
    public static bool CarriesAttribute(byte[] body, string key, string value)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        // A length under 128 is one byte on the wire, which is all a scenario's names ever need;
        // a longer one is a varint of two, and a wrong match here would be silent.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyBytes.Length, 127);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valueBytes.Length, 125);

        byte[] keyValue =
        [
            0x0A, (byte)keyBytes.Length, .. keyBytes,
            0x12, (byte)(valueBytes.Length + 2), 0x0A, (byte)valueBytes.Length, .. valueBytes,
        ];
        return body.AsSpan().IndexOf(keyValue) >= 0;
    }
}
