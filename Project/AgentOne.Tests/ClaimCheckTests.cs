using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// A report of work the tools never did. Measured (2026-09-29): asked to write
/// and run unit tests, the model called no tool and answered that it had created
/// Project/ZeroCommon.Tests/WebAutomationTests.csproj and run the tests; the
/// strong model polished that into a completion report, and three false facts
/// were learned from it. The file check never ran — no tool, no "report" — and
/// the .csproj extension was not one it knew.
/// </summary>
public class ClaimCheckLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-claims-" + Guid.NewGuid().ToString("N")[..8]);

    public ClaimCheckLoopTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ProjectFilesAreRecognisedAsFiles()
    {
        var paths = AgentLoop.FilePathsIn("Project/ZeroCommon.Tests/WebAutomationTests.csproj 와 App.sln, MainWindow.xaml");

        Assert.Contains("Project/ZeroCommon.Tests/WebAutomationTests.csproj", paths);
        Assert.Contains("App.sln", paths);
        Assert.Contains("MainWindow.xaml", paths);
    }

    [Fact]
    public async Task AnAnswerTakenAsProseIsCheckedLikeAFinalOne()
    {
        // Prose after a tool call used to return straight out of the loop, so a
        // model that dropped its JSON — the 4b one did, nine times in one
        // session — skipped every check on its report.
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            "Done. I created src/WebFetcherService.cs and wired it into the project.",
            """{"tool":"final","args":{"text":"Nothing was created; src/WebFetcherService.cs is only a proposal."}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 6) { Root = _root };

        var run = await loop.RunAsync("wire in a fetcher service");

        Assert.Contains(run.Steps, s => s.Tool == "unwritten");
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task FilesStillMissingAfterTheNudgesAreShownOnTheAnswer()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            """{"tool":"final","args":{"text":"Created src/a.cs."}}""",
            """{"tool":"final","args":{"text":"Created src/a.cs."}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 6) { Root = _root };

        var run = await loop.RunAsync("make a.cs");

        Assert.True(run.Succeeded);
        Assert.EndsWith("⚠ not on disk: src/a.cs", run.Text);
        Assert.Contains(run.Steps, s => s.Tool == "missing" && !s.Ok);
    }
}

/// <summary>The claim question after a turn: its answer against the turn's tool calls.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class ClaimCheckSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public ClaimCheckSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-claimsess-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
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
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    }

    private const string FalseReport =
        """{"tool":"final","args":{"text":"테스트 프로젝트를 만들고 유닛 테스트를 수행했습니다. 5개 모두 통과했습니다."}}""";

    [Fact]
    public async Task AReportNoToolBacksIsSentBackOnceAndAnHonestAnswerStands()
    {
        var basic = new ScriptedChatProvider(
            FalseReport,
            """{"tool":"final","args":{"text":"아직 테스트를 작성하거나 실행하지 않았습니다."}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly),     // route
            Choose(SmartRouter.UnbackedRun),        // claims, first answer
            Choose(SmartRouter.ClaimsBacked));      // claims, after the correction
        using var session = Session(basic, engine);

        var run = await session.SubmitAsync("작성한 코드에대해 테스트 코드작성하고 유닛테스트수행", CancellationToken.None);

        Assert.Equal("아직 테스트를 작성하거나 실행하지 않았습니다.", run!.Text);
        Assert.Null(run.Unverified);
        Assert.Contains(run.Steps, s => s.Tool == "unbacked");
        Assert.Contains(basic.Calls[1], m => m.Content.StartsWith("[check]") && m.Content.Contains("run_command"));
        Assert.Contains(SmartRouter.ClaimQuestion, engine.Questions);
    }

    [Fact]
    public async Task AReportThatStillDoesNotHoldIsMarkedAndNeitherEscalatedNorRememberedAsDone()
    {
        var basic = new ScriptedChatProvider(FalseReport, FalseReport);
        var strong = new ScriptedChatProvider("should not be asked");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly),
            Choose(SmartRouter.UnbackedRun),
            Choose(SmartRouter.UnbackedRun),
            Choose(SmartRouter.EscalateOption));
        using var session = Session(basic, engine, strong);

        var run = await session.SubmitAsync("작성한 코드에대해 테스트 코드작성하고 유닛테스트수행", CancellationToken.None);

        Assert.NotNull(run!.Unverified);
        Assert.Contains("⚠ unverified", run.Text);
        Assert.Equal(0, strong.CallCount);
        Assert.DoesNotContain(SmartRouter.EscalationQuestion, engine.Questions);
        Assert.Contains("UNVERIFIED", session.Workspace.ReadMemory());
    }

    [Fact]
    public async Task TheEnginesVerdictIsNotActedOnWhenTheRecordShowsTheWork()
    {
        // "Unbacked change" with a successful write_file in hand is the engine
        // misreading; the record wins, and nothing is sent back.
        var basic = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"a.cs","content":"class A {}"}}""",
            """{"tool":"final","args":{"text":"a.cs 를 만들었습니다."}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace),
            Choose(SmartRouter.SmallTask),
            Choose(SmartRouter.UnbackedChange));
        using var session = Session(basic, engine);

        var run = await session.SubmitAsync("a.cs 파일을 하나 만들어줘", CancellationToken.None);

        Assert.Equal("a.cs 를 만들었습니다.", run!.Text);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "unbacked");
        Assert.Equal(2, basic.CallCount);
    }

    [Fact]
    public async Task ATurnThatWroteAndRanIsNotAsked()
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"a.txt","content":"x"}}""",
            """{"tool":"run_command","args":{"command":"echo hi"}}""",
            """{"tool":"final","args":{"text":"a.txt 를 만들고 echo 를 실행했습니다."}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace),   // no reasoning model, so no scope question
            Choose(SmartRouter.SafeOption),
            Choose(SmartRouter.UnbackedRun));
        using var session = Session(basic, engine);

        await session.SubmitAsync("a.txt 만들고 echo hi 실행해", CancellationToken.None);

        Assert.DoesNotContain(SmartRouter.ClaimQuestion, engine.Questions);
    }
}
