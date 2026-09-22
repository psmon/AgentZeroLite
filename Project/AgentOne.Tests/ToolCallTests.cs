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
