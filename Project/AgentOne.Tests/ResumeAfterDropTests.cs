using System.Net;
using System.Text;
using System.Text.Json;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-06, music-harness): a long MV build lost its connection
/// mid-answer, after it had written the lore file. The IOException escaped the
/// loop; the turn vanished with no result and no memory, and "재개" worked only
/// because the same process still held the conversation. After a restart and
/// /resume, neither the request nor the file would have been known.
/// </summary>
public class DroppedStreamTests
{
    [Fact]
    public async Task AConnectionThatDropsMidAnswerIsARetryableStallNotARawIOException()
    {
        var config = new AgentConfig();
        config.TrySet("baseUrl", "http://drop.test/v1", out _);
        using var provider = new OpenAiCompatChatProvider(config, new DroppingHandler());

        var stall = await Assert.ThrowsAsync<ChatProviderStalledException>(() =>
            provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { }));

        Assert.True(stall.Dropped);
        Assert.Equal(5, stall.ReceivedChars);
        Assert.Contains("connection dropped", stall.Message);
    }

    [Fact]
    public async Task WhateverAProviderLetsThroughTheTurnEndsWithItsSteps()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-one-drop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var provider = new ThrowAfter(
                new ScriptedChatProvider("""{"tool":"write_file","args":{"path":"lore.md","content":"# lore"}}"""),
                answers: 1, () => new IOException("The response ended prematurely."));
            var loop = new AgentLoop(provider, new LocalFileToolbelt(root), maxSteps: 5) { Root = root };

            var run = await loop.RunAsync("뮤비 만들어");

            Assert.Equal(StopReason.ProviderError, run.Reason);
            Assert.Contains(run.Steps, s => s.Tool == "write_file" && s.Ok);
            Assert.Contains(run.Steps, s => s.Tool == "provider-error" && s.Detail.Contains("IOException"));
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private sealed class DroppingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) });
    }

    /// <summary>One chunk, then the connection is gone.</summary>
    private sealed class DroppingStream : Stream
    {
        private bool _sent;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent) throw new IOException("The response ended prematurely.");
            _sent = true;
            var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n");
            bytes.CopyTo(buffer, offset);
            return bytes.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var array = new byte[buffer.Length];
            var n = Read(array, 0, array.Length);
            array.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Answers from <paramref name="inner"/> <paramref name="answers"/> times, then throws.</summary>
internal sealed class ThrowAfter(IChatProvider inner, int answers, Func<Exception> error) : IChatProvider
{
    private int _calls;
    public string Name => "throw-after";

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null) =>
        ++_calls > answers ? throw error() : inner.CompleteAsync(messages, ct, onDelta);
}

