using System.Collections;
using System.Linq;
using System.Text.Json;
using Agent.Common.Agents;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// M0033 — terminal-side seams the Avalonia host reuses: the catalog JSON, the POSIX
/// environment, the launch planner, the health tracker, the PTY-backed session and the
/// chat mode cycle. None of it needs a PTY or a window.
/// </summary>
[Trait("Category", "Terminal")]
public sealed class TerminalSeamTests
{
    // ── catalog JSON ─────────────────────────────────────────────────────────

    private sealed class Tab(string title, ITerminalSession? session) : IConsoleTabInfo
    {
        public string Title => title;
        public int CliDefinitionId => 1;
        public ITerminalSession? Session => session;
        public bool IsTerminalStarted => session is not null;
    }

    private sealed class Group(string name, string dir, int active, params IConsoleTabInfo[] tabs) : ICliGroupInfo
    {
        public string DirectoryPath => dir;
        public string DisplayName => name;
        public IReadOnlyList<IConsoleTabInfo> TabsView => tabs;
        public int ActiveTabIndex => active;
        public string? DockLayoutJson => null;
    }

    [Fact]
    public void Terminal_list_json_matches_the_wpf_shape()
    {
        var session = new FakeSession("Claude#1");
        var groups = new ICliGroupInfo[]
        {
            new Group("main \"q\"", @"C:\work\a", 1, new Tab("cmd", null), new Tab("Claude", session)),
        };
        var json = TerminalCatalogJson.BuildTerminalListJson(groups, t => t.Title == "Claude" ? "0x0000BEEF" : "");
        var root = JsonDocument.Parse(json).RootElement;
        var g = root.GetProperty("groups")[0];
        Assert.Equal(0, g.GetProperty("group_index").GetInt32());
        Assert.Equal("main \"q\"", g.GetProperty("group_name").GetString());
        Assert.Equal(@"C:\work\a", g.GetProperty("directory").GetString());
        var tabs = g.GetProperty("tabs");
        Assert.Equal(2, tabs.GetArrayLength());
        Assert.False(tabs[0].GetProperty("active").GetBoolean());
        Assert.False(tabs[0].GetProperty("running").GetBoolean());
        Assert.Equal("", tabs[0].GetProperty("session_id").GetString());
        Assert.True(tabs[1].GetProperty("active").GetBoolean());
        Assert.True(tabs[1].GetProperty("running").GetBoolean());
        Assert.Equal("0x0000BEEF", tabs[1].GetProperty("hwnd").GetString());
        Assert.Equal("Claude#1", tabs[1].GetProperty("session_id").GetString());

        Assert.True(TerminalCatalogJson.TryResolveSession(groups, 0, 1, out _, out _, out var resolved, out _));
        Assert.Same(session, resolved);
        Assert.False(TerminalCatalogJson.TryResolveSession(groups, 0, 0, out _, out _, out _, out var err));
        Assert.Contains("not started", err);
        Assert.False(TerminalCatalogJson.TryResolveSession(groups, 3, 0, out _, out _, out _, out err));
        Assert.Contains("group", err);
    }

    // ── POSIX environment ────────────────────────────────────────────────────

