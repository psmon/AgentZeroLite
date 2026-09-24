using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// The turn has to leave the work on disk. Every guard here comes from one
/// measured session (2026-09-24): "테트리스 웹게임 만들어" routed to `answer_directly`
/// at 0.77, so the edit family was ruled out, every `write_file` was turned
/// away, and the model then told the user it had created three files. The
/// folder was empty and the code existed nowhere but in that sentence.
/// </summary>
public class FileDeliverableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-deliver-" + Guid.NewGuid().ToString("N")[..8]);

    public FileDeliverableTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentLoop Loop(ScriptedChatProvider provider)
    {
        var files = new LocalFileToolbelt(_root);
        var belt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, files),
            (ToolCatalog.EditFamily, files),
            (ToolCatalog.WebFamily, files));
        return new AgentLoop(provider, belt, maxSteps: 10) { Root = _root };
    }

    /// <summary>A write_file envelope. Content stays free of quotes and newlines so the JSON needs no escaping.</summary>
    private static string Write(string path, string content) =>
        $"{{\"tool\":\"write_file\",\"args\":{{\"path\":\"{path}\",\"content\":\"{content}\"}}}}";

    private static string Answer(string text) => ToolCall.Final(text).ToJson();

    /// <summary>A fenced block big enough to be a deliverable rather than an illustration.</summary>
    private static string BigFence() =>
        "```js\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"  const row{i} = [0,0,0,0,0,0,0,0];")) + "\n```";

    // --- the route must steer, but it must not be a wall ----------------------

    [Fact]
    public async Task AskingTwiceForARuledOutToolOverrulesTheRouteAndTheFileIsWritten()
    {
        var provider = new ScriptedChatProvider(
            Write("index.html", "<h1>tetris</h1>"),      // refused: the route ruled the edit family out
            Write("index.html", "<h1>tetris</h1>"),      // the model insists -> the route gives way
            Answer("wrote index.html"));

        var run = await Loop(provider).RunAsync("테트리스 웹게임 만들어", CancellationToken.None,
            SmartRouter.FamiliesFor(Route.Answer));

        Assert.True(run.Succeeded);
        Assert.Equal("<h1>tetris</h1>", await File.ReadAllTextAsync(Path.Combine(_root, "index.html")));
        Assert.Contains(run.Steps, s => s.Tool == "route-overruled" && s.Ok);
        Assert.Contains(run.Steps, s => s.Tool == "write_file" && s.Ok);
    }

    [Fact]
    public async Task TheFirstRefusalStillStandsSoTheRouteIsNotPointless()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"web_search","args":{"query":"tetris"}}""",
            Answer("answered without the web"));

        var run = await Loop(provider).RunAsync("테트리스가 뭔지 설명해줘", CancellationToken.None,
            SmartRouter.FamiliesFor(Route.Answer));

        Assert.True(run.Succeeded);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "route-overruled");
        Assert.Contains(run.Steps, s => s.Tool == "web_search" && !s.Ok);
    }

    [Fact]
    public async Task TheRefusalTellsTheModelHowToGetTheToolItActuallyNeeds()
    {
        var provider = new ScriptedChatProvider(
            Write("a.txt", "hello"),
            Answer("gave up"));

        var run = await Loop(provider).RunAsync("파일 만들어줘", CancellationToken.None, SmartRouter.FamiliesFor(Route.Answer));

        var refusal = run.Steps.First(s => s.Tool == "write_file" && !s.Ok);
        Assert.Contains("ask for it once more", refusal.Detail);
    }

    // --- code in the answer with nothing on disk is not an answer -------------

    [Fact]
    public async Task ASubstantialCodeBlockWithNoWriteGetsOneNudgeAndThenTheFileAppears()
    {
        var provider = new ScriptedChatProvider(
            Answer("세 개의 파일을 생성하고 내용을 채웠습니다.\n\n" + BigFence()),   // claims files, wrote none
            Write("script.js", "const board = [];"),                          // after the nudge
            Answer("script.js 를 썼습니다"));

        var run = await Loop(provider).RunAsync("테트리스 만들어", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Contains(run.Steps, s => s.Tool == "unwritten" && !s.Ok);
        Assert.True(File.Exists(Path.Combine(_root, "script.js")));
        Assert.Equal("script.js 를 썼습니다", run.Text);
    }

    [Fact]
    public async Task TheNudgeIsSpentOnceSoAModelThatMeantToShowCodeStillGetsToAnswer()
    {
        var provider = new ScriptedChatProvider(
            Answer("이런 식입니다.\n\n" + BigFence()),
            Answer("코드를 보여드린 것이고 파일로 저장하지는 않았습니다.\n\n" + BigFence()));

        var run = await Loop(provider).RunAsync("테트리스 코드 보여줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Single(run.Steps.Where(s => s.Tool == "unwritten"));
        Assert.StartsWith("코드를 보여드린 것", run.Text);
    }

    [Fact]
    public async Task OnceAFileIsWrittenTheSameAnswerIsAccepted()
    {
        var provider = new ScriptedChatProvider(
            Write("script.js", "const board = [];"),
            Answer("script.js 를 만들었습니다.\n\n" + BigFence()));

        var run = await Loop(provider).RunAsync("테트리스 만들어", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "unwritten");
    }

    // --- the detectors --------------------------------------------------------

    [Fact]
    public void FencedCharsCountsOnlyClosedFences()
    {
        Assert.Equal(0, AgentLoop.FencedChars("no code here at all"));
        Assert.Equal(0, AgentLoop.FencedChars("```js\nnever closed"));
        Assert.Equal(6, AgentLoop.FencedChars("```\nhello\n```"));
        Assert.Equal(12, AgentLoop.FencedChars("a ```js\nhello\n``` b ```\nworld\n``` c"));
    }

    [Fact]
    public void AShortSnippetIsAnIllustrationNotADeliverable()
    {
        var steps = new List<AgentStep>();
        Assert.False(AgentLoop.ShowsUnwrittenCode("이렇게 쓰면 됩니다:\n```py\nprint(1)\n```", steps));
        Assert.True(AgentLoop.ShowsUnwrittenCode(BigFence(), steps));

        steps.Add(new AgentStep(1, "write_file", "path=a.py -> 12 chars", true));
        Assert.False(AgentLoop.ShowsUnwrittenCode(BigFence(), steps));
    }

    // --- files named as done that were never created ------------------------

    [Fact]
    public async Task ListingFilesAsDoneWhenOnlyOneWasWrittenIsSentBack()
    {
        // Measured (2026-09-24, the second tetris run): index.html really was
        // written, then the answer listed four files as complete. The page
        // loaded with three ERR_FILE_NOT_FOUND. One successful write had
        // disarmed the "nothing written" check.
        var claim = Answer(string.Join("\n",
            "테트리스 웹 게임이 완성되었습니다.",
            "1. index.html: 기본 구조",
            "2. css/style.css: 스타일",
            "3. js/tetris.js: 로직",
            "4. js/main.js: 루프"));

        var provider = new ScriptedChatProvider(
            Write("index.html", "<h1>t</h1>"),
            claim,                                        // three of the four do not exist
            Write("css/style.css", "body{}"),
            Write("js/tetris.js", "//t"),
            Write("js/main.js", "//m"),
            Answer("네 개의 파일을 모두 썼습니다"));

        var run = await Loop(provider).RunAsync("테트리스 웹 게임을 만들어줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        var caught = run.Steps.Single(s => s.Tool == "unwritten");
        Assert.Contains("css/style.css", caught.Detail);
        Assert.Contains("js/tetris.js", caught.Detail);
        Assert.Contains("js/main.js", caught.Detail);
        Assert.DoesNotContain("index.html", caught.Detail);        // that one was really written

        foreach (var path in new[] { "index.html", "css/style.css", "js/tetris.js", "js/main.js" })
            Assert.True(File.Exists(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))), path);
    }

    [Fact]
    public async Task AFileThatAlreadyExistsMayBeNamedFreely()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# earlier");

        var provider = new ScriptedChatProvider(Answer("README.md 를 확인했습니다. 내용은 그대로입니다."));
        var run = await Loop(provider).RunAsync("README 확인해줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "unwritten");
    }

    [Fact]
    public void FilePathsInPicksOutSourceAndAssetNames()
    {
        var found = AgentLoop.FilePathsIn("1. `index.html`: 구조, 2. css/style.css 스타일, 3. js/main.js 루프. 끝.");
        Assert.Equal(["index.html", "css/style.css", "js/main.js"], found);

        Assert.Empty(AgentLoop.FilePathsIn("버전 1.2.3 으로 올렸습니다"));
        Assert.Empty(AgentLoop.FilePathsIn("점수는 0 이고 레벨은 1 입니다"));
    }

    [Fact]
    public async Task APlanMayNameFilesThatDoNotExistYet()
    {
        // A design turn writes nothing and names what it proposes to build.
        // That is a plan, not a false report, and it must not be sent back.
        var provider = new ScriptedChatProvider(
            Answer("이렇게 구성하겠습니다: index.html, css/style.css, js/main.js 세 파일."));

        var run = await Loop(provider).RunAsync("어떻게 구성할지 알려줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "unwritten");
    }

    [Fact]
    public async Task TheNudgeRepeatsWhileTheModelKeepsWritingSoASixFileBuildFinishes()
    {
        // Measured (2026-09-24, third run): with a flat budget of one nudge the
        // model wrote index.html, was sent back, wrote css/style.css, then
        // claimed all six files were done — and the spent budget let it
        // through with four missing. Each nudge buys about one file.
        var claim = Answer(string.Join("\n",
            "모든 파일 구현이 완료되었습니다.",
            "index.html, css/style.css, js/core.mjs, js/app.mjs, server.mjs 를 모두 작성했습니다."));

        var provider = new ScriptedChatProvider(
            Write("index.html", "<h1>t</h1>"), claim,
            Write("css/style.css", "body{}"), claim,
            Write("js/core.mjs", "//core"), claim,
            Write("js/app.mjs", "//app"), claim,
            Write("server.mjs", "//server"), claim);

        var run = await Loop(provider).RunAsync("테트리스 웹게임 만들어죠", CancellationToken.None);

        Assert.True(run.Succeeded);
        foreach (var path in new[] { "index.html", "css/style.css", "js/core.mjs", "js/app.mjs", "server.mjs" })
            Assert.True(File.Exists(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))), path + " missing");

        // Four nudges, one per file that was still missing; the fifth claim is true.
        Assert.Equal(4, run.Steps.Count(s => s.Tool == "unwritten"));
    }

    [Fact]
    public async Task AModelThatWritesNothingAfterANudgeIsNotNudgedForever()
    {
        var claim = Answer("index.html, css/style.css, js/main.js 를 모두 작성했습니다.");

        var provider = new ScriptedChatProvider(
            Write("index.html", "<h1>t</h1>"),
            claim,          // nudged: two files still missing
            claim,          // wrote nothing in between — the nudge stops here
            claim);

        var run = await Loop(provider).RunAsync("만들어줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Single(run.Steps.Where(s => s.Tool == "unwritten"));
    }

    [Fact]
    public async Task ClaimingFilesFromAnEarlierTurnIsCheckedToo()
    {
        // Measured (2026-09-24, tetris3 turn 2): the turn ran Get-ChildItem,
        // got a listing with one file in it, and answered that four files had
        // been written "in the earlier turn" and confirmed by that very
        // listing. It wrote nothing, so a write-only check skipped it.
        var claim = Answer(string.Join("\n",
            "이전 턴에서 모든 구성 요소가 성공적으로 작성 및 저장되었습니다.",
            "1. index.html  2. css/style.css  3. js/tetris.js  4. README.md",
            "파일 시스템 목록 명령어로 생성되었음을 확인했습니다."));

        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<h1>t</h1>");

        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            claim,
            Answer("index.html 만 있습니다. 나머지는 작성되지 않았습니다."));

        var run = await Loop(provider).RunAsync("파일 확인해줘", CancellationToken.None);

        Assert.True(run.Succeeded);
        var caught = run.Steps.Single(s => s.Tool == "unwritten");
        Assert.Contains("css/style.css", caught.Detail);
        Assert.Contains("README.md", caught.Detail);
        Assert.DoesNotContain("index.html", caught.Detail);     // that one is really there
        Assert.StartsWith("index.html 만 있습니다", run.Text);
    }

    [Fact]
    public async Task AFinalFamilySetIsARuleTheModelCannotTalkItsWayOutOf()
    {
        // The wrap-up and the post-escalation pass are deliberate no-tool
        // calls, not route guesses. Measured: the override leaked into a
        // wrap-up, the model went hunting for files instead of summarising,
        // and the turn ended on a repeat guard with no summary at all.
        var provider = new ScriptedChatProvider(
            """{"tool":"find_files","args":{"pattern":"*","path":"."}}""",
            """{"tool":"find_files","args":{"pattern":"*","path":"."}}""",
            Answer("요약합니다: 아무것도 찾지 못했습니다."));

        var run = await Loop(provider).RunAsync("[wrap-up] 요약해줘", CancellationToken.None,
            SmartRouter.FamiliesFor(Route.Answer), familiesAreFinal: true);

        Assert.True(run.Succeeded);
        Assert.DoesNotContain(run.Steps, s => s.Tool == "route-overruled");
        Assert.Equal(2, run.Steps.Count(s => s.Tool == "find_files" && !s.Ok));
        Assert.StartsWith("요약합니다", run.Text);
    }
}