[Collection(AgentOneHomeCollection.Name)]
public class ResumeUnfinishedTurnTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public ResumeUnfinishedTurnTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-resume-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(IChatProvider provider, bool smart = false)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", smart ? "on" : "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, new ScriptedDecisionEngine(
                new Decision(true, SmartRouter.AnswerDirectly, 0.9, new Dictionary<string, double>(), "ok", 1)), smart)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    }

    private static string Line(string kind, string text, string? tool = null, bool? ok = null) =>
        JsonSerializer.Serialize(new SessionEntry { Timestamp = "2026-11-06T14:40:00Z", Kind = kind, Text = text, Tool = tool, Ok = ok },
            AgentOneWireJson.Default.SessionEntry);

    [Fact]
    public async Task AResumedSessionCarriesTheCutOffTurnsRequestProgressAndDesign()
    {
        string path;
        using (var first = Session(new ScriptedChatProvider("unused")))
        {
            path = Path.Combine(first.Workspace.SessionsDir, "20261106-233850-chat.jsonl");
            File.WriteAllLines(path,
            [
                Line("prompt", "샌디 채널 영상 카테고리별로 분석해줘"),
                Line("result", "역사 7편: 종말의 그날 1, 종말의 그날 2 …", "Final", true),
                Line("prompt", "종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자.."),
                Line("step", "1. AGENTS.md 와 mv-forge.md 를 읽는다 2. lore 를 쓴다 3. 가사를 쓴다", "design-text", true),
                Line("step", "path=harness/knowledge/lore/apocalypse-day-combined.md content=# lore", "write_file", true)
                // …and the connection dropped: no result.
            ]);
        }

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"이어서 가사를 씁니다."}}""");
        using var session = Session(provider);
        session.Resume(path);
        await session.SubmitAsync("재개", CancellationToken.None);

        // Restored answers are final envelopes; read them as the model does, decoded.
        var context = string.Join("\n", provider.Calls[0].Select(m =>
            m.Role == "assistant" && ToolCall.TryParse(m.Content, out var call, out _) ? call.Arg("text") : m.Content));
        Assert.Contains("종말의 그날 1,2 를 통합해 뮤비작성을 진행해보자..", context);
        Assert.Contains("This turn did not finish — it was cut off", context);
        Assert.Contains("write_file: path=harness/knowledge/lore/apocalypse-day-combined.md", context);
        Assert.Contains("2. lore 를 쓴다", context);
        Assert.Contains("역사 7편", context);                                   // the finished turn, as before
    }

    [Fact]
    public async Task ADroppedConnectionIsRetriedWithoutAskingAnyone()
    {
        var inner = new ScriptedChatProvider("""{"tool":"final","args":{"text":"answered"}}""");
        var dropping = new DropFirst(inner, drops: 2);
        using var session = Session(dropping);
        session.DroppedRetryDelay = _ => TimeSpan.Zero;
        var asked = 0;
        session.Chooser = (_, _) => { asked++; return Task.FromResult(""); };

        var run = await session.SubmitAsync("what does this project do?", CancellationToken.None);

        Assert.Equal("answered", run!.Text);
        Assert.Equal(0, asked);
        Assert.Equal(3, dropping.Calls);
    }

    private sealed class DropFirst(IChatProvider inner, int drops) : IChatProvider
    {
        public int Calls { get; private set; }
        public string Name => "drop-first";

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null) =>
            ++Calls <= drops
                ? throw new ChatProviderStalledException("the connection dropped mid-answer", 0, new IOException("reset"), dropped: true)
                : inner.CompleteAsync(messages, ct, onDelta);
    }
}

public class AnswerScriptShareTests
{
    [Fact]
    public void AnEnglishAnswerWhoseOnlyHangulIsATitleIsTheWrongLanguage()
    {
        // Measured (2026-11-06): "재개" was answered in English; 「종말의 그날」 alone passed the old check.
        const string answer = "The project files for '종말의 그날 (Apocalypse Day)' have been successfully generated and are " +
                              "now available in the workspace. I have completed the integration of the two source videos.";
        Assert.Equal("Korean", AgentLoop.WrongScript("재개", answer));
    }

    [Fact]
    public void AKoreanAnswerFullOfFileNamesAndCodeIsFine()
    {
        const string answer = "`songs/2026-11-07-apocalypse-day/lyrics.md`, `guide.md`, `mv/plan.md` 를 작성했습니다.\n" +
                              "```powershell\nGet-ChildItem songs/2026-11-07-apocalypse-day -Recurse | Select-Object FullName\n```\n" +
                              "다음 단계: Suno 에 guide.md 의 Style 블록을 붙여넣어 곡을 생성합니다.";
        Assert.Null(AgentLoop.WrongScript("재개", answer));
    }

    [Fact]
    public void TheResumeNoteLeavesOutTheTurnBeforesLearning()
    {
        var note = ChatSession.UnfinishedNote("cut off", [
            new SessionEntry { Kind = "step", Tool = "learned", Text = "(fact) 채널 카테고리 분석" },
            new SessionEntry { Kind = "step", Tool = "write_file", Ok = true, Text = "path=lore.md" }], null);
        Assert.DoesNotContain("채널 카테고리 분석", note);
        Assert.Contains("write_file: path=lore.md", note);
    }
}

/// <summary>
/// Measured (2026-11-07): gemma-4 on LM Studio streams reasoning_content before
/// its answer; one step sat 44 minutes on "thinking about what came back" —
/// every reasoning frame reset the idle clock, and nothing reached the screen.
/// </summary>
public class RunawayReplyTests
{
    private static string Frame(string field, string text) =>
        "data: {\"choices\":[{\"delta\":{\"" + field + "\":" + JsonSerializer.Serialize(text) + "}}]}\n\n";

    private static OpenAiCompatChatProvider Provider(string body, int thinkingLimit = 60_000)
    {
        var config = new AgentConfig();
        config.TrySet("baseUrl", "http://loop.test/v1", out _);
        return new OpenAiCompatChatProvider(config, new BodyHandler(body)) { ThinkingLimitChars = thinkingLimit };
    }

    [Fact]
    public async Task ReasoningThatNeverStartsAnAnswerIsCutOffAsARetryableStall()
    {
        var body = string.Concat(Enumerable.Range(0, 400).Select(i => Frame("reasoning_content", $"step {i}: weighing option {i * 7 % 13} against {i * 11 % 17}. ")));
        using var provider = Provider(body, thinkingLimit: 5_000);

        var stall = await Assert.ThrowsAsync<ChatProviderStalledException>(() =>
            provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { }));

        Assert.True(stall.Degenerate);
        Assert.Contains("without starting an answer", stall.Message);
    }

    [Fact]
    public async Task AnAnswerStuckInACycleIsCutOff()
    {
        var body = Frame("content", "정리하면 다음과 같습니다. ") + string.Concat(Enumerable.Repeat(Frame("content", "을 아수 "), 900));
        using var provider = Provider(body);

        var stall = await Assert.ThrowsAsync<ChatProviderStalledException>(() =>
            provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { }));

        Assert.True(stall.Degenerate);
        Assert.Contains("cycle", stall.Message);
    }

    [Fact]
    public async Task ReasoningThenAnAnswerIsTheAnswer()
    {
        var body = Frame("reasoning_content", "The user wants a greeting.") + Frame("content", "안녕하세요") + "data: [DONE]\n\n";
        using var provider = Provider(body);

        Assert.Equal("안녕하세요", await provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { }));
    }

    [Fact]
    public void ProseAndLayoutAreNotLoops()
    {
        var prose = new StringBuilder(string.Concat(Enumerable.Range(0, 200).Select(i => $"Line {i}: the take at {i * 3}s keeps the hook. ")));
        var rule = new StringBuilder(new string('-', 3_000));
        Assert.Null(OpenAiCompatChatProvider.RepeatingTail(prose));
        Assert.Null(OpenAiCompatChatProvider.RepeatingTail(rule));
        Assert.Equal(4, OpenAiCompatChatProvider.RepeatingTail(new StringBuilder(string.Concat(Enumerable.Repeat("을 아수", 600)))) is { } p ? p : 0);
    }

    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

public class EngineCacheTests
{
    [Fact]
    public void AUnityProjectsGeneratedFoldersAreSkippedAndALibraryElsewhereIsNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-one-unity-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var unity = Path.Combine(root, "stage-urp");
            foreach (var d in new[] { "Assets", "ProjectSettings", "Library", "Temp" }) Directory.CreateDirectory(Path.Combine(unity, d));
            Directory.CreateDirectory(Path.Combine(root, "docs", "Library"));

            Assert.True(LocalFileToolbelt.IsEngineCache(unity, Path.Combine(unity, "Library")));
            Assert.True(LocalFileToolbelt.IsEngineCache(unity, Path.Combine(unity, "Temp")));
            Assert.False(LocalFileToolbelt.IsEngineCache(unity, Path.Combine(unity, "Assets")));
            Assert.False(LocalFileToolbelt.IsEngineCache(Path.Combine(root, "docs"), Path.Combine(root, "docs", "Library")));
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }
}

[Collection(AgentOneHomeCollection.Name)]
public class HandBackAfterWorkTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public HandBackAfterWorkTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-handback-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Decision Choose(string choice) => new(true, choice, 0.9, new Dictionary<string, double> { [choice] = 0.9 }, "ok", 1);

    [Fact]
    public async Task ATurnThatReadButChangedNothingAndAsksWhichVersionIsSentBackToGoAhead()
    {
        // Measured (2026-11-07): "재개" on a deevid generation read the guides, laid out
        // "버전 1 / 버전 2" of the three fields and asked which — nothing written, nothing run.
        var basic = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"notes.md"}}""",
            """{"tool":"final","args":{"text":"버전 1(스타일 채움)과 버전 2(비움)를 준비했습니다. 어느 버전으로 생성할지 알려주세요."}}""",
            """{"tool":"write_file","args":{"path":"guide.md","content":"# guide"}}""",
            """{"tool":"final","args":{"text":"guide.md 를 쓰고 두 버전으로 진행했습니다."}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.ClaimsBacked), Choose(SmartRouter.HandsBack), Choose(SmartRouter.ClaimsBacked));
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        using var session = new ChatSession(config, _root, streaming: false, basic, engine, true)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        var run = await session.SubmitAsync("재개", CancellationToken.None);   // short: no route, no probe

        Assert.Contains(SmartRouter.HandBackQuestion, engine.Questions);
        Assert.Contains(run!.Steps, s => s.Tool == "handed-back");
        Assert.Contains(basic.Calls[2], m => m.Content.StartsWith("[check] Your answer stops and hands the work"));
        Assert.True(File.Exists(Path.Combine(_root, "guide.md")));
    }

    [Fact]
    public async Task ATurnThatWroteAndRanButAsksThePersonToSupplyTheInputIsSentBackToo()
    {
        // Measured (2026-11-07): a review turn wrote two reports, ran ffprobe over no
        // files and ended "please upload the takes to out/" — while the workspace had
        // the script that fetches them.
        var basic = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"review.md","content":"# review"}}""",
            """{"tool":"run_command","args":{"command":"hostname"}}""",
            """{"tool":"final","args":{"text":"review.md 를 썼습니다. 오디오 파일을 out/ 에 업로드해 주시면 측정하겠습니다."}}""",
            """{"tool":"final","args":{"text":"테이크를 받아 측정했습니다."}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.SafeOption), Choose(SmartRouter.HandsBack), Choose(SmartRouter.ClaimsBacked));
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        using var session = new ChatSession(config, _root, streaming: false, basic, engine, true)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        var run = await session.SubmitAsync("재개", CancellationToken.None);

        Assert.Contains(SmartRouter.HandBackQuestion, engine.Questions);
        Assert.Contains(run!.Steps, s => s.Tool == "handed-back");
    }
}

[Collection(AgentOneHomeCollection.Name)]
public class ShortFollowUpTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public ShortFollowUpTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-follow-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(Path.Combine(_root, "tools"));
        File.WriteAllText(Path.Combine(_root, "tools", "dv-gen.cjs"),
            "// deevid.ai AI 음악 생성기 자동 투입 (Playwright + CDP Chrome)\n//   node dv-gen.cjs <guide.md> [--dry]\nconst fs = require('fs');\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AShortReplyIsToldWhichRequestItContinues()
    {
        // Measured (2026-11-07): "생성 진행해" after "종말의 그날 곡 다비드로 생성 진행해줘" became
        // "generate the MV's image assets".
        var basic = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"세 칸을 준비했습니다."}}""",
            """{"tool":"final","args":{"text":"생성합니다."}}""");
        var config = new AgentConfig();
        config.TrySet("smartMode", "off", out _);
        config.TrySet("saveSessions", "false", out _);
        using var session = new ChatSession(config, _root, streaming: false, basic, null!, false)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        await session.SubmitAsync("종말의 그날 곡 다비드로 생성 진행해줘", CancellationToken.None);
        await session.SubmitAsync("생성 진행해", CancellationToken.None);

        Assert.Contains("continues the earlier request: \"종말의 그날 곡 다비드로 생성 진행해줘\"", basic.Calls[1][^1].Content);
        Assert.DoesNotContain("[context]", basic.Calls[0][^1].Content);
    }

    [Fact]
    public void TheSafetyQuestionReadsWhatAScriptSaysAboutItself()
    {
        var notes = ChatSession.ScriptNotes("node tools/dv-gen.cjs songs/x/guide.md", _root);
        Assert.Contains("deevid.ai AI 음악 생성기 자동 투입", notes);
        Assert.Equal("", ChatSession.ScriptNotes("node ../outside.cjs", _root));
        Assert.Contains("spends money or credits", SmartRouter.SafetyOptions[1].Description);
    }

    [Fact]
    public void ThePromptSaysToRunTheWorkspacesToolNotDescribeIt() =>
        Assert.Contains("Writing instructions for the user to do it by hand is not doing it", SystemPrompt.Build("."));
}

