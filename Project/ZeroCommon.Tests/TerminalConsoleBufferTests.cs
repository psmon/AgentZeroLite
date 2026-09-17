using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// The contract both terminal backends must meet for output. These exist because
/// the two backends disagreed: the ConPTY one returned the visible screen from
/// GetConsoleText() while the WebViewXterm one returned the entire transcript, so
/// "the backend is interchangeable" was false in exactly the method the approval
/// parser and the agent-state monitor depend on.
/// </summary>
public class TerminalConsoleBufferTests
{
    private static string Lines(int from, int to)
        => string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"line{i}")) + "\n";

    // ── the stream half: OutputLength / ReadOutput ──

    [Fact]
    public void Append_GrowsLength_AndReadReturnsTheWindow()
    {
        var buf = new TerminalConsoleBuffer();
        buf.Append("hello ");
        buf.Append("world");

        Assert.Equal(11, buf.Length);
        Assert.Equal("hello world", buf.FullText);
        Assert.Equal("world", buf.Read(6, 5));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)]
    [InlineData(99, 5)]
    public void Read_OutOfRange_ReturnsEmpty(int start, int length)
    {
        var buf = new TerminalConsoleBuffer();
        buf.Append("hello");
        Assert.Equal("", buf.Read(start, length));
    }

    [Fact]
    public void Read_PastTheEnd_ClampsInsteadOfThrowing()
    {
        var buf = new TerminalConsoleBuffer();
        buf.Append("hello");
        Assert.Equal("llo", buf.Read(2, 999));
    }

    // ── the screen half: GetConsoleText ──

    [Fact]
    public void GetConsoleText_WithSnapshot_ReturnsTheViewport_NotTheTranscript()
    {
        var buf = new TerminalConsoleBuffer();
        buf.Append(Lines(1, 500));
        buf.SetScreenSnapshot("line499\nline500\n$ ");

        var text = buf.GetConsoleText();

        Assert.Equal("line499\nline500\n$ ", text);
        Assert.DoesNotContain("line1\n", text);
    }

    /// <summary>The regression: an approval prompt answered long ago must not keep matching.</summary>
    [Fact]
    public void GetConsoleText_AfterTheScreenMovesOn_NoLongerShowsTheOldPrompt()
    {
        var buf = new TerminalConsoleBuffer();
        buf.Append("Do you want to proceed? (y/n)\n");
        buf.SetScreenSnapshot("Do you want to proceed? (y/n)");
        Assert.Contains("proceed?", buf.GetConsoleText());

        buf.Append(Lines(1, 300));
        buf.SetScreenSnapshot("line299\nline300\n$ ");

        Assert.DoesNotContain("proceed?", buf.GetConsoleText());
        Assert.Contains("proceed?", buf.FullText);   // still in the transcript, as it should be
    }

    [Fact]
    public void GetConsoleText_WithoutSnapshot_FallsBackToABoundedTail()
    {
        var buf = new TerminalConsoleBuffer(fallbackLines: 10);
        buf.Append(Lines(1, 500));

        var text = buf.GetConsoleText();

        Assert.Equal(10, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("line500", text);
        Assert.DoesNotContain("line1\n", text);
    }

    [Fact]
    public void GetConsoleText_ShorterThanTheWindow_ReturnsEverything()
    {
        var buf = new TerminalConsoleBuffer(fallbackLines: 10);
        buf.Append("only\ntwo\n");
        Assert.Equal("only\ntwo\n", buf.GetConsoleText());
    }

    [Fact]
    public void GetConsoleText_Empty_ReturnsEmpty()
    {
        Assert.Equal("", new TerminalConsoleBuffer().GetConsoleText());
    }

    [Fact]
    public void SetScreenSnapshot_Null_FallsBackAgainRatherThanServingAFrozenScreen()
    {
        var buf = new TerminalConsoleBuffer(fallbackLines: 5);
        buf.Append(Lines(1, 100));
        buf.SetScreenSnapshot("frozen");
        Assert.Equal("frozen", buf.GetConsoleText());
        Assert.True(buf.HasScreenSnapshot);

        buf.SetScreenSnapshot(null);

        Assert.False(buf.HasScreenSnapshot);
        Assert.Contains("line100", buf.GetConsoleText());
        Assert.DoesNotContain("frozen", buf.GetConsoleText());
    }

    [Fact]
    public void GetConsoleText_IsBounded_WhileTheTranscriptGrows()
    {
        var buf = new TerminalConsoleBuffer(fallbackLines: 20);
        buf.Append(Lines(1, 50));
        var small = buf.GetConsoleText().Length;

        buf.Append(Lines(51, 5000));
        var large = buf.GetConsoleText().Length;

        // The transcript grew by orders of magnitude; the screen did not.
        Assert.True(buf.FullText.Length > 40_000, $"transcript should be large, was {buf.FullText.Length}");
        Assert.True(large < small * 3, $"screen text should stay bounded: {small} -> {large}");
    }
}
