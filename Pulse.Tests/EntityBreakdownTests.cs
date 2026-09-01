using Xunit;

namespace Pulse.Tests;

public class EntityBreakdownTests
{
    private static long Value(IReadOnlyList<KeyValuePair<string, long>> values, string code)
        => values.Single(entry => entry.Key == code).Value;

    [Fact]
    public void Refresh_Counts_EachCode()
    {
        EntityBreakdown breakdown = new(10);

        IReadOnlyList<KeyValuePair<string, long>> values =
            breakdown.Refresh(["drifter", "chicken", "drifter"]);

        Assert.Equal(2, Value(values, "drifter"));
        Assert.Equal(1, Value(values, "chicken"));
    }

    [Fact]
    public void Refresh_Keeps_TheBusiestCodesOnly_AndSumsTheRestIntoOther()
    {
        EntityBreakdown breakdown = new(2);

        IReadOnlyList<KeyValuePair<string, long>> values = breakdown.Refresh(
            ["drifter", "drifter", "drifter", "chicken", "chicken", "wolf", "bear"]);

        Assert.Equal(3, Value(values, "drifter"));
        Assert.Equal(2, Value(values, "chicken"));
        Assert.Equal(2, Value(values, EntityBreakdown.OtherCode));
        Assert.DoesNotContain(values, entry => entry.Key == "wolf");
    }

    [Fact]
    public void Refresh_Publishes_Other_EvenWhenNothingIsLeftOver()
    {
        EntityBreakdown breakdown = new(10);

        Assert.Equal(0, Value(breakdown.Refresh(["drifter"]), EntityBreakdown.OtherCode));
    }

    [Fact]
    public void Refresh_Publishes_OnlyOther_ForAnEmptyWorld()
    {
        EntityBreakdown breakdown = new(10);

        IReadOnlyList<KeyValuePair<string, long>> values = breakdown.Refresh([]);

        Assert.Equal(new KeyValuePair<string, long>(EntityBreakdown.OtherCode, 0), Assert.Single(values));
    }

    /// <summary>The reason this class is stateful. A gauge keeps whatever value it last got, so a
    /// code that leaves the top has to be handed an explicit zero or its series reads as a live
    /// count forever.</summary>
    [Fact]
    public void Refresh_Zeroes_ACodeThatDroppedOutOfTheTop()
    {
        EntityBreakdown breakdown = new(1);
        breakdown.Refresh(["drifter", "drifter", "chicken"]);

        IReadOnlyList<KeyValuePair<string, long>> values = breakdown.Refresh(["chicken", "chicken"]);

        Assert.Equal(2, Value(values, "chicken"));
        Assert.Equal(0, Value(values, "drifter"));
    }

    [Fact]
    public void Refresh_Zeroes_ACodeThatVanishedFromTheWorldEntirely()
    {
        EntityBreakdown breakdown = new(10);
        breakdown.Refresh(["drifter"]);

        Assert.Equal(0, Value(breakdown.Refresh(["chicken"]), "drifter"));
    }

    /// <summary>Zeroing happens once. Repeating it every refresh would leave Pulse writing a
    /// growing list of dead series for the life of the server.</summary>
    [Fact]
    public void Refresh_Zeroes_ADroppedCode_OnceAndThenForgetsIt()
    {
        EntityBreakdown breakdown = new(10);
        breakdown.Refresh(["drifter"]);
        breakdown.Refresh(["chicken"]);

        Assert.DoesNotContain(breakdown.Refresh(["chicken"]), entry => entry.Key == "drifter");
    }

    [Fact]
    public void Refresh_Does_NotZeroACodeThatIsStillPublished()
    {
        EntityBreakdown breakdown = new(10);
        breakdown.Refresh(["drifter"]);

        IReadOnlyList<KeyValuePair<string, long>> values = breakdown.Refresh(["drifter", "drifter"]);

        Assert.Equal(2, Value(values, "drifter"));
    }

    /// <summary>Two codes on the same count must not swap places between refreshes: that would
    /// flap one series to zero and back for no reason at all.</summary>
    [Fact]
    public void Refresh_Breaks_TiesOnTheCode_SoTheSelectionIsStable()
    {
        EntityBreakdown first = new(1);
        EntityBreakdown second = new(1);

        Assert.Equal(
            first.Refresh(["wolf", "bear"]),
            second.Refresh(["bear", "wolf"]));
    }

    [Fact]
    public void Refresh_Counts_EverythingIntoOther_WhenTheLimitIsZero()
    {
        EntityBreakdown breakdown = new(0);

        IReadOnlyList<KeyValuePair<string, long>> values = breakdown.Refresh(["drifter", "chicken"]);

        Assert.Equal(new KeyValuePair<string, long>(EntityBreakdown.OtherCode, 2), Assert.Single(values));
    }
}