[Collection(AgentOneHomeCollection.Name)]
public class UnbackedReviewTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public UnbackedReviewTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-review-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static Decision Choose(string choice) => new(true, choice, 0.9, new Dictionary<string, double> { [choice] = 0.9 }, "ok", 1);

    [Fact]
    public async Task ARecommendationOfTakesNoToolExaminedIsSentBack()
    {
        // Measured (2026-11-07): six takes "reviewed" from their lengths and the plan, none downloaded or measured.
        var basic = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"전문가 검토 결과 C2 버전(03:51)을 추천합니다. 도입부가 가장 어둡고 훅이 강렬합니다."}}""",
            """{"tool":"final","args":{"text":"아직 테이크를 받거나 측정하지 않았습니다. 길이만 보고는 고를 수 없습니다."}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace),                       // no reasoning model: no scope question
            Choose(SmartRouter.UnbackedReview), Choose(SmartRouter.ClaimsBacked));
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        using var session = new ChatSession(config, _root, streaming: false, basic, engine, true)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        var run = await session.SubmitAsync("생성된 곡들 검토해서 추천해줘", CancellationToken.None);

        Assert.Contains(run!.Steps, s => s.Tool == "unbacked" && s.Detail.Contains("never examined"));
        Assert.StartsWith("아직 테이크를", run.Text);
        Assert.Contains(basic.Calls[1], m => m.Content.StartsWith("[check] Your answer judges files or media") && m.Content.Contains("do it now"));
    }
}

