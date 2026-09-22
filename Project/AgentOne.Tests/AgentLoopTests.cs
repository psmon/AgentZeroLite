using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

public class AgentLoopTests : IDisposable
{
    private readonly string _root;

    public AgentLoopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-one-loop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "AgentOne is a CLI agent.\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentLoop Loop(int maxSteps, params string[] replies) =>
        new(new ScriptedChatProvider(replies), new LocalFileToolbelt(_root), maxSteps);

    [Fact]
    public async Task AnswersImmediatelyWhenModelCallsFinal()
    {
        var run = await Loop(8, """{"tool":"final","args":{"text":"hello"}}""").RunAsync("hi");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("hello", run.Text);
        Assert.Equal(0, run.ExitCode);
        Assert.Single(run.Steps);
    }

    [Fact]
    public async Task FeedsToolResultBackBeforeFinalAnswer()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"it is a CLI agent"}}""");

        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 8);
        var run = await loop.RunAsync("what is this?");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal(2, run.Steps.Count);
        Assert.Equal("read_file", run.Steps[0].Tool);

        // The second call must have seen the file contents as a user message.
        var secondCall = provider.Calls[1];
        Assert.Contains(secondCall, m => m.Role == "user" && m.Content.Contains("AgentOne is a CLI agent"));
        Assert.Contains(secondCall, m => m.Role == "user" && m.Content.StartsWith("[tool:read_file]"));
    }

    [Fact]
    public async Task StopsWhenModelRepeatsTheSameCall()
    {
        var run = await Loop(8, """{"tool":"read_file","args":{"path":"README.md"}}""").RunAsync("loop forever");

        Assert.Equal(StopReason.Repeat, run.Reason);
        Assert.NotEqual(0, run.ExitCode);
    }

    [Fact]
    public async Task NudgesOnceBeforeGivingUpOnARepeat()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"recovered"}}""");

        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 8);
        var run = await loop.RunAsync("go");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("recovered", run.Text);
    }

    [Fact]
    public async Task StopsWhenTheModelNeverEmitsAnEnvelope()
    {
        var run = await Loop(8, "I am just chatting, no JSON here.").RunAsync("hi");

        Assert.Equal(StopReason.ParseFailure, run.Reason);
        Assert.NotEqual(0, run.ExitCode);
    }

    [Fact]
    public async Task RecoversFromOneUnparseableReply()
    {
        var provider = new ScriptedChatProvider(
            "oops, prose",
            """{"tool":"final","args":{"text":"fixed"}}""");

        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 8);
        var run = await loop.RunAsync("hi");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("fixed", run.Text);
    }

    [Fact]
    public async Task StopsAtTheStepBudget()
    {
        // Each reply is a distinct call, so the repeat guard never fires.
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"list_files","args":{"path":"./"}}""");

        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 2);
        var run = await loop.RunAsync("keep going");

        Assert.Equal(StopReason.MaxSteps, run.Reason);
        Assert.Equal(2, run.Steps.Count);
    }

    [Fact]
    public async Task ReportsProviderFailureWithoutThrowing()
    {
        var loop = new AgentLoop(new FailingChatProvider("no route to host"), new LocalFileToolbelt(_root));
        var run = await loop.RunAsync("hi");

        Assert.Equal(StopReason.ProviderError, run.Reason);
        Assert.Contains("no route to host", run.Text);
    }

    [Fact]
    public async Task ToolFailureIsHandedBackToTheModelRatherThanEndingTheRun()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"../escape.txt"}}""",
            """{"tool":"final","args":{"text":"cannot read that"}}""");

        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 8);
        var run = await loop.RunAsync("read outside");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.False(run.Steps[0].Ok);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("escapes the workspace root"));
    }

    [Fact]
    public async Task ConversationCarriesAcrossRunsUntilReset()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 4);

        await loop.RunAsync("first");
        await loop.RunAsync("second");

        Assert.Contains(provider.Calls[1], m => m.Role == "user" && m.Content == "first");

        loop.Reset();
        await loop.RunAsync("third");
        Assert.DoesNotContain(provider.Calls[2], m => m.Content == "first");
    }

    [Fact]
    public async Task SystemPromptIsTheFirstMessage()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root));

        await loop.RunAsync("hi");

        Assert.Equal("system", provider.Calls[0][0].Role);
        Assert.Contains("agent-one", provider.Calls[0][0].Content);
    }

    [Fact]
    public async Task CancelledRunReportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = await Loop(8, """{"tool":"final","args":{"text":"never"}}""").RunAsync("hi", cts.Token);

        Assert.Equal(StopReason.Cancelled, run.Reason);
        Assert.Equal(130, run.ExitCode);
    }
}
