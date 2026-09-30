using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// A follow-up that points at the last answer. Measured (2026-09-30): after an
/// answer ending "3. You can now delete count_files.ps1", the user wrote
/// "3번 수행 이제 필요없음"; the router saw only those words and ruled out every
/// tool, the model read it as "don't", the file stayed, and a two-line
/// acknowledgement was escalated for 31 s. The answer before it was in English.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class FollowUpSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    private const string ListAnswer =
        """{"tool":"final","args":{"text":"스크립트를 만들고 실행했습니다.\n\n**다음 단계:**\n1. bin, obj 도 제외할 수 있습니다.\n2. 파일 종류별로 나눠 볼 수 있습니다.\n3. 필요 없으면 `count_files.ps1` 을 삭제할 수 있습니다."}}""";

    public FollowUpSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-followup-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Decision Choose(string choice, double confidence = 0.9) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine, ScriptedChatProvider? reasoning = null)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        if (reasoning is not null) config.TrySet("reasoningModel", "big-model", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, true, reasoning)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false, ChecksClaims = false };
    }

    [Fact]
    public async Task ANumberedFollowUpCarriesTheItemToTheRouterAndTheModel()
    {
        var basic = new ScriptedChatProvider(ListAnswer, """{"tool":"final","args":{"text":"삭제할까요?"}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly), Choose(SmartRouter.WorkInWorkspace));
        using var session = Session(basic, engine);

        await session.SubmitAsync("파일 개수 세는 스크립트 만들어줘", CancellationToken.None);
        await session.SubmitAsync("3번 수행 이제 필요없음", CancellationToken.None);

        var routeState = engine.States[1];
        Assert.Contains("count_files.ps1", routeState);
        Assert.Contains("your last answer", routeState);

        var sent = basic.Calls[1][^1].Content;
        Assert.Contains("[reference]", sent);
        Assert.Contains("item 3", sent);
        Assert.Contains("삭제할 수 있습니다", sent);
        Assert.Contains("ask one short question", sent);
    }

    [Theory]
    [InlineData("3번 해줘", 3)]
    [InlineData("go with option 2", 2)]
    [InlineData("#1 please", 1)]
    public async Task TheWaysANumberIsNamedAllResolve(string followUp, int number)
    {
        var basic = new ScriptedChatProvider(ListAnswer);
        using var session = Session(basic, new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly)));
        await session.SubmitAsync("파일 개수 세는 스크립트 만들어줘", CancellationToken.None);

        Assert.Contains($"item {number} of your previous answer", session.ResolveReference(followUp));
    }

    [Theory]
    [InlineData("버전 1.2.3 으로 올려")]
    [InlineData("7번 항목")]            // there is no item 7
    [InlineData("그냥 고마워요")]
    public async Task NoReferenceIsInventedWhereThereIsNone(string followUp)
    {
        var basic = new ScriptedChatProvider(ListAnswer);
        using var session = Session(basic, new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly)));
        await session.SubmitAsync("파일 개수 세는 스크립트 만들어줘", CancellationToken.None);

        Assert.Null(session.ResolveReference(followUp));
    }

    [Fact]
    public async Task AnUnsureEscalationOfAShortExchangeKeepsTheDraft()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"알겠습니다. 그대로 두겠습니다."}}""");
        var strong = new ScriptedChatProvider("never asked");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly), Choose(SmartRouter.EscalateOption, 0.14));
        using var session = Session(basic, engine, strong);
        var notes = new List<SmartNote>();
        session.Decided += notes.Add;

        var run = await session.SubmitAsync("3번 수행 이제 필요없음", CancellationToken.None);

        Assert.Equal("알겠습니다. 그대로 두겠습니다.", run!.Text);
        Assert.Empty(strong.Calls);
        Assert.Contains(notes, n => n.Kind == "escalation" && n.Verdict.Contains("short exchange"));
    }

    [Fact]
    public async Task AConfidentEscalationOfAShortExchangeStillGoes()
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"391"}}""",
            """{"tool":"final","args":{"text":"17 × 23 = 391"}}""");
        var strong = new ScriptedChatProvider("17 × 23 = 391");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly), Choose(SmartRouter.EscalateOption, 0.9));
        using var session = Session(basic, engine, strong);

        await session.SubmitAsync("17 곱하기 23 은 얼마야?", CancellationToken.None);

        Assert.Single(strong.Calls);
    }
}

/// <summary>The answer is in the language the user wrote in.</summary>
public class AnswerLanguageTests
{
    private const string English = "The script count_files.ps1 was created and executed successfully; there are 6,749 files.";

    [Theory]
    [InlineData("이 워크스페이스의 파일개수를 알려주는 스크립트 만들어서 실행한번해죠", English, "Korean")]   // measured
    [InlineData("このフォルダのファイル数を数えて", English, "Japanese")]
    [InlineData("Сколько файлов в этой папке?", English, "Russian")]
    public void AnAnswerWithoutTheRequestsScriptIsCaught(string request, string answer, string language) =>
        Assert.Equal(language, AgentLoop.WrongScript(request, answer));

    [Theory]
    [InlineData("파일 개수를 알려줘", "`count_files.ps1` 을 실행했고 파일은 6,749개입니다. Get-ChildItem -Recurse 를 썼습니다.")]
    [InlineData("count the files please", English)]
    [InlineData("파일 개수를 알려줘", "6749")]                                   // too short to judge
    [InlineData("README.md 읽어줘", "The README.md file describes AgentZero Lite as a shell for agent CLIs.")] // mostly Latin request
    public void OtherwiseNothingIsSaid(string request, string answer) =>
        Assert.Null(AgentLoop.WrongScript(request, answer));

    [Fact]
    public async Task TheLoopAsksOnceForTheAnswerInTheUsersLanguage()
    {
        var provider = new ScriptedChatProvider(
            ToolCall.Final(English).ToJson(),
            """{"tool":"final","args":{"text":"count_files.ps1 을 만들어 실행했고, 파일은 6,749개입니다."}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(Path.GetTempPath()), maxSteps: 4)
        {
            Request = "이 워크스페이스의 파일개수를 알려주는 스크립트 만들어서 실행한번해죠"
        };

        var run = await loop.RunAsync("[route guidance in English] " + loop.Request);

        Assert.StartsWith("count_files.ps1 을 만들어", run.Text);
        Assert.Contains(run.Steps, s => s.Tool == "language" && !s.Ok);
        Assert.Contains("in Korean", provider.Calls[1][^1].Content);
    }
}
