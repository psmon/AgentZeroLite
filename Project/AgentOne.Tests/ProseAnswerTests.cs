using AgentOne.Agent;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// A reply with no envelope is usually the answer, not a mistake. Two real
/// sessions lost a minute each to "reply with ONE JSON object" nudges for text
/// that was already the answer. This is the rule that decides when prose is
/// taken as final and when it still earns a nudge.
/// </summary>
public class ProseAnswerTests : IDisposable
{
    private readonly string _root;

    public ProseAnswerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-one-prose-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentLoop Loop(params string[] replies) =>
        new(new ScriptedChatProvider(replies), new LocalFileToolbelt(_root), maxSteps: 6);

    private static readonly string LongProse =
        "마이크로서비스 아키텍처를 구성하는 좋은 방법은 단순히 애플리케이션을 작은 서비스로 분해하는 것을 넘어, " +
        "시스템 전체의 설계 방식과 운영 문화에 근본적인 변화를 가져오는 접근 방식입니다. 도메인 경계를 먼저 정하세요.";

    [Fact]
    public async Task LongProseWithNoEnvelopeIsTheAnswer()
    {
        var run = await Loop(LongProse).RunAsync("MSA를 구성하는 좋은 방법은?");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal(LongProse, run.Text);
        Assert.Contains(run.Steps, s => s.Tool == "unwrapped");
        Assert.DoesNotContain(run.Steps, s => s.Tool == "(unparsed)");   // no nudge spent
    }

    [Fact]
    public async Task ProseAfterAToolCallIsTheAnswerHoweverShort()
    {
        // Once the model has done its work, what it says next is what it has to say.
        var run = await Loop(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            "It says hello.").RunAsync("what does the file say?");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("It says hello.", run.Text);
    }

    [Fact]
    public async Task AShortAnnouncementBeforeAnyToolStillGetsTheNudge()
    {
        // "Let me search for that" is intent, not an answer. The nudge stands.
        var run = await Loop(
            "Let me search for that.",
            """{"tool":"final","args":{"text":"found it"}}""").RunAsync("what is X?");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("found it", run.Text);
        Assert.Contains(run.Steps, s => s.Tool == "(unparsed)");
    }

    [Fact]
    public async Task AnEmptyReplyIsNeverAnAnswer()
    {
        var run = await Loop("   ", """{"tool":"final","args":{"text":"ok"}}""").RunAsync("hi");

        Assert.Equal("ok", run.Text);
        Assert.Contains(run.Steps, s => s.Tool == "(unparsed)");
    }

    [Fact]
    public void TheThresholdIsWhereShortAnnouncementsEnd()
    {
        var none = new List<AgentStep>();

        Assert.False(AgentLoop.LooksLikeAnAnswer(new string('x', AgentLoop.ProseAnswerMinChars - 1), none));
        Assert.True(AgentLoop.LooksLikeAnAnswer(new string('x', AgentLoop.ProseAnswerMinChars), none));
    }

    [Fact]
    public void NudgesDoNotCountAsToolSteps()
    {
        // A failed parse is not "work done"; it must not unlock the short-prose path.
        var steps = new List<AgentStep> { new(1, "(unparsed)", "no JSON", false) };

        Assert.False(AgentLoop.LooksLikeAnAnswer("ok then", steps));
    }

    [Fact]
    public async Task StepsCarryTheirOwnTiming()
    {
        var run = await Loop(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"done"}}""").RunAsync("read it");

        // Every real step is timed; the value can legitimately be 0 ms on a fake
        // provider, so the assertion is that the field exists on the right steps.
        Assert.All(run.Steps.Where(s => s.Tool != ToolCall.FinalTool), s => Assert.True(s.ElapsedMs >= 0));
    }
}

[Collection(AgentOneHomeCollection.Name)]
public class SessionLoggingTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public SessionLoggingTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-log-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void APromptRecordsWhichModeItRanIn()
    {
        var session = SessionStore.Create("chat");
        session.Prompt("hi", smart: true);
        session.Prompt("again", smart: false);

        var lines = File.ReadAllLines(session.Path);
        Assert.Contains("\"mode\":\"smart\"", lines[0]);
        Assert.Contains("\"mode\":\"basic\"", lines[1]);
    }

    [Fact]
    public void AStepRecordsHowLongItTook()
    {
        var session = SessionStore.Create("run");
        session.Step(new AgentStep(1, "web_read", "url=x -> 100 chars", true, 843));

        Assert.Contains("\"elapsedMs\":843", File.ReadAllText(session.Path));
    }

    [Fact]
    public void APlanRecordsTheDecisionAndWhetherItSteered()
    {
        var decision = new Llm.Decision.Decision(true, "search_web", 0.91,
            new Dictionary<string, double> { ["search_web"] = 0.95, ["read_local"] = 0.05 }, "ok", 310);
        var plan = new SmartPlan(
            [new("read_local", "Read."), new("search_web", "Search."), new(SmartTurn.ReviewOption, SmartTurn.ReviewDescription)],
            decision, Confident: true);

        var session = SessionStore.Create("chat");
        session.Plan(plan);

        var line = File.ReadAllText(session.Path);
        Assert.Contains("\"kind\":\"plan\"", line);
        Assert.Contains("\"tool\":\"search_web\"", line);
        Assert.Contains("\"ok\":true", line);
        Assert.Contains("steering", line);
        Assert.Contains("0.91", line);
        Assert.Contains("\"elapsedMs\":310", line);
    }

    [Fact]
    public void AReviewDecisionSaysAPersonWasNeeded()
    {
        var decision = new Llm.Decision.Decision(true, SmartTurn.ReviewOption, 0.68,
            new Dictionary<string, double> { [SmartTurn.ReviewOption] = 0.7 }, "ok", 300);
        var plan = new SmartPlan([new("x", "X."), new(SmartTurn.ReviewOption, SmartTurn.ReviewDescription)], decision, false);

        var session = SessionStore.Create("chat");
        session.Plan(plan);

        Assert.Contains("needs a person", File.ReadAllText(session.Path));
    }
}
