using System;
using System.IO;
using System.Linq;
using Agent.Common.Agents;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>The resume picker's data: the sessions Claude / Codex recorded for a folder, and the relaunch arguments.</summary>
[Trait("Category", "AgentSession")]
public sealed class AgentSessionCatalogTests : IDisposable
{
    private readonly string _home;

    public AgentSessionCatalogTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "aztest-sessions-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    // ── Claude ───────────────────────────────────────────────────────────────

    private string ClaudeDir(string cwd)
    {
        var dir = Path.Combine(_home, ".claude", "projects", ClaudeSessionLocator.Slug(cwd));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ClaudeUser(string content, bool meta = false) =>
        "{\"type\":\"user\"" + (meta ? ",\"isMeta\":true" : "") + ",\"message\":{\"role\":\"user\",\"content\":\"" + Esc(content) + "\"}}";

    [Theory]
    [InlineData(@"C:\code\talk-bridge\.claude\worktrees\feat", "C--code-talk-bridge--claude-worktrees-feat")]
    [InlineData(@"C:\my_proj\한글", "C--my-proj---")]
    public void Slug_ReplacesEveryNonAlphanumeric(string cwd, string expected)
        => Assert.Equal(expected, ClaudeSessionLocator.Slug(cwd));

    [Fact]
    public void Claude_ListsNewestFirst_WithFirstPromptAndLatestTitle()
    {
        var cwd = @"C:\code\proj";
        var dir = ClaudeDir(cwd);
        var older = Path.Combine(dir, "aaaaaaaa-0000-0000-0000-000000000001.jsonl");
        File.WriteAllLines(older, new[]
        {
            "{\"type\":\"mode\",\"mode\":\"normal\"}",
            ClaudeUser("<local-command-caveat>Caveat: generated</local-command-caveat>", meta: true),
            ClaudeUser("fix the build\nplease"),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Old title\"}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"Build fix\"}",
        });
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        var newer = Path.Combine(dir, "bbbbbbbb-0000-0000-0000-000000000002.jsonl");
        File.WriteAllLines(newer, new[]
        {
            ClaudeUser("<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>42</command-args>"),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Model title\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"My name\"}",
        });

        var list = AgentSessionCatalog.List(AgentCliTools.Claude, cwd, env: null, homeDir: _home);

        Assert.Equal(2, list.Count);
        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000002", list[0].Id);
        Assert.Equal("My name", list[0].Title);            // a /rename wins over the model's title
        Assert.Equal("/review 42", list[0].Preview);
        Assert.Equal("Build fix", list[1].Title);           // the latest ai-title, not the first
        Assert.Equal("fix the build please", list[1].Preview);
    }

    [Fact]
    public void Claude_SkipsSessionsWithNoPrompt_AndOtherFolders()
    {
        var cwd = @"C:\code\empty";
        var dir = ClaudeDir(cwd);
        File.WriteAllText(Path.Combine(dir, "cccccccc-0000-0000-0000-000000000003.jsonl"),
            "{\"type\":\"file-history-snapshot\"}\n");
        ClaudeDir(@"C:\code\other");
        File.WriteAllText(Path.Combine(_home, ".claude", "projects", ClaudeSessionLocator.Slug(@"C:\code\other"), "x.jsonl"),
            ClaudeUser("hello") + "\n");

        Assert.Empty(AgentSessionCatalog.List(AgentCliTools.Claude, cwd, env: null, homeDir: _home));
    }

    [Fact]
    public void Claude_ReadsTheProfileTheTabRuns()
    {
        var cwd = @"C:\code\profiled";
        var profile = Path.Combine(_home, "profiles", "work");
        var dir = Path.Combine(profile, "projects", ClaudeSessionLocator.Slug(cwd));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dddddddd-0000-0000-0000-000000000004.jsonl"), ClaudeUser("work item") + "\n");
        File.WriteAllText(Path.Combine(ClaudeDir(cwd), "eeeeeeee-0000-0000-0000-000000000005.jsonl"), ClaudeUser("default") + "\n");

        var env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = profile };
        var list = AgentSessionCatalog.List(AgentCliTools.Claude, cwd, env, homeDir: _home);

        Assert.Equal("dddddddd-0000-0000-0000-000000000004", Assert.Single(list).Id);
    }