public class NamedFileUnderAListedFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-under-" + Guid.NewGuid().ToString("N")[..8]);

    public NamedFileUnderAListedFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "songs", "x", "mv"));
        File.WriteAllText(Path.Combine(_root, "songs", "x", "mv", "plan.md"), "# plan");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ABareNameUnderAFolderTheTurnListedIsNotMissing()
    {
        // Measured (2026-11-07): after `ls -R songs/x` the answer named plan.md (songs/x/mv/plan.md),
        // was told it was not on disk, and the model replaced a good plan.
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"songs/x"}}""",
            """{"tool":"final","args":{"text":"plan.md 를 확인했습니다."}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 4) { Root = _root };

        var run = await loop.RunAsync("plan 확인해줘");

        Assert.DoesNotContain(run.Steps, s => s.Tool is "unwritten" or "missing");
    }
}

[Collection(AgentOneHomeCollection.Name)]
public class RedoPlanTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public RedoPlanTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-redo-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Decision Choose(string choice) => new(true, choice, 0.9, new Dictionary<string, double> { [choice] = 0.9 }, "ok", 1);

    [Fact]
    public async Task ARejectedTurnIsRedoneOnTheStrongModelsPlan()
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"notes.md"}}""",
            """{"tool":"final","args":{"text":"두 버전을 준비했습니다. 어느 쪽으로 할까요?"}}""",
            """{"tool":"final","args":{"text":"계획대로 진행했습니다."}}""");
        var strong = new ScriptedChatProvider("1. tools/fetch.cjs 로 받는다\n2. measure.sh 로 잰다");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace), Choose(SmartRouter.SmallTask),
            Choose(SmartRouter.ClaimsBacked), Choose(SmartRouter.HandsBack), Choose(SmartRouter.ClaimsBacked));
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        config.TrySet("reasoningModel", "big-model", out _);
        using var session = new ChatSession(config, _root, streaming: false, basic, engine, true, strong)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        await session.SubmitAsync("생성된 곡들 검토해서 추천해줘", CancellationToken.None);

        Assert.Equal(1, strong.CallCount);
        Assert.Contains("first attempt at this was rejected", strong.Calls[0][^1].Content);
        Assert.Contains(basic.Calls[2], m => m.Content.Contains("2. measure.sh 로 잰다"));
    }
}

public class PrecedentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-prec-" + Guid.NewGuid().ToString("N")[..8]);

    public PrecedentTests()
    {
        void W(string rel, string text = "x") { var f = Path.Combine(_root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text); }
        W("songs/2026-11-07-new/guide.md");
        W("songs/2026-11-05-older/guide.md"); W("songs/2026-11-05-older/out/fill-1.mp3"); W("songs/2026-11-05-older/mv/judge-takes.py");
        W("songs/2026-11-05-older/notes.md", "# notes\n- 받기: `dv-fetch.cjs out 4 --match \"Older\"`\n- 측정: `mv/judge-takes.py` 로 판정\n- 다시 `dv-fetch.cjs` 로 받음");
        W("songs/2026-11-01-oldest/guide.md");
        W("songs/2026-10-01-thin/guide.md");
        W("harness/logs/song-forge/2026-11-06-00-15-older-generated.md");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheNewestSiblingThatGotFurtherShowsItsOutputsScriptsAndLogs()
    {
        var folder = WorkspacePrecedent.WorkFolderOf(_root, "songs/2026-11-07-new/mv/plan.md");
        var material = WorkspacePrecedent.Material(_root, folder);

        Assert.Equal("songs/2026-11-07-new", folder);
        Assert.Contains("songs/2026-11-05-older/", material);
        Assert.Contains("judge-takes.py", material);
        Assert.Contains("fill-1.mp3", material);
        Assert.Contains("harness/logs/song-forge/2026-11-06-00-15-older-generated.md", material);
        Assert.DoesNotContain("2026-11-01-oldest", material);          // went no further than the new one
        Assert.Contains("how: 받기: `dv-fetch.cjs out 4", material);   // how the step was run, not only that a notes file exists
        Assert.Contains("how: 측정: `mv/judge-takes.py`", material);
        Assert.DoesNotContain("다시", material);                        // one line per script
    }

    [Fact]
    public void AFileAtTheRootHasNoWorkFolder() => Assert.Null(WorkspacePrecedent.WorkFolderOf(_root, "notes.md"));

    [Fact]
    public void AFreshSessionTakesTheWorkFolderFromWhatTheTurnLookedAtMost()
    {
        // Measured: /new, "review the takes" — the turn listed the song folder and
        // searched it, read one log, and wrote nothing.
        var steps = new[]
        {
            new AgentStep(1, "list_files", "path=songs/2026-11-07-new -> 79 chars", true),
            new AgentStep(2, "list_files", "path=songs/2026-11-07-new/out -> not a directory", false),
            new AgentStep(3, "list_files", "path=. -> 3121 chars", true),
            new AgentStep(4, "find_files", "pattern=* path=songs/2026-11-07-new -> 247 chars", true),
            new AgentStep(5, "read_file", "path=harness/logs/song-forge/2026-11-06-00-15-older-generated.md -> 1634 chars", true),
        };

        var folder = ChatSession.WorkFolderLookedAt(_root, steps);

        Assert.Equal("songs/2026-11-07-new", folder);
        Assert.Contains("judge-takes.py", WorkspacePrecedent.Material(_root, folder));
    }
}

public class BareNameAnywhereTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-bare-" + Guid.NewGuid().ToString("N")[..8]);

    public BareNameAnywhereTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "songs", "x"));
        Directory.CreateDirectory(Path.Combine(_root, "harness", "logs"));
        File.WriteAllText(Path.Combine(_root, "songs", "x", "lyrics.md"), "[Verse 1] 하늘의 인장이");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ABareNameThatIsAFileSomewhereIsNotSentBackToBeRewritten()
    {
        // Measured (2026-11-07): "guide.md, lyrics.md" named bare after writing a log elsewhere were
        // "not on disk"; the model rewrote both, and the Korean lyrics the song was generated from became English.
        var provider = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"harness/logs/review.md","content":"# review"}}""",
            """{"tool":"final","args":{"text":"lyrics.md 기준으로 검토를 review.md 에 남겼습니다."}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 4) { Root = _root };

        var run = await loop.RunAsync("검토 남겨줘");

        Assert.DoesNotContain(run.Steps, s => s.Tool is "unwritten" or "missing");
        Assert.Equal("[Verse 1] 하늘의 인장이", File.ReadAllText(Path.Combine(_root, "songs", "x", "lyrics.md")));
    }

    [Fact]
    public void TheStrongModelThinksLongerBeforeItCountsAsARunaway()
    {
        var config = new AgentConfig();
        config.TrySet("reasoningModel", "big", out _);
        using var everyday = new OpenAiCompatChatProvider(config);
        using var strong = new OpenAiCompatChatProvider(config.ForReasoning());
        Assert.Equal(60_000, everyday.ThinkingLimitChars);
        Assert.Equal(OpenAiCompatChatProvider.ReasoningThinkingLimitChars, strong.ThinkingLimitChars);
    }
}

/// <summary>
/// Measured (2026-11-07): a turn ran Get-ChildItem for *.mp3, found none and
/// ranked six takes by their lengths; the engine said "unbacked_review" at 0.97
/// and was ignored, because a command had succeeded. Listing is not examining.
/// </summary>
public class ListingIsNotExaminingTests
{
    [Theory]
    [InlineData("Get-ChildItem -Recurse -Include *.mp3,*.wav | Select-Object FullName")]
    [InlineData("Get-ChildItem -Recurse -File -Path 'songs/x' -ErrorAction SilentlyContinue | Select-Object FullName, Length | Format-Table -AutoSize")]
    [InlineData("ls -R songs/x")]
    [InlineData("Test-Path out; dir out")]
    public void ListingCommandsListOnly(string command) => Assert.True(ChatSession.ListsOnly(command));

    [Theory]
    [InlineData("node stage-unity/Tools/deevid/dv-fetch.cjs songs/x")]
    [InlineData("bash songs/x/mv/measure-takes.sh")]
    [InlineData(".local/whisper-env/Scripts/python.exe songs/x/mv/judge-takes.py")]
    [InlineData("$files = Get-ChildItem -Filter *.mp3; foreach ($f in $files) { & ffprobe -v error $f.FullName }")]
    public void CommandsThatOpenFilesExamineThem(string command) => Assert.False(ChatSession.ListsOnly(command));

    [Fact]
    public void TheCommandIsReadOutOfTheStepDetail() =>
        Assert.Equal("ls -R songs/x", ChatSession.CommandOf("command=ls -R songs/x -> 879 chars"));
}

public class UnsureBackedTests
{
    private static Decision Of(string choice, double confidence, params (string, double)[] probs) =>
        new(true, choice, confidence, probs.ToDictionary(p => p.Item1, p => p.Item2), "ok", 1);

    [Fact]
    public void AnUnsureBackedYieldsToAnUnbackedRunnerUpWithARealShare()
    {
        // Measured: "backed" at 0.31 for a turn that repeated an earlier unmeasured ranking.
        var judged = Of(SmartRouter.ClaimsBacked, 0.31,
            (SmartRouter.ClaimsBacked, 0.31), (SmartRouter.UnbackedReview, 0.29), (SmartRouter.UnbackedRun, 0.22), (SmartRouter.UnbackedChange, 0.18));

        var effective = ChatSession.UnsureBackedYields(judged, 0.6);

        Assert.Equal(SmartRouter.UnbackedReview, effective.Choice);
    }

    [Fact]
    public void AConfidentBackedOrAWeakRunnerUpStands()
    {
        var sure = Of(SmartRouter.ClaimsBacked, 0.8, (SmartRouter.ClaimsBacked, 0.8), (SmartRouter.UnbackedReview, 0.1));
        var weak = Of(SmartRouter.ClaimsBacked, 0.45, (SmartRouter.ClaimsBacked, 0.45), (SmartRouter.UnbackedReview, 0.2), (SmartRouter.UnbackedRun, 0.2), (SmartRouter.UnbackedChange, 0.15));

        Assert.Equal(SmartRouter.ClaimsBacked, ChatSession.UnsureBackedYields(sure, 0.6).Choice);
        Assert.Equal(SmartRouter.ClaimsBacked, ChatSession.UnsureBackedYields(weak, 0.6).Choice);
    }
}

public class GuessingValuesTests
{
    [Fact]
    public void TheSecondEmptyTryWithAnotherQuotedValueGetsANote()
    {
        var tried = new Dictionary<string, List<string>>();
        const string a = "node dv-fetch.cjs out 4 --match \"Apocalypse Day\"";
        const string b = "node dv-fetch.cjs out 4 --match \"Apocalypse\"";

        Assert.Null(AgentLoop.GuessNote(a, empty: true, tried));
        var note = AgentLoop.GuessNote(b, empty: true, tried);

        Assert.NotNull(note);
        Assert.Contains("\"Apocalypse Day\" · \"Apocalypse\"", note);
        Assert.Contains("grep this work's own files", note);
    }

    [Fact]
    public void TheSameValueAtACorrectedPathIsNotANewGuess()
    {
        var tried = new Dictionary<string, List<string>>();
        Assert.Null(AgentLoop.GuessNote("node dv-fetch.cjs out 4 --match \"Apocalypse Day\"", empty: true, tried));
        Assert.Null(AgentLoop.GuessNote("node stage-unity/Tools/deevid/dv-fetch.cjs out 4 --match \"Apocalypse Day\"", empty: true, tried));
        Assert.NotNull(AgentLoop.GuessNote("node stage-unity/Tools/deevid/dv-fetch.cjs out 4 --match \"Apocalypse\"", empty: true, tried));
    }

    [Fact]
    public void RunningTheSameScriptOverEachTakeIsWorkNotGuessing()
    {
        var tried = new Dictionary<string, List<string>>();
        Assert.Null(AgentLoop.GuessNote("python judge.py \"out/take-1.mp3\"", empty: false, tried));
        Assert.Null(AgentLoop.GuessNote("python judge.py \"out/take-2.mp3\"", empty: false, tried));
        Assert.Null(AgentLoop.GuessNote("python judge.py \"out/take-3.mp3\"", empty: false, tried));
    }

    [Fact]
    public void AnErrorLineInASucceededCommandCountsAsEmpty() =>
        // Push-Location; node …; Pop-Location exits 0 even when the script failed.
        Assert.True(AgentLoop.CameBackEmpty(ToolResult.Success("captured 1 [...]\nERR No matching music assets; nothing downloaded")));
}

public class MissingMediaInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-media-" + Guid.NewGuid().ToString("N")[..8]);

    public MissingMediaInputTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "songs", "x", "out"));
        File.WriteAllText(Path.Combine(_root, "songs", "x", "out", "take-1.mp3"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AMadeUpTakeIsMissing() =>
        // Measured: the model asked to transcribe a take named after its own ranking.
        Assert.Equal(["songs/x/out/C2_v2.mp3"], ChatSession.MissingMediaInputs(
            "python songs/x/mv/transcribe.py --input songs/x/out/C2_v2.mp3 --out songs/x/mv/tmp/C2_v2.json", _root));

    [Theory]
    [InlineData("python transcribe.py --input songs/x/out/take-1.mp3")]
    [InlineData("Push-Location songs/x; python ../../t.py out/take-1.mp3; Pop-Location")]
    [InlineData("ffmpeg -i songs/x/out/take-1.mp3 songs/x/out/take-1.wav")]
    [InlineData("python cut.py songs/x/out/take-1.mp3 --out songs/x/out/clip.mp3")]
    [InlineData("Get-ChildItem songs/x/out/*.mp3")]
    public void RealInputsGlobsAndOutputsPass(string command) =>
        Assert.Empty(ChatSession.MissingMediaInputs(command, _root));
}

public class FencedEnvelopeTests
{
    [Fact]
    public void ABrokenCallInACodeFenceIsNotAnAnswer()
    {
        // Measured: one quote missing, in a ```json fence, accepted as a 69-char answer.
        var raw = "```json\n{\"tool\":\"find_files\",\"args\":{\"pattern\":\"*그날*\",path\":\".\"}}\n```";
        Assert.True(ToolCall.LooksLikeEnvelope(raw));
        Assert.False(AgentLoop.LooksLikeAnAnswer(raw, [new AgentStep(1, "list_files", "path=.", true)]));
    }

    [Fact]
    public void ProseWithAFencedSnippetStaysAnAnswer() =>
        Assert.False(ToolCall.LooksLikeEnvelope("결과입니다.\n```json\n{\"tool\":\"x\"}\n```"));
}
