namespace Pulse;

/// <summary>Pairs the engine's suspend and resume signals into one measurable pause.</summary>
/// <remarks>The engine does not call the suspend handler once per suspend. It polls it every 10 ms
/// for as long as it is still waiting for its own threads to park, so a single autosave fires the
/// handler anywhere from once to hundreds of times. Opening the window is therefore idempotent and
/// only the resume closes it, which is also the only signal that arrives exactly once. A resume
/// with no window open is ignored: it means the suspend never got as far as polling anything,
/// which the engine's own suspend-timeout warning already reports.</remarks>
internal sealed class SuspendBookkeeper
{
    private double startSeconds = -1;

    public void Open(double nowSeconds)
    {
        if (startSeconds < 0)
        {
            startSeconds = nowSeconds;
        }
    }

    /// <summary>Closes an open window and returns how long it lasted, or null when none was open.</summary>
    public double? Close(double nowSeconds)
    {
        if (startSeconds < 0)
        {
            return null;
        }

        double seconds = nowSeconds - startSeconds;
        startSeconds = -1;
        return seconds;
    }
}
