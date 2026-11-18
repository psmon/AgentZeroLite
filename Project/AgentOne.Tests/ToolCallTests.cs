using AgentOne.Agent;

namespace AgentOne.Tests;

/// <summary>
/// The envelope parser is the one place a small model's sloppiness is absorbed,
/// so these cases are the actual shapes models emit, not tidy inputs.
/// </summary>
public class ToolCallTests
{
    [Fact]
    public void ParsesBareEnvelope()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"read_file","args":{"path":"a.txt"}}""", out var call, out _));
        Assert.Equal("read_file", call.Tool);
        Assert.Equal("a.txt", call.Arg("path"));
        Assert.False(call.IsFinal);
    }

    [Fact]
    public void AQuotationInsideFileContentIsText()
    {
        // Measured (2026-11-18): gemma's lyrics notes quoted their own hook — "'0xEB' is invalid after
        // a value" — and the model blamed its em dashes for 24 000 characters of reasoning.
        const string raw = "{\"tool\":\"write_file\",\"args\":{\"path\":\"songs/x/lyrics-draft-1.md\",\"content\":\"# 초안\n훅 \"네온 서울\"을 두 번 {반복}.\n| 3 | 대조 | '사라질 우리' — \"빛\"과 그림자 |\"}}";

        Assert.True(ToolCall.TryParse(raw, out var call, out var error), error);

        Assert.Equal("write_file", call.Tool);
        Assert.Equal("songs/x/lyrics-draft-1.md", call.Arg("path"));
        Assert.Equal("# 초안\n훅 \"네온 서울\"을 두 번 {반복}.\n| 3 | 대조 | '사라질 우리' — \"빛\"과 그림자 |", call.Arg("content"));
    }

    [Fact]
    public void AnUnevenQuotationStillFindsTheEnvelopesEnd()
    {
        const string raw = "{\"tool\":\"write_file\",\"args\":{\"path\":\"a.md\",\"content\":\"he said \"stop} here\"}}\nDone.";

        Assert.True(ToolCall.TryParse(raw, out var call, out var error), error);

        Assert.Equal("he said \"stop} here", call.Arg("content"));
    }

    [Fact]
    public void ParsesFencedJson()
    {
        var raw = """
            Sure! Here is the call:
            ```json
            {"tool":"list_files","args":{"path":"src"}}
            ```
            """;
        Assert.True(ToolCall.TryParse(raw, out var call, out _));
        Assert.Equal("list_files", call.Tool);
        Assert.Equal("src", call.Arg("path"));
    }

    [Fact]
    public void AWindowsPathEscapeInACommandIsPutBack()
    {
        // Measured: the model wrote ".\run.ps1"; JSON made it ".<CR>un.ps1" and PowerShell threw a ParserError.
        Assert.True(ToolCall.TryParse("""{"tool":"run_command","args":{"command":".\run.ps1 -Verbose"}}""", out var call, out _));
        Assert.Equal(@".\run.ps1 -Verbose", call.Arg("command"));

        Assert.True(ToolCall.TryParse("""{"tool":"read_file","args":{"path":"src\new\bin\tools\form.txt"}}""", out call, out _));
        Assert.Equal(@"src\new\bin\tools\form.txt", call.Arg("path"));
    }

    [Fact]
    public void RealNewlinesAndTabsSurviveWhereTheyAreMeant()
    {
        var raw = """{"tool":"write_file","args":{"path":"a.py","content":"if x:\n\treturn 1\r\n"}}""";
        Assert.True(ToolCall.TryParse(raw, out var call, out _));
        Assert.Equal("if x:\n\treturn 1\r\n", call.Arg("content"));

        raw = """{"tool":"run_command","args":{"command":"echo one\necho two"}}""";
        Assert.True(ToolCall.TryParse(raw, out call, out _));
        Assert.Equal("echo one\necho two", call.Arg("command"));
    }

    [Fact]
    public void RecognizesFinal()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"final","args":{"text":"done"}}""", out var call, out _));
        Assert.True(call.IsFinal);
        Assert.Equal("done", call.Arg("text"));
    }

    [Fact]
    public void CoercesNonStringArgsToText()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"read_file","args":{"path":"a.txt","lines":40,"raw":true}}""", out var call, out _));
        Assert.Equal("40", call.Arg("lines"));
        Assert.Equal("true", call.Arg("raw"));
    }

    [Fact]
    public void KeepsBracesInsideStrings()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"final","args":{"text":"use {braces} freely"}}""", out var call, out _));
        Assert.Equal("use {braces} freely", call.Arg("text"));
    }

    [Fact]
    public void KeepsEscapedQuotesInsideStrings()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"final","args":{"text":"he said \"hi\""}}""", out var call, out _));
        Assert.Equal("he said \"hi\"", call.Arg("text"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I cannot do that.")]
    [InlineData("{\"args\":{\"path\":\"a\"}}")]
    [InlineData("{\"tool\":\"\"}")]
    [InlineData("{\"tool\":\"read_file\",")]
    public void RejectsUnusableReplies(string raw)
    {
        Assert.False(ToolCall.TryParse(raw, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void ArgsAreCaseInsensitive()
    {
        Assert.True(ToolCall.TryParse("""{"tool":"read_file","args":{"Path":"a.txt"}}""", out var call, out _));
        Assert.Equal("a.txt", call.Arg("path"));
    }

    [Fact]
    public void SignatureIgnoresArgumentOrder()
    {
        ToolCall.TryParse("""{"tool":"x","args":{"a":"1","b":"2"}}""", out var first, out _);
        ToolCall.TryParse("""{"tool":"x","args":{"b":"2","a":"1"}}""", out var second, out _);
        Assert.Equal(first.Signature(), second.Signature());
    }

    [Fact]
    public void FinalRoundTripsThroughJson()
    {
        var json = ToolCall.Final("답변").ToJson();
        Assert.True(ToolCall.TryParse(json, out var call, out _));
        Assert.True(call.IsFinal);
        Assert.Equal("답변", call.Arg("text"));
    }
}
