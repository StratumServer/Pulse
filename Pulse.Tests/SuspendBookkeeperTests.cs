using Xunit;

namespace Pulse.Tests;

public class SuspendBookkeeperTests
{
    [Fact]
    public void Close_Returns_TheWindowLength()
    {
        SuspendBookkeeper bookkeeper = new();
        bookkeeper.Open(10.0);

        Assert.Equal(1.5, bookkeeper.Close(11.5));
    }

    /// <summary>The engine polls the suspend handler every 10 ms while it waits for its threads to
    /// park, so one autosave opens the window many times over. Only the first one may count.</summary>
    [Fact]
    public void Open_Keeps_TheFirstTimestamp_WhenTheEnginePollsAgain()
    {
        SuspendBookkeeper bookkeeper = new();
        bookkeeper.Open(10.0);
        bookkeeper.Open(10.01);
        bookkeeper.Open(10.02);

        Assert.Equal(2.0, bookkeeper.Close(12.0));
    }

    [Fact]
    public void Close_Returns_Null_WhenNoWindowIsOpen()
    {
        SuspendBookkeeper bookkeeper = new();

        Assert.Null(bookkeeper.Close(10.0));
    }

    [Fact]
    public void Close_Returns_Null_OnASecondResumeForTheSameWindow()
    {
        SuspendBookkeeper bookkeeper = new();
        bookkeeper.Open(10.0);
        bookkeeper.Close(11.0);

        Assert.Null(bookkeeper.Close(12.0));
    }

    [Fact]
    public void Open_Starts_AFreshWindow_AfterTheLastOneClosed()
    {
        SuspendBookkeeper bookkeeper = new();
        bookkeeper.Open(10.0);
        bookkeeper.Close(11.0);

        bookkeeper.Open(20.0);

        Assert.Equal(0.5, bookkeeper.Close(20.5));
    }

    /// <summary>A suspend that resolves inside one poll is a real, and common, case: the counter
    /// has to move for it, not just the accumulated seconds.</summary>
    [Fact]
    public void Close_Returns_AZeroLengthWindow_RatherThanNothing()
    {
        SuspendBookkeeper bookkeeper = new();
        bookkeeper.Open(10.0);

        Assert.Equal(0.0, bookkeeper.Close(10.0));
    }
}