    [Fact]
    public void Posix_environment_is_case_sensitive_drops_host_markers_and_sets_defaults()
    {
        var source = new Hashtable
        {
            ["Path"] = "/usr/bin",
            ["PATH"] = "/bin",
            ["NO_COLOR"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["HOME"] = "/Users/me",
        };
        var env = TerminalEnvironment.BuildPosix(source);
        Assert.Equal("/usr/bin", env["Path"]);
        Assert.Equal("/bin", env["PATH"]);
        Assert.False(env.ContainsKey("NO_COLOR"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.Equal("truecolor", env["COLORTERM"]);
        Assert.Equal(TerminalEnvironment.TermProgram, env["TERM_PROGRAM"]);
        Assert.Equal("xterm-256color", env["TERM"]);
        Assert.Equal("en_US.UTF-8", env["LANG"]);

        TerminalEnvironment.PrependPath(env, "/opt/app", ':');
        Assert.Equal("/opt/app:/bin", env["PATH"]);

        var win = TerminalEnvironment.Build(new Hashtable { ["Path"] = @"C:\x" });
        TerminalEnvironment.PrependPath(win, @"C:\app", ';');
        Assert.Equal(@"C:\app;C:\x", win["PATH"]);   // case-insensitive key kept its spelling
    }

    // ── launch planner ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("-NoExit -Command claude", new[] { "-NoExit", "-Command", "claude" })]
    [InlineData("-l -c \"claude; exec zsh -l\"", new[] { "-l", "-c", "claude; exec zsh -l" })]
    [InlineData("say 'hello world' \"a \\\"b\\\"\"", new[] { "say", "hello world", "a \"b\"" })]
    public void Command_lines_split_into_argv(string line, string[] expected)
        => Assert.Equal(expected, CommandLineSplitter.Split(line));

    [Fact]
    public void Planner_hides_the_other_os_definitions_and_builds_a_spec()
    {
        var pwsh = new CliDefinition { Name = "PW7", ExePath = "pwsh.exe", Arguments = "-NoLogo" };
        var zsh = new CliDefinition { Name = "zsh", ExePath = "/bin/zsh", Arguments = "-l" };
        var ssh = new CliDefinition { Name = "box", ExePath = "/bin/zsh", IsRemote = true };

        Assert.True(TerminalLaunchPlanner.IsAvailableOnThisOs(pwsh, isWindows: true));
        Assert.False(TerminalLaunchPlanner.IsAvailableOnThisOs(pwsh, isWindows: false));
        Assert.False(TerminalLaunchPlanner.IsAvailableOnThisOs(zsh, isWindows: true));
        Assert.True(TerminalLaunchPlanner.IsAvailableOnThisOs(zsh, isWindows: false));
        Assert.False(TerminalLaunchPlanner.IsAvailableOnThisOs(ssh, isWindows: false));

        var env = new Hashtable { ["PATH"] = "/bin" };
        var spec = TerminalLaunchPlanner.Plan(zsh, "/Users/me/proj", "/Applications/AZ", out var error, isWindows: false, sourceEnvironment: env)!;
        Assert.Null(error);
        Assert.Equal("/bin/zsh", spec.App);
        Assert.Equal(new[] { "-l" }, spec.Args);
        Assert.Equal("/Users/me/proj", spec.Cwd);
        Assert.Equal("/Applications/AZ:/bin", spec.Env["PATH"]);
        Assert.Equal("/bin/zsh -l", spec.CommandLine);

        Assert.Null(TerminalLaunchPlanner.Plan(ssh, "/x", null, out error, isWindows: false));
        Assert.Contains("remote", error);

        var winSpec = TerminalLaunchPlanner.Plan(pwsh, @"C:\w", @"C:\app", out error, isWindows: true, sourceEnvironment: new Hashtable { ["Path"] = @"C:\bin" })!;
        Assert.Equal("pwsh.exe -NoLogo", winSpec.CommandLine);
        Assert.Equal(@"C:\app;C:\bin", winSpec.Env["Path"]);
    }

    // ── health tracker ───────────────────────────────────────────────────────

    [Fact]
    public async Task Health_goes_stale_then_dead_on_silent_input_and_recovers_on_output()
    {
        var length = 0;
        var gate = new TaskCompletionSource();
        var checks = 0;
        using var tracker = new TerminalHealthTracker(() => length, delay: (_, _) => { checks++; return Task.CompletedTask; });
        var seen = new List<TerminalHealthState>();
        tracker.Changed += s => seen.Add(s);

        for (var i = 0; i < 3; i++) tracker.NoteInputAttempt("k");
        await Task.Delay(50);
        Assert.Equal(TerminalHealthState.Stale, tracker.State);
        for (var i = 0; i < 2; i++) tracker.NoteInputAttempt("k");
        await Task.Delay(50);
        Assert.Equal(TerminalHealthState.Dead, tracker.State);

        length = 10;   // output arrived
        tracker.NoteOutput();
        Assert.Equal(TerminalHealthState.Alive, tracker.State);
        Assert.Equal(new[] { TerminalHealthState.Stale, TerminalHealthState.Dead, TerminalHealthState.Alive }, seen);

        // An echoed input never counts.
        tracker.NoteInputAttempt("k");
        length = 11;
        await Task.Delay(50);
        Assert.Equal(TerminalHealthState.Alive, tracker.State);
        Assert.Equal(6, checks);
    }

    // ── session over a fake PTY ──────────────────────────────────────────────

    private sealed class FakePty : IPtyHost
    {
        public readonly List<string> Written = new();
        public event Action<string>? Output;
        public event Action? Exited;
        public bool IsRunning { get; set; } = true;
        public (int cols, int rows) Size;
        public string Diagnostics => "fake";
        public void Write(ReadOnlySpan<char> text) => Written.Add(text.ToString());
        public void Resize(int cols, int rows) => Size = (cols, rows);
        public void Emit(string s) => Output?.Invoke(s);
        public void Exit() => Exited?.Invoke();
        public void Dispose() { }
    }

    [Fact]
    public async Task Session_writes_reads_and_serves_the_renderer_snapshot()
    {
        var pty = new FakePty();
        var log = new List<string>();
        using var session = new XtermTerminalSession(pty, "cmd#1", log.Add);

        Assert.True(session.IsRunning);
        session.Write("dir");
        Assert.Equal(new[] { "dir" }, pty.Written);

        var frames = new List<string>();
        session.OutputReceived += f => frames.Add(f.Text);
        pty.Emit("C:\\> dir\r\n");
        pty.Emit("file.txt\r\n");
        Assert.Equal(2, frames.Count);
        Assert.Equal("C:\\> dir\r\nfile.txt\r\n".Length, session.OutputLength);
        Assert.Equal("file.txt", session.ReadOutput(session.OutputLength - 10, 8));

        session.SetScreenSnapshot("C:\\> dir\nfile.txt\nC:\\>");
        Assert.Equal("C:\\> dir\nfile.txt\nC:\\>", session.GetConsoleText());

        session.SendControl(TerminalControl.Interrupt);
        Assert.Equal("\u0003", pty.Written[^1]);

        // Long text goes through the chunked writer and ends with a CR.
        var big = new string('a', 450);
        await session.WriteAsync(big.AsMemory());
        // The write loop runs on the thread pool with 50 ms gaps between chunks; a busy CI
        // runner schedules it late, so wait for the four writes rather than a fixed time.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (pty.Written.Count < 6 && DateTime.UtcNow < deadline) await Task.Delay(25);
        var chunks = pty.Written.Skip(2).ToList();
        Assert.Equal(new[] { 200, 200, 50, 1 }, chunks.Select(c => c.Length));
        Assert.Equal("\r", chunks[^1]);

        pty.IsRunning = false;
        Assert.False(session.IsRunning);
        session.Write("ignored");
        Assert.DoesNotContain("ignored", pty.Written);
        Assert.Contains(log, l => l.Contains("host not running"));
    }

    // ── chat mode cycle ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChatMode.Chat, true, ChatMode.Key)]
    [InlineData(ChatMode.Key, true, ChatMode.Ai)]
    [InlineData(ChatMode.Key, false, ChatMode.Chat)]
    [InlineData(ChatMode.Ai, true, ChatMode.Chat)]
    public void Chat_mode_cycle(ChatMode from, bool ai, ChatMode expected)
        => Assert.Equal(expected, ChatModeCycle.Next(from, ai));

    [Fact]
    public void Ai_mode_only_stands_while_available()
    {
        Assert.Equal(ChatMode.Chat, ChatModeCycle.StabilizeForBadge(ChatMode.Ai, false));
        Assert.Equal(ChatMode.Ai, ChatModeCycle.StabilizeForBadge(ChatMode.Ai, true));
        Assert.Equal("AI", ChatModeCycle.Label(ChatMode.Ai));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeSession(string id) : ITerminalSession
    {
        public string SessionId => id;
        public string InternalId => "deadbeef";
        public bool IsRunning => true;
        public void Write(ReadOnlySpan<char> text) { }
        public void WriteAndSubmit(string text) { }
        public void WriteAndEnter(string text) { }
        public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default) => Task.CompletedTask;
        public void SendControl(TerminalControl control) { }
        public event Action<TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => 0;
        public string ReadOutput(int start, int length) => "";
        public string GetConsoleText() => "";
        public void NoteInputAttempt(string source) { }
        public TerminalHealthState HealthState => TerminalHealthState.Alive;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
    }
}
