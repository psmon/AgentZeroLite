using Agent.Common.Llm.Tools;
using Xunit;

namespace ZeroCommon.Tests;

public sealed class GemmaNativeToolCallTests
{
    [Theory]
    [InlineData("<|tool_call>call: list_terminals{}<tool_call|>", "{\"tool\":\"list_terminals\",\"args\":{}}")]
    [InlineData("<|tool_call>call:list_terminals{}<tool_call|>", "{\"tool\":\"list_terminals\",\"args\":{}}")]
    [InlineData("Sure.\n<|tool_call>call: read_terminal{\"group\":0,\"tab\":2}<tool_call|>", "{\"tool\":\"read_terminal\",\"args\":{\"group\":0,\"tab\":2}}")]
    [InlineData("<|tool_call>call: done{\"message\":\"a } in a string\"}", "{\"tool\":\"done\",\"args\":{\"message\":\"a } in a string\"}}")]
    [InlineData("<|tool_call>send_to_terminal{\"text\":\"dir\"}<tool_call|>", "{\"tool\":\"send_to_terminal\",\"args\":{\"text\":\"dir\"}}")]
    public void Converts_native_syntax_to_the_envelope(string raw, string expected)
    {
        Assert.True(GemmaNativeToolCall.TryConvert(raw, out var json));
        Assert.Equal(expected, json);
        var call = LocalAgentLoop.ParseToolCall(json);
        Assert.NotEmpty(call.Tool);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"tool\":\"done\",\"args\":{}}")]
    [InlineData("just prose without a call")]
    [InlineData("<|tool_call>call: {}")]
    [InlineData("<|tool_call>call: read_terminal{\"unterminated\":1")]
    public void Leaves_everything_else_alone(string? raw)
    {
        Assert.False(GemmaNativeToolCall.TryConvert(raw, out _));
    }

    [Fact]
    public async Task External_loop_accepts_native_syntax_without_spending_a_correction()
    {
        var provider = new ExternalAgentLoopTests.ScriptedProvider(new[]
        {
            "<|tool_call>call: list_terminals{}<tool_call|>",
            "{\"tool\":\"done\",\"args\":{\"message\":\"no terminals\"}}",
        });
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions { MaxIterations = 4 };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);
        var run = await loop.RunAsync("what terminals do I have?");
        Assert.True(run.TerminatedCleanly, run.FailureReason);
        Assert.Equal("no terminals", run.FinalMessage);
        Assert.Equal(0, loop.FormatCorrectionsUsed);
    }
}