    // ── profile wrappers (claude-sam) ────────────────────────────────────────
    // Measured: a "Claude-Sam" row (powershell -NoExit -Command claude-sam, a function that sets
    // CLAUDE_CONFIG_DIR=~/.claude-sam and runs `claude @args`) never got the resume question.

    private static CliDefinition Row(string arguments) =>
        new() { Name = "Claude-Sam", ExePath = "powershell.exe", Arguments = arguments };

    private string SamSession(string cwd, string id, string prompt)
    {
        var dir = Path.Combine(_home, ".claude-sam", "projects", ClaudeSessionLocator.Slug(cwd));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".jsonl"), ClaudeUser(prompt) + "\n");
        return dir;
    }

    [Fact]
    public void ProfileWrapper_IsOfferedAndListsItsOwnProfileOnly()
    {
        var cwd = @"C:\code\psmon\AgentZeroLite";
        SamSession(cwd, "aaaaaaaa-0000-0000-0000-00000000000a", "sam's work");
        File.WriteAllText(Path.Combine(ClaudeDir(cwd), "bbbbbbbb-0000-0000-0000-00000000000b.jsonl"), ClaudeUser("default account") + "\n");

        var target = AgentResumeTarget.Of(Row("-NoExit -Command claude-sam"), _home);

        Assert.NotNull(target);
        Assert.Same(AgentCliTools.Claude, target!.Tool);
        Assert.Equal("claude-sam", target.Command);
        Assert.Equal(Path.Combine(_home, ".claude-sam"), target.ConfigDir);
        Assert.Equal("Claude (claude-sam)", target.DisplayName);
        // The tab's environment has no CLAUDE_CONFIG_DIR (the function sets it), and must not decide.
        var list = target.ListSessions(cwd, env: new Dictionary<string, string>(), homeDir: _home);
        Assert.Equal("aaaaaaaa-0000-0000-0000-00000000000a", Assert.Single(list).Id);
    }

    [Fact]
    public void ProfileWrapper_ResumeWordsFollowTheWrapper()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude-sam"));
        var row = Row("-NoExit -Command claude-sam");
        var target = AgentResumeTarget.Of(row, _home)!;

        var resumed = target.WithResume(row, "aaaaaaaa-0000-0000-0000-00000000000a");

        Assert.Equal("-NoExit -Command claude-sam --resume aaaaaaaa-0000-0000-0000-00000000000a", resumed!.Arguments);
        Assert.Equal("powershell.exe", resumed.ExePath);
    }

    [Fact]
    public void ProfileWrapper_FromTheProfilesFolder()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude-profiles", "work"));
        var target = AgentResumeTarget.Of(Row("-NoExit -Command claude-work"), _home);
        Assert.Equal(Path.Combine(_home, ".claude-profiles", "work"), target!.ConfigDir);
    }

    [Fact]
    public void ProfileWrapper_WithoutItsFolder_IsNotOfferedTheDefaultProfile()
    {
        // ~/.claude has sessions, ~/.claude-sam does not exist: those ids would not resume under sam.
        File.WriteAllText(Path.Combine(ClaudeDir(@"C:\x"), "cccccccc-0000-0000-0000-00000000000c.jsonl"), ClaudeUser("default") + "\n");
        Assert.Null(AgentResumeTarget.Of(Row("-NoExit -Command claude-sam"), _home));
    }

    [Fact]
    public void BuiltInRow_KeepsTheTabEnvironmentRule()
    {
        var target = AgentResumeTarget.Of(Row("-NoExit -Command claude"), _home);
        Assert.Equal("claude", target!.Command);
        Assert.Null(target.ConfigDir);
        Assert.Equal("Claude", target.DisplayName);
    }

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command claude-sam", "claude-sam", "sam")]
    [InlineData(@"C:\bin\codex_work.cmd", null, "codex_work", "work")]
    [InlineData("powershell.exe", "-NoExit -Command \"claude-sam --model opus\"", "claude-sam", "sam")]
    public void ProfileCommand_IsACommandWordShapedToolDashName(string exe, string? args, string command, string profile)
    {
        var found = AgentProfileCommand.Find(exe, args);
        Assert.Equal(command, found!.Command);
        Assert.Equal(profile, found.Profile);
    }

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command claude")]               // the tool itself
    [InlineData("powershell.exe", "-NoExit -Command claudette")]            // not tool-dash-name
    [InlineData("powershell.exe", "-NoExit -Command claude-")]              // no name
    [InlineData(@"C:\claude-notes\run.cmd", null)]                          // a folder, not a command
    public void ProfileCommand_IgnoresWhatIsNotAWrapper(string exe, string? args) =>
        Assert.Null(AgentProfileCommand.Find(exe, args));

    [Fact]
    public void Claude_HugeFirstLineIsSkippedNotMisread()
    {
        var cwd = @"C:\code\huge";
        var path = Path.Combine(ClaudeDir(cwd), "ffffffff-0000-0000-0000-000000000006.jsonl");
        File.WriteAllLines(path, new[]
        {
            ClaudeUser(new string('x', 300_000)),   // longer than the head budget
            ClaudeUser("second prompt"),
        });
        // The cut line is dropped; nothing past the budget is read either — no preview, so not listed.
        Assert.Empty(AgentSessionCatalog.List(AgentCliTools.Claude, cwd, env: null, homeDir: _home));
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    private string CodexRollout(string id, string cwd, DateTime written, params string[] more)
    {
        var dir = Path.Combine(_home, ".codex", "sessions", "2026", "10", "15");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-2026-10-15T16-40-16-{id}.jsonl");
        var meta = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"" + Esc(cwd)
                   + "\",\"base_instructions\":{\"text\":\"" + new string('i', 2000) + "\"}}}";
        File.WriteAllLines(path, new[] { meta }.Concat(more));
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static string CodexUser(string text) =>
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + Esc(text) + "\"}]}}";

    [Fact]
    public void Codex_FiltersByCwd_NewestFirst_TitlesFromIndex()
    {
        const string a = "01a13e81-2f61-7713-9df2-d58e3c0d42a8";
        const string b = "01a13e5e-f82b-7b31-9e8f-2ac5116c2d39";
        const string other = "01a13e49-8c8d-71c3-b197-9685d54cfc24";
        var cwd = @"C:\code\zero-test";
        CodexRollout(a, cwd, DateTime.UtcNow.AddMinutes(-5),
            CodexUser("<environment_context>\n<cwd>x</cwd></environment_context>"),
            CodexUser("리액티브 스트림에대해 설명"));
        CodexRollout(b, OperatingSystem.IsWindows() ? cwd.ToUpperInvariant() + "\\" : cwd, DateTime.UtcNow.AddMinutes(-1),
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"java streams\"}}");
        CodexRollout(other, @"C:\code\elsewhere", DateTime.UtcNow, CodexUser("not this one"));
        File.WriteAllLines(Path.Combine(_home, ".codex", "session_index.jsonl"), new[]
        {
            "{\"id\":\"" + a + "\",\"thread_name\":\"first name\"}",
            "{\"id\":\"" + a + "\",\"thread_name\":\"리액티브 스트림 설명하기\"}",
        });

        var list = AgentSessionCatalog.List(AgentCliTools.Codex, cwd, env: null, homeDir: _home);

        Assert.Equal(new[] { b, a }, list.Select(s => s.Id));
        Assert.Equal("java streams", list[0].Preview);
        Assert.Null(list[0].Title);
        Assert.Equal("리액티브 스트림 설명하기", list[1].Title);      // the latest rename
        Assert.Equal("리액티브 스트림에대해 설명", list[1].Preview);   // injected context skipped
    }

    [Fact]
    public void Codex_LeavesOutThreadsCodexStartedForItself()
    {
        const string user = "01a0c9e8-6503-7323-b25a-ce26c7932ef3";
        const string guardian = "01a0c9f1-77f4-71f0-83e4-f734e9a7b109";
        var cwd = @"C:\code\guarded";
        var dir = Path.Combine(_home, ".codex", "sessions", "2026", "09", "23");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"rollout-2026-09-23T01-17-25-{user}.jsonl"),
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + user + "\",\"cwd\":\"" + Esc(cwd) + "\",\"source\":\"cli\",\"thread_source\":\"user\"}}\n"
            + CodexUser("make a song") + "\n");
        File.WriteAllText(Path.Combine(dir, $"rollout-2026-09-23T01-27-20-{guardian}.jsonl"),
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + guardian + "\",\"parent_thread_id\":\"" + user + "\",\"cwd\":\"" + Esc(cwd)
            + "\",\"source\":{\"subagent\":{\"other\":\"guardian\"}},\"thread_source\":\"guardian_review\"}}\n"
            + CodexUser("The following is the Codex agent history whose request action you are assessing.") + "\n");

        Assert.Equal(user, Assert.Single(AgentSessionCatalog.List(AgentCliTools.Codex, cwd, env: null, homeDir: _home)).Id);
    }

    [Fact]
    public void Claude_PrefersAPromptOverABareCommand()
    {
        var cwd = @"C:\code\cleared";
        File.WriteAllLines(Path.Combine(ClaudeDir(cwd), "abababab-0000-0000-0000-000000000007.jsonl"), new[]
        {
            ClaudeUser("<command-name>/clear</command-name>\n<command-message>clear</command-message>\n<command-args></command-args>"),
            ClaudeUser("Base directory for this skill: …", meta: true),
            ClaudeUser("now the real question"),
        });
        Assert.Equal("now the real question", Assert.Single(AgentSessionCatalog.List(AgentCliTools.Claude, cwd, env: null, homeDir: _home)).Preview);
    }

    [Fact]
    public void Codex_FallsBackToHistoryForThePreview()
    {
        const string id = "01a13e81-0000-7713-9df2-d58e3c0d42a8";
        var cwd = @"C:\code\hist";
        CodexRollout(id, cwd, DateTime.UtcNow);
        File.WriteAllLines(Path.Combine(_home, ".codex", "history.jsonl"), new[]
        {
            "{\"session_id\":\"" + id + "\",\"ts\":1,\"text\":\"first words\"}",
            "{\"session_id\":\"" + id + "\",\"ts\":2,\"text\":\"later words\"}",
        });

        var s = Assert.Single(AgentSessionCatalog.List(AgentCliTools.Codex, cwd, env: null, homeDir: _home));
        Assert.Equal("first words", s.Preview);
    }

    [Fact]
    public void OnlyClaudeAndCodexAreSupported()
    {
        Assert.True(AgentSessionCatalog.Supports(AgentCliTools.Claude));
        Assert.True(AgentSessionCatalog.Supports(AgentCliTools.Codex));
        Assert.False(AgentSessionCatalog.Supports(AgentCliTools.AgentOne));
        Assert.Empty(AgentSessionCatalog.List(AgentCliTools.Netclaw, @"C:\x", env: null, homeDir: _home));
    }

    // ── relaunch arguments ───────────────────────────────────────────────────

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command claude", "-NoExit -Command claude --resume S1")]
    [InlineData("powershell.exe", "-NoExit -Command claude --model opus", "-NoExit -Command claude --resume S1 --model opus")]
    [InlineData("/bin/zsh", "-l -c \"claude; exec zsh -l\"", "-l -c \"claude --resume S1; exec zsh -l\"")]
    [InlineData("claude", "", "--resume S1")]
    [InlineData(@"C:\tools\claude.cmd", "--verbose", "--resume S1 --verbose")]
    public void InsertResume_Claude(string exe, string args, string expected)
        => Assert.Equal(expected, AgentResumeLaunch.InsertResume(exe, args, AgentCliTools.Claude, "S1"));

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command codex", "-NoExit -Command codex resume S1")]
    [InlineData("powershell.exe", "-NoExit -Command codex --yolo", "-NoExit -Command codex resume S1 --yolo")]
    [InlineData("/bin/zsh", "-l -c \"codex; exec zsh -l\"", "-l -c \"codex resume S1; exec zsh -l\"")]
    public void InsertResume_Codex_PutsTheSubcommandRightAfterTheCommand(string exe, string args, string expected)
        => Assert.Equal(expected, AgentResumeLaunch.InsertResume(exe, args, AgentCliTools.Codex, "S1"));

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command claude", "a b")]          // unsafe id
    [InlineData("powershell.exe", "-NoExit -Command claude", "x;rm -rf /")]
    [InlineData("powershell.exe", @"-File C:\claude-notes\run.ps1", "S1")]    // a path, not the command
    public void InsertResume_RefusesWhatItCannotDoSafely(string exe, string args, string id)
        => Assert.Null(AgentResumeLaunch.InsertResume(exe, args, AgentCliTools.Claude, id));

    [Fact]
    public void WithResume_CopiesTheDefinition_AndPlansTheResumedCommand()
    {
        var def = new CliDefinition { Id = 7, Name = "Codex", ExePath = "powershell.exe", Arguments = "-NoExit -Command codex", ReducedMotion = true };
        var resumed = AgentResumeLaunch.WithResume(def, AgentCliTools.Codex, "01a13e81-2f61-7713-9df2-d58e3c0d42a8")!;

        Assert.Equal("-NoExit -Command codex", def.Arguments);   // the stored row is not touched
        var spec = TerminalLaunchPlanner.Plan(resumed, @"C:\w", null, out _, isWindows: true)!;
        Assert.Equal("powershell.exe -NoExit -Command codex resume 01a13e81-2f61-7713-9df2-d58e3c0d42a8 -c tui.animations=false", spec.CommandLine);
    }

    [Fact]
    public void FindCommandEnd_UsesTheTokenRule()
    {
        Assert.Equal("-NoExit -Command claude".Length, CliToolMention.FindCommandEnd("-NoExit -Command claude", "claude"));
        Assert.Equal(-1, CliToolMention.FindCommandEnd(@"-File C:\claude-x\a.ps1", "claude"));
        Assert.Equal(-1, CliToolMention.FindCommandEnd(null, "claude"));
    }

    [Fact]
    public void ResumeSettings_RoundTripPerTool()
    {
        var saved = AgentResumeSettingsStore.FilePath;
        AgentResumeSettingsStore.FilePath = Path.Combine(_home, "agent-resume.json");
        try
        {
            Assert.True(AgentResumeSettingsStore.Load().Asks(AgentCliTools.Claude));   // no file → ask
            var s = new AgentResumeSettings();
            s.SetAsks(AgentCliTools.Codex, false);
            AgentResumeSettingsStore.Save(s);
            var back = AgentResumeSettingsStore.Load();
            Assert.True(back.Asks(AgentCliTools.Claude));
            Assert.False(back.Asks(AgentCliTools.Codex));
            Assert.False(back.Asks(AgentCliTools.AgentOne));                            // never asked for unsupported tools
        }
        finally { AgentResumeSettingsStore.FilePath = saved; }
    }
}
