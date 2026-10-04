using Vintagestory.API.Common;

namespace Pulse.Tests.Fakes;

/// <summary>Captures every entry through the one abstract hook LoggerBase funnels its whole
/// friendly API (Warning, Error, ...) through, so it needs no server and no mod loader.</summary>
internal sealed class FakeLogger : LoggerBase
{
    private readonly object gate = new();
    private readonly List<(EnumLogType Type, string Message)> entries = [];

    public IReadOnlyList<(EnumLogType Type, string Message)> Entries
    {
        get { lock (gate) { return entries.ToList(); } }
    }

    protected override void LogImpl(EnumLogType logType, string format, object[] args)
    {
        string message = args is { Length: > 0 } ? string.Format(format, args) : format;
        lock (gate)
        {
            entries.Add((logType, message));
        }
    }
}
