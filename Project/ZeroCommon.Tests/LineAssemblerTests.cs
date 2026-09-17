using System.Text;
using Agent.Common.Wearable;

namespace ZeroCommon.Tests;

/// <summary>
/// The wearable link's inbound text path. These exist because the original implementation
/// decoded each BLE notification as its own UTF-8 string, which quietly destroyed any
/// character that straddled a notification boundary — a bug that only shows up once the MTU
/// is small enough to split often, i.e. after a watch reboots and reconnects on the 23-byte
/// default.
/// </summary>
public class LineAssemblerTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Append_WholeLine_ReturnsIt()
    {
        var assembler = new LineAssembler();
        Assert.Equal(new[] { "R {\"t\":\"ping\"}" }, assembler.Append(Utf8("R {\"t\":\"ping\"}\n")));
    }

    [Fact]
    public void Append_LineSplitAcrossChunks_ReturnsItOnce()
    {
        var assembler = new LineAssembler();
        Assert.Empty(assembler.Append(Utf8("R {\"t\":")));
        Assert.Equal(new[] { "R {\"t\":\"ping\"}" }, assembler.Append(Utf8("\"ping\"}\n")));
    }

    /// <summary>The regression this class was written for.</summary>
    [Fact]
    public void Append_MultiByteCharSplitAcrossChunks_SurvivesIntact()
    {
        const string line = "R {\"text\":\"안녕하세요 반갑습니다\"}";
        var bytes = Utf8(line + "\n");

        // Cut in the middle of a Hangul syllable: '안' is 3 bytes starting at index 11.
        var head = bytes[..13];
        var tail = bytes[13..];

        var assembler = new LineAssembler();
        Assert.Empty(assembler.Append(head));
        var lines = assembler.Append(tail);

        Assert.Equal(new[] { line }, lines);
        Assert.DoesNotContain('�', lines[0]);
    }

    [Fact]
    public void Append_OneBytePerChunk_StillReassembles()
    {
        const string line = "A {\"text\":\"대화가 이어집니다\",\"done\":true}";
        var assembler = new LineAssembler();
        var collected = new List<string>();
        foreach (var b in Utf8(line + "\n"))
            collected.AddRange(assembler.Append(new[] { b }));

        Assert.Equal(new[] { line }, collected);
    }

    [Fact]
    public void Append_SeveralLinesInOneChunk_ReturnsAllInOrder()
    {
        var assembler = new LineAssembler();
        Assert.Equal(new[] { "one", "two", "three" }, assembler.Append(Utf8("one\ntwo\nthree\n")));
    }

    [Fact]
    public void Append_StripsCarriageReturnAndSkipsBlankLines()
    {
        var assembler = new LineAssembler();
        Assert.Equal(new[] { "S {}", "E {}" }, assembler.Append(Utf8("S {}\r\n\r\n\nE {}\r\n")));
    }

    [Fact]
    public void Append_TrailingPartialLine_IsHeldBack()
    {
        var assembler = new LineAssembler();
        Assert.Equal(new[] { "first" }, assembler.Append(Utf8("first\nsecond-without-newline")));
        Assert.Equal(22, assembler.Pending);
        Assert.Equal(new[] { "second-without-newline!" }, assembler.Append(Utf8("!\n")));
    }

    [Fact]
    public void Append_LineOverLimit_IsDiscardedThroughItsNewline()
    {
        var assembler = new LineAssembler(maxBytes: 32);
        Assert.Empty(assembler.Append(Utf8(new string('x', 100))));
        Assert.True(assembler.Discarded > 0);

        // The rest of that oversized line goes with it: a fragment would only corrupt
        // whatever it got glued to.
        Assert.Empty(assembler.Append(Utf8("...and its tail\n")));

        // The next line starts clean.
        Assert.Equal(new[] { "recovered" }, assembler.Append(Utf8("recovered\n")));
    }

    [Fact]
    public void Reset_DropsAHalfReceivedLine()
    {
        var assembler = new LineAssembler();
        assembler.Append(Utf8("half of a line"));
        assembler.Reset();

        Assert.Equal(0, assembler.Pending);
        Assert.Equal(new[] { "fresh" }, assembler.Append(Utf8("fresh\n")));
    }
}
