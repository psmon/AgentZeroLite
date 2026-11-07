using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;
using AgentOne.Tools.Cli;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-06, music-harness): "종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자".
/// AGENTS.md routes "뮤비 만들어" to harness/engine/mv-forge.md, and a todo held both
/// episodes' video ids, genre and art style. agent-one read neither; the strong model
/// designed "join two local videos with ffmpeg" for 493 s, the build looked for files
/// the design invented, and the turn asked the person to supply them.
/// </summary>
public class WorkspaceGuideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-guide-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceGuideTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoGuideFileMeansNoGuide() => Assert.Null(WorkspaceGuide.Load(_root));

    [Fact]
    public void AgentsMdIsReadAndAClaudeMdThatOnlyImportsItIsSkipped()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Rules\n\n| 뮤비 만들어 | mv-forge | harness/engine/mv-forge.md |\n");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "@AGENTS.md\n");

        var guide = WorkspaceGuide.Load(_root)!;

        Assert.Equal(["AGENTS.md"], guide.Files);
        Assert.Contains("harness/engine/mv-forge.md", guide.Text);
    }

    [Fact]
    public void ALongGuideKeepsItsHeadAndListsWhatWasCut()
    {
        var body = "# Guide\n\n## 1. Dispatch\n| 뮤비 | mv-forge |\n\n" +
                   string.Concat(Enumerable.Range(0, 60).Select(i => $"## Section {i}\n{new string('x', 200)}\n\n")) +
                   "```\n# not a heading\n```\n## Last words\nbye\n";
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), body);

        var guide = WorkspaceGuide.Load(_root, maxChars: 3_000)!;

        Assert.True(guide.Text.Length < 3_400, $"{guide.Text.Length} chars");
        Assert.Contains("| 뮤비 | mv-forge |", guide.Text);
        Assert.Contains("AGENTS.md is cut here", guide.Text);
        Assert.Contains("read_file AGENTS.md", guide.Text);
        Assert.Contains("Section 20", guide.Text[guide.Text.IndexOf("is cut here", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("not a heading", guide.Text);
    }

    [Fact]
    public void TheSystemPromptCarriesTheGuideAsTheOwnersRules()
    {
        var prompt = SystemPrompt.Build(_root, guide: new WorkspaceGuideText(["AGENTS.md"], "| 뮤비 만들어 | mv-forge |"));

        Assert.Contains("own instructions for agents (AGENTS.md)", prompt);
        Assert.Contains("| 뮤비 만들어 | mv-forge |", prompt);
        Assert.Contains("read_file the documents they point to BEFORE planning", prompt);
    }
}

public class WorkspaceProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-probe-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "harness", "todo"));
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "harness", "todo", "candidates.md"),
            "# 후보\n| 역사 여행 #2 | 종말의 그날 1: 요한계시록 | aYJzXlgX1i0 |\n| 3 | 종말의 그날 2부작 | 가스펠 블루스 |\n");
        // Generic words only: "진행", "작성" are in every request and many files.
        for (var i = 0; i < 6; i++)
            File.WriteAllText(Path.Combine(_root, "notes", $"n{i}.md"), "작업을 진행하고 문서를 작성한다. 뮤비 작업 기록.\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "readme.md"), "종말의 그날 종말의 그날\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("종말의", "종말")]
    [InlineData("그날", "그날")]
    [InlineData("통합해", "통합")]
    [InlineData("MV", "mv")]
    public void AKoreanWordLosesItsParticle(string word, string stem) => Assert.Equal(stem, WorkspaceProbe.Stem(word));

    [Fact]
    public void TermsAreTheRequestsNamesAndPhrasesKeepTheirParticle()
    {
        var (terms, phrases) = WorkspaceProbe.TermsOf("종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자..");

        Assert.Contains("종말", terms);
        Assert.Contains("그날", terms);
        Assert.DoesNotContain("1", terms);
        Assert.DoesNotContain("진행", terms);
        Assert.Contains("종말의 그날", phrases);
    }

    [Fact]
    public void TheFileThatNamesTheThingRanksFirstAndIgnoredFoldersAreNotSearched()
    {
        var result = WorkspaceProbe.Search(_root, "종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자..");

        Assert.True(result.Found);
        Assert.Equal("harness/todo/candidates.md", result.Hits[0].Path);
        Assert.Contains("종말의 그날", result.Hits[0].Text);
        Assert.DoesNotContain(result.Hits, h => h.Path.StartsWith("node_modules", StringComparison.Ordinal));
        Assert.Contains("harness/todo/candidates.md:2:", result.Material());
    }

    [Fact]
    public void CommonWordsAlonePointAtNothing()
    {
        // "작업", "뮤비" are in every note: a match on them says nothing about which file is meant.
        Assert.False(WorkspaceProbe.Search(_root, "뮤비 작업 기록 정리해줘").Found);
    }

    [Fact]
    public void ARequestWithNothingToSearchForFindsNothing()
    {
        Assert.False(WorkspaceProbe.Search(_root, "진행해줘").Found);
        Assert.False(WorkspaceProbe.Search(_root, "quantum chromodynamics lattice").Found);
    }
}

