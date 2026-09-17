using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// Synchronized output (DEC 2026) held in the host, because xterm.js does not
/// implement it and the native control this backend replaced did.
///
/// <para>Without it an Ink TUI's repaint — clear, home, redraw — is shown one step
/// at a time, and the cursor is visibly somewhere new on each step. That is what
/// "the cursor wanders around the screen" turned out to be.</para>
/// </summary>
public class SynchronizedOutputBufferTests
{
    private const string Esc = "\u001b";
    private const string Begin = Esc + "[?2026h";
    private const string End = Esc + "[?2026l";

    [Fact]
    public void PassesThroughOutputWithNoFrames()
    {
        var b = new SynchronizedOutputBuffer();
        Assert.Equal("hello", b.Append("hello"));
        Assert.False(b.IsBuffering);
        Assert.Equal(0, b.FramesCoalesced);
    }

    /// <summary>The whole point: one frame reaches the renderer as one write.</summary>
    [Fact]
    public void HoldsAFrameBackUntilItCloses()
    {
        var b = new SynchronizedOutputBuffer();

        Assert.Equal("", b.Append(Begin + "clear"));
        Assert.True(b.IsBuffering);

        Assert.Equal("", b.Append("home"));
        Assert.True(b.IsBuffering);

        var released = b.Append("draw" + End);

        Assert.Equal(Begin + "clearhomedraw" + End, released);
        Assert.False(b.IsBuffering);
        Assert.Equal(1, b.FramesCoalesced);
    }

    [Fact]
    public void EmitsWhatCameBeforeTheFrameImmediately()
    {
        var b = new SynchronizedOutputBuffer();
        Assert.Equal("before", b.Append("before" + Begin + "inside"));
        Assert.True(b.IsBuffering);
    }

    [Fact]
    public void HandlesSeveralFramesInOneChunk()
    {
        var b = new SynchronizedOutputBuffer();
        var outp = b.Append(Begin + "one" + End + "gap" + Begin + "two" + End);

        Assert.Equal(Begin + "one" + End + "gap" + Begin + "two" + End, outp);
        Assert.False(b.IsBuffering);
        Assert.Equal(2, b.FramesCoalesced);
    }

    /// <summary>
    /// The pseudo-console chunks wherever it likes, so a marker arrives split. Miss
    /// this and the class silently does nothing at all.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void DetectsAMarkerSplitAcrossChunks(int cut)
    {
        var b = new SynchronizedOutputBuffer();
        var stream = "before" + Begin + "frame" + End + "after";

        var released = "";
        // Feed it in tiny pieces, cutting through the markers.
        for (var i = 0; i < stream.Length; i += cut)
            released += b.Append(stream.Substring(i, Math.Min(cut, stream.Length - i)));
        released += b.Flush();

        Assert.Equal(stream, released);
        Assert.Equal(1, b.FramesCoalesced);
    }

    [Fact]
    public void OneCharacterAtATime_StillCoalesces()
    {
        var b = new SynchronizedOutputBuffer();
        var stream = Begin + "x" + End;

        var released = "";
        foreach (var c in stream) released += b.Append(c.ToString());

        Assert.Equal(stream, released);
        Assert.Equal(1, b.FramesCoalesced);
    }

    // ── the guards: a frozen terminal is worse than a flickering one ──

    [Fact]
    public void Flush_ReleasesAnUnclosedFrame()
    {
        var b = new SynchronizedOutputBuffer();
        b.Append(Begin + "half a frame");

        var released = b.Flush();

        Assert.Equal(Begin + "half a frame", released);
        Assert.False(b.IsBuffering);
        Assert.Equal("", b.Flush());   // idempotent
    }

    [Fact]
    public void Flush_AlsoReleasesAHeldPartialMarker()
    {
        var b = new SynchronizedOutputBuffer();
        b.Append("text" + Esc + "[?20");    // could still become Begin
        Assert.Equal(Esc + "[?20", b.Flush());
    }

    [Fact]
    public void AFrameOverTheCap_IsForwardedAnyway()
    {
        var b = new SynchronizedOutputBuffer(maxBufferedBytes: 64);
        var released = b.Append(Begin + new string('x', 200));

        Assert.Contains(new string('x', 200), released);
        Assert.False(b.IsBuffering);
    }

    [Fact]
    public void Append_Empty_IsHarmless()
    {
        var b = new SynchronizedOutputBuffer();
        Assert.Equal("", b.Append(""));
        Assert.Equal("", b.Append(null!));
    }

    /// <summary>Nothing is lost or duplicated, frames or not.</summary>
    [Fact]
    public void TheStreamIsPreservedExactly()
    {
        var b = new SynchronizedOutputBuffer();
        var stream = "a" + Begin + "b" + End + "c" + Begin + "d" + End + "e";

        var released = "";
        for (var i = 0; i < stream.Length; i += 2)
            released += b.Append(stream.Substring(i, Math.Min(2, stream.Length - i)));
        released += b.Flush();

        Assert.Equal(stream, released);
    }
}