/// <summary>The probe in a turn: material for the model and the design, and the hand-back check.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class WorkspaceGroundingSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    private const string Request = "종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자..";
    private const string Todo = "harness/todo/candidates.md";

    public WorkspaceGroundingSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-ground-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(Path.Combine(_root, "harness", "todo"));
        File.WriteAllText(Path.Combine(_root, "harness", "todo", "candidates.md"),
            "# 후보\n| 3 | 종말의 그날 2부작 | 가스펠 블루스 | 뒤러 목판화 2D |\n");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Agents\n\n| 뮤비 만들어 | mv-forge | harness/engine/mv-forge.md |\n");
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

    private const string HandBack =
        """{"tool":"final","args":{"text":"assets/in 폴더가 비어 있습니다. 원본 영상 두 개와 음악 파일을 넣어주시면 바로 진행하겠습니다."}}""";

    [Fact]
    public async Task TheSystemPromptCarriesAgentsMdAndTheTurnCarriesWhatTheWorkspaceHolds()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"확인했습니다."}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace), Choose(SmartRouter.ClaimsBacked), Choose(SmartRouter.Proceeds));
        using var session = Session(basic, engine);

        await session.SubmitAsync(Request, CancellationToken.None);

        var first = basic.Calls[0];
        Assert.Contains("harness/engine/mv-forge.md", first[0].Content);          // system prompt
        Assert.Contains("[workspace]", first[^1].Content);                         // the turn's prompt
        Assert.Contains(Todo, first[^1].Content);
    }

    [Fact]
    public async Task AHandBackWithoutLookingIsSentBackWithTheFilesThatHoldTheAnswer()
    {
        var basic = new ScriptedChatProvider(
            HandBack,
            $$$"""{"tool":"read_file","args":{"path":"{{{Todo}}}"}}""",
            """{"tool":"final","args":{"text":"후보 3번(가스펠 블루스, 뒤러 목판화 2D)으로 진행합니다."}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace),   // route
            Choose(SmartRouter.ClaimsBacked),      // claims: nothing false in it
            Choose(SmartRouter.HandsBack));        // hand-back
        using var session = Session(basic, engine);

        var run = await session.SubmitAsync(Request, CancellationToken.None);

        Assert.Contains(SmartRouter.HandBackQuestion, engine.Questions);
        Assert.Contains(run!.Steps, s => s.Tool == "handed-back");
        Assert.Contains(basic.Calls[1], m => m.Content.StartsWith("[check] Your answer hands the work back") && m.Content.Contains(Todo));
        Assert.StartsWith("후보 3번", run.Text);
    }

    [Fact]
    public async Task ATurnThatReadTheFilesIsNotSentToLookAgainOnlyToGoAhead()
    {
        // It looked, so the workspace's files are not handed to it again; it
        // changed nothing and asked the user, so it is told to go ahead instead.
        var basic = new ScriptedChatProvider(
            $$$"""{"tool":"read_file","args":{"path":"{{{Todo}}}"}}""",
            HandBack,
            """{"tool":"final","args":{"text":"후보 3번 자료로 진행합니다."}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace), Choose(SmartRouter.ClaimsBacked), Choose(SmartRouter.HandsBack));
        using var session = Session(basic, engine);

        await session.SubmitAsync(Request, CancellationToken.None);

        Assert.DoesNotContain(basic.Calls, call => call.Any(m => m.Content.Contains("you have not looked at what this workspace")));
        Assert.Contains(basic.Calls[2], m => m.Content.StartsWith("[check] Your answer stops and hands the work"));
    }

    [Fact]
    public void TheDesignIsToldTheGuideAndTheFilesAndNotToAssumeInputs()
    {
        using var session = Session(new ScriptedChatProvider("unused"), new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace)));
        var probe = WorkspaceProbe.Search(_root, Request);

        var context = session.DesignContext(probe);

        Assert.Contains("harness/engine/mv-forge.md", context);
        Assert.Contains(Todo, context);
        Assert.Contains("Never assume a file, asset or input exists", ReasoningSubtask.DesignSystemPrompt("small", "pwsh"));
    }

    [Fact]
    public async Task AWebLookupThatReportsThePageIsNotEscalated()
    {
        // Measured (2026-11-06): eleven titles sorted into history/science, escalated
        // at 0.94, the same table back 169 s later. cli_guide stands in for a page:
        // a route-exempt tool that returns material without the network.
        const string report = "git status --short, git log --oneline, git diff --stat, git blame — read freely, change carefully.";
        var basic = new ScriptedChatProvider(
            """{"tool":"cli_guide","args":{"name":"git"}}""",
            $$$"""{"tool":"final","args":{"text":"{{{report}}}"}}""");
        var strong = new ScriptedChatProvider("should not be asked");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.SearchWeb, 1.0),
            Choose(SmartRouter.ClaimsBacked),
            Choose(SmartRouter.EscalateOption, 0.94));
        using var session = Session(basic, engine, strong);

        var run = await session.SubmitAsync("git 으로 상태 보는 명령들 찾아서 정리해줘", CancellationToken.None);

        Assert.Equal(report, run!.Text);
        Assert.Equal(0, strong.CallCount);
        Assert.Contains(SmartRouter.EscalationQuestion, engine.Questions);
    }

    [Theory]
    [InlineData(0.86, 0)]   // measured: the second analysis run, escalated at 0.86 for 206 s
    [InlineData(0.97, 1)]   // a web turn whose draft reasons beyond its pages, the engine all but certain
    public async Task AWebTurnWhoseDraftReasonsBeyondThePageEscalatesOnlyWhenTheEngineIsAllButCertain(double confidence, int strongCalls)
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"cli_guide","args":{"name":"git"}}""",
            """{"tool":"final","args":{"text":"마이크로서비스에서는 사가 패턴과 이벤트 소싱으로 최종 일관성을 맞추는 편이 낫습니다."}}""",
            """{"tool":"final","args":{"text":"사가 패턴을 권합니다."}}""");
        var strong = new ScriptedChatProvider("사가 패턴이 맞습니다.");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.SearchWeb, 1.0),
            Choose(SmartRouter.ClaimsBacked),
            Choose(SmartRouter.EscalateOption, confidence));
        using var session = Session(basic, engine, strong);

        var run = await session.SubmitAsync("마이크로서비스로 옮길 때 데이터 일관성은 어떻게 지켜?", CancellationToken.None);

        Assert.Equal(strongCalls, strong.CallCount);
        if (strongCalls > 0)   // the escalated answer keeps the turn's own work on record
            Assert.Contains(run!.Steps, s => s.Tool == "cli_guide");
    }

    [Theory]
    [InlineData("고흐의 첫 직업 | 인물사", "고흐의 첫 직업은 화가가 아니었다? 그림 팔다 잘린 청년", 0.6)]
    [InlineData("사가 패턴과 이벤트 소싱으로 최종 일관성을 맞춥니다", "Saga pattern overview", 0.0)]
    public void ADraftsShareOfThePagesWords(string draft, string material, double atLeast)
    {
        var share = ChatSession.ShareFromMaterial(draft, material);
        if (atLeast > 0) Assert.True(share >= atLeast, $"{share}");
        else Assert.Equal(0, share);
    }

    // ------------------------------------------------ analysis → work, in one session

    private const string Analysis =
        "샌디의 지식탐험 채널의 최신 영상 11개를 기준으로 분석한 결과입니다.\n\n" +
        "### 📜 역사 / 인문 계열\n\n| 영상 제목 | 세부 분류 |\n| :--- | :--- |\n" +
        "| **종말의 그날 2: “그 날과 그 때는 아무도 모르나니” \\| 샌디와 떠나는 역사 여행 #3** | 종교사 / 세계사 |\n" +
        "| **종말의 그날 1: 요한계시록엔 뭐라고 적혀 있을까? \\| 샌디와 떠나는 역사 여행 #2** | 종교사 / 세계사 |\n";

    [Fact]
    public async Task TheDesignForAWorkRequestSeesTheAnalysisItBuildsOn()
    {
        // Measured (2026-11-06): the analysis was a table, the Digest keeps an
        // answer's first line and numbered items, and the design pass saw
        // "…11개를 기준으로 분석한 결과입니다." and nothing of the two episodes.
        var basic = new ScriptedChatProvider(
            $$$"""{"tool":"final","args":{"text":{{{System.Text.Json.JsonSerializer.Serialize(Analysis)}}}}}""",
            """{"tool":"final","args":{"text":"설계대로 시작합니다."}}""");
        var strong = new ScriptedChatProvider("1. harness/engine/mv-forge.md 를 읽는다.");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly),     // turn 1: route
            Choose(SmartRouter.ClaimsBacked),       //          claims
            Choose(SmartRouter.KeepDraft),          //          escalation
            Choose(SmartRouter.WorkInWorkspace),    // turn 2: route
            Choose(SmartRouter.NeedsDesign),        //          scope
            Choose(SmartRouter.ClaimsBacked));      //          claims
        using var session = Session(basic, engine, strong);

        await session.SubmitAsync("샌디 채널 영상들 카테고리별로 분류해줘", CancellationToken.None);
        await session.SubmitAsync("종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자..", CancellationToken.None);

        var designRequest = strong.Calls[0][^1].Content;
        Assert.Contains("Earlier answers in this conversation", designRequest);
        Assert.Contains("종말의 그날 1: 요한계시록엔 뭐라고 적혀 있을까?", designRequest);
        Assert.Contains("종말의 그날 2: “그 날과 그 때는 아무도 모르나니”", designRequest);
    }

    [Fact]
    public void MemoryKeepsAnAnswersSubstanceNotItsMarkup()
    {
        var text = ChatSession.MemoryText(Analysis);

        Assert.DoesNotContain(":---", text);
        Assert.DoesNotContain("**", text);
        Assert.Contains("종말의 그날 2: “그 날과 그 때는 아무도 모르나니” | 샌디와 떠나는 역사 여행 #3 · 종교사 / 세계사", text);
        Assert.Contains("종말의 그날 1: 요한계시록", text[..Math.Min(text.Length, ChatSession.MemoryOutcomeChars)]);
    }

    [Fact]
    public void LookedUpFactsCountAsKnowledgeWorthKeeping() =>
        Assert.Contains("titles, dates, ids or links found on a page", SmartRouter.WorthSavingOptions[0].Description);

    [Fact]
    public void TheReasoningPassSeesEarlierAnswersToo() =>
        Assert.StartsWith("Earlier answers", ReasoningSubtask.Request("뮤비", "", "draft", "Earlier answers in this conversation …"));
}

public class ShellAndModuleToolTests
{
    [Theory]
    [InlineData(ShellDialect.Pwsh)]
    [InlineData(ShellDialect.WindowsPowerShell)]
    public void PowerShellIsToldToParenthesiseCommandsInExpressions(ShellDialect dialect) =>
        Assert.Contains("(Test-Path a) -and (Test-Path b)", new ShellInfo("x", dialect, "").Hints);

    private static string? Find(string name) => name == "python" ? @"C:\py\python.exe" : name == "git" ? @"C:\git\git.exe" : null;

    [Fact]
    public void AToolInstalledOnlyAsAPythonModuleIsFoundAndRunThroughPython()
    {
        var status = KnownCli.Detect(Find, (python, module) => python == @"C:\py\python.exe" && module == "yt_dlp");
        var ytdlp = status.Single(s => s.Tool.Name == "yt-dlp");

        Assert.True(ytdlp.Installed);
        Assert.Equal("python -m yt_dlp", ytdlp.Command);
        Assert.Contains("run it as `python -m yt_dlp`", KnownCli.PromptLines(status));
    }

    [Fact]
    public void WithoutTheModuleItStaysMissing()
    {
        var status = KnownCli.Detect(Find, (_, _) => false);

        Assert.False(status.Single(s => s.Tool.Name == "yt-dlp").Installed);
        Assert.True(status.Single(s => s.Tool.Name == "git").Installed);
    }

    [Fact]
    public async Task TheGuideSaysHowToRunAModuleAndHowToGetTitlesInTheChannelsLanguage()
    {
        var status = KnownCli.Detect(Find, (_, module) => module == "yt_dlp");
        var belt = new KnownCliToolbelt(status);

        var result = await belt.InvokeAsync(new ToolCall { Tool = "cli_guide", Args = { ["name"] = "yt_dlp" } }, CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Contains("installed as a Python module", result.Text);
        Assert.Contains("`python -m yt_dlp`", result.Text);
        Assert.Contains("Accept-Language", result.Text);
    }
}

/// <summary>A design-carrying run is not ended by a model re-reading what it has.</summary>
public class BuildingRepeatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-build-" + Guid.NewGuid().ToString("N")[..8]);

    public BuildingRepeatTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "todo.md"), "| 3 | 종말의 그날 2부작 |\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Read = """{"tool":"read_file","args":{"path":"todo.md"}}""";

    private static ScriptedChatProvider Script() => new(
        Read, Read, Read,
        """{"tool":"write_file","args":{"path":"lore.md","content":"# 종말의 그날"}}""",
        """{"tool":"final","args":{"text":"lore.md 를 썼습니다."}}""");

    [Fact]
    public async Task ABuildGetsMoreRepeatNudgesAndTheyNameTheNextStep()
    {
        var provider = Script();
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 10) { Root = _root, Building = true };

        var run = await loop.RunAsync("[design] 1. todo.md 를 읽는다 2. lore.md 를 쓴다");

        Assert.True(run.Succeeded, run.Text);
        Assert.True(File.Exists(Path.Combine(_root, "lore.md")));
        Assert.Contains(provider.Calls[2], m => m.Content.Contains("Carry on with the design"));
    }

    [Fact]
    public async Task AnOrdinaryRunStillEndsOnTheSecondRepeat()
    {
        var loop = new AgentLoop(Script(), new LocalFileToolbelt(_root), maxSteps: 10) { Root = _root };

        var run = await loop.RunAsync("todo.md 읽어줘");

        Assert.Equal(StopReason.Repeat, run.Reason);
    }
}

public class GemmaAssignmentTests
{
    [Theory]
    [InlineData("""<|tool_call>call:run_command{command:="Get-Content AGENTS.md"}<tool_call|>""")]
    [InlineData("""<|tool_call>call:run_command{command="Get-Content AGENTS.md"}<tool_call|>""")]
    [InlineData("""<|tool_call>call:run_command{command:"Get-Content AGENTS.md"}<tool_call|>""")]
    public void AGemmaArgumentWrittenWithAnEqualsSignIsStillAString(string raw)
    {
        // Measured (2026-11-06): `command:="…"` reached PowerShell as `="Get-Content AGENTS.md"`.
        Assert.True(ToolCall.TryParse(raw, out var call, out var error), error);
        Assert.Equal("run_command", call.Tool);
        Assert.Equal("Get-Content AGENTS.md", call.Arg("command"));
    }

    [Fact]
    public void TheDesignAndThePromptSayToReadFilesWithReadFile()
    {
        Assert.Contains("never a shell command (Get-Content, cat, Select-String) for reading", ReasoningSubtask.DesignSystemPrompt("small", "pwsh"));
        Assert.Contains("Read a file with read_file", SystemPrompt.Build("."));
    }
}
