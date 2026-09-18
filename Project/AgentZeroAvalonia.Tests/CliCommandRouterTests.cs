using System.Text.Json;
using Agent.Common.Module;
using Agent.Common.Services;
using AgentZeroAvalonia.Cli;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The router against fake workspaces/sessions: request in, WPF-shaped JSON out.</summary>
public class CliCommandRouterTests : IDisposable
{
    private readonly string _aliasFile = Path.Combine(Path.GetTempPath(), "az-alias-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeSession _session = new("infra/CMD");
    private readonly CliCommandRouter _router;

    public CliCommandRouterTests()
    {
        var groups = new List<ICliGroupInfo>
        {
            new FakeGroup("infra", @"C:\infra", new FakeTab("PW7", null), new FakeTab("CMD", _session)),
            new FakeGroup("docs", @"C:\docs", new FakeTab("zsh", null)),
        };
        _router = new CliCommandRouter(null)
        {
            Groups = () => groups,
            AliasRegistryPath = _aliasFile,
            LayoutStatus = () => ("{\"Tabs\":[0]}", 1, "infra"),
            ExecuteWindowCommand = _ => { },
        };
    }

    public void Dispose()
    {
        try { File.Delete(_aliasFile); } catch { }
    }

    private JsonElement Send(string json) => JsonDocument.Parse(_router.HandleAsync(json, CancellationToken.None).Result).RootElement.Clone();

    [Fact]
    public void Status_counts_groups_and_terminals()
    {
        var r = Send("{\"command\":\"status\"}");
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("avalonia", r.GetProperty("host").GetString());
        Assert.Equal(2, r.GetProperty("groups").GetInt32());
        Assert.Equal(3, r.GetProperty("terminals").GetInt32());
    }

    [Fact]
    public void Terminal_list_reports_running_state_and_session_ids()
    {
        var r = Send("{\"command\":\"terminal-list\"}");
        var tabs = r.GetProperty("groups")[0].GetProperty("tabs");
        Assert.False(tabs[0].GetProperty("running").GetBoolean());
        Assert.True(tabs[1].GetProperty("running").GetBoolean());
        Assert.Equal("infra/CMD", tabs[1].GetProperty("session_id").GetString());
    }

    [Fact]
    public void Send_read_and_key_round_trip_through_the_session()
    {
        var sent = Send("{\"command\":\"terminal-send\",\"group_index\":0,\"tab_index\":1,\"text\":\"dir\"}");
        Assert.True(sent.GetProperty("ok").GetBoolean());
        Assert.Equal(3, sent.GetProperty("sent_length").GetInt32());
        Assert.Equal("dir", _session.Submitted.Single());

        _session.Screen = "C:\\infra>dir\n\x1b[32m file.txt\x1b[0m\nC:\\infra>";
        var read = Send("{\"command\":\"terminal-read\",\"group_index\":0,\"tab_index\":1}");
        Assert.True(read.GetProperty("ok").GetBoolean());
        var shown = read.GetProperty("text").GetString() ?? "";
        Assert.True(!shown.Contains((char)27), "ANSI not stripped: " + shown.Replace(((char)27).ToString(), "<ESC>"));
        Assert.Contains("file.txt", read.GetProperty("text").GetString());

        var key = Send("{\"command\":\"terminal-key\",\"group_index\":0,\"tab_index\":1,\"key\":\"ctrlc\"}");
        Assert.True(key.GetProperty("ok").GetBoolean());
        Assert.Equal("\x03", _session.Written.Last());
    }

    [Fact]
    public void Not_started_and_bad_indexes_are_explained()
    {
        var notStarted = Send("{\"command\":\"terminal-send\",\"group_index\":0,\"tab_index\":0,\"text\":\"x\"}");
        Assert.False(notStarted.GetProperty("ok").GetBoolean());
        Assert.Contains("not started", notStarted.GetProperty("error").GetString());

        var badGroup = Send("{\"command\":\"terminal-read\",\"group_index\":9,\"tab_index\":0}");
        Assert.Contains("Invalid group_index 9", badGroup.GetProperty("error").GetString());

        var missing = Send("{\"command\":\"terminal-read\"}");
        Assert.Contains("group_index", missing.GetProperty("error").GetString());
    }

    [Fact]
    public void Aliases_are_set_listed_resolved_and_removed()
    {
        var set = Send("{\"command\":\"terminal-alias\",\"sub\":\"set\",\"group_index\":0,\"tab_index\":1,\"name\":\"builder\"}");
        Assert.True(set.GetProperty("ok").GetBoolean());
        Assert.Equal("CMD", set.GetProperty("title").GetString());

        var list = Send("{\"command\":\"terminal-alias\",\"sub\":\"list\"}");
        var entry = Assert.Single(list.GetProperty("aliases").EnumerateArray());
        Assert.Equal("builder", entry.GetProperty("alias").GetString());
        Assert.True(entry.GetProperty("live").GetBoolean());
        Assert.Equal(1, entry.GetProperty("tab_index").GetInt32());

        var viaAlias = Send("{\"command\":\"terminal-send\",\"alias\":\"builder\",\"text\":\"echo hi\"}");
        Assert.True(viaAlias.GetProperty("ok").GetBoolean());
        Assert.Equal(1, viaAlias.GetProperty("tab_index").GetInt32());

        var unknown = Send("{\"command\":\"terminal-read\",\"alias\":\"ghost\"}");
        Assert.Contains("not defined", unknown.GetProperty("error").GetString());

        var rm = Send("{\"command\":\"terminal-alias\",\"sub\":\"rm\",\"name\":\"builder\"}");
        Assert.True(rm.GetProperty("ok").GetBoolean());
        Assert.Empty(Send("{\"command\":\"terminal-alias\",\"sub\":\"list\"}").GetProperty("aliases").EnumerateArray());
    }

    [Fact]
    public void Layout_status_and_unknown_verbs()
    {
        var status = Send("{\"command\":\"layout\",\"sub\":\"status\"}");
        Assert.Equal(1, status.GetProperty("panes").GetInt32());
        Assert.Equal("infra", status.GetProperty("workspace").GetString());
        Assert.Equal(JsonValueKind.Object, status.GetProperty("layout").ValueKind);

        var bad = Send("{\"command\":\"layout\",\"sub\":\"teleport\"}");
        Assert.False(bad.GetProperty("ok").GetBoolean());

        var unknown = Send("{\"command\":\"frobnicate\"}");
        Assert.Contains("unknown command", unknown.GetProperty("error").GetString());

        var garbage = Send("not json");
        Assert.Contains("bad request json", garbage.GetProperty("error").GetString());
    }

    [Fact]
    public void Bot_verbs_report_when_no_pane_is_wired()
    {
        Assert.False(Send("{\"command\":\"bot-chat\",\"message\":\"hi\"}").GetProperty("ok").GetBoolean());
        string? got = null;
        _router.BotChat = (from, msg) => got = from + ":" + msg;
        var ok = Send("{\"command\":\"bot-chat\",\"message\":\"DONE(x)\",\"from\":\"CMD\"}");
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("CMD:DONE(x)", got);
        Assert.Equal(7, ok.GetProperty("message_length").GetInt32());
    }

    [Fact]
    public void Web_echoes_req_and_fails_cleanly_without_a_surface()
    {
        var r = Send("{\"command\":\"web\",\"verb\":\"tabs\",\"req\":\"abc123\"}");
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("abc123", r.GetProperty("req").GetString());
    }

    // ── fakes ──

    private sealed class FakeGroup : ICliGroupInfo
    {
        private readonly List<IConsoleTabInfo> _tabs;
        public FakeGroup(string name, string dir, params IConsoleTabInfo[] tabs) { DisplayName = name; DirectoryPath = dir; _tabs = tabs.ToList(); }
        public string DirectoryPath { get; }
        public string DisplayName { get; }
        public IReadOnlyList<IConsoleTabInfo> TabsView => _tabs;
        public int ActiveTabIndex => 0;
        public string? DockLayoutJson => null;
    }

    private sealed class FakeTab : IConsoleTabInfo
    {
        public FakeTab(string title, ITerminalSession? session) { Title = title; Session = session; }
        public string Title { get; }
        public int CliDefinitionId => 1;
        public ITerminalSession? Session { get; }
        public bool IsTerminalStarted => Session is not null;
    }

    private sealed class FakeSession : ITerminalSession
    {
        public FakeSession(string id) { SessionId = id; }
        public string SessionId { get; }
        public string InternalId => "fake0001";
        public bool IsRunning => true;
        public List<string> Written { get; } = new();
        public List<string> Submitted { get; } = new();
        public string Screen { get; set; } = "";
        public void Write(ReadOnlySpan<char> text) => Written.Add(text.ToString());
        public void WriteAndSubmit(string text) => Submitted.Add(text);
        public void WriteAndEnter(string text) => Submitted.Add(text);
        public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default) { Written.Add(text.ToString()); return Task.CompletedTask; }
        public void SendControl(TerminalControl control) => Written.Add(TerminalControlSequences.ToSequence(control));
        public event Action<TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => Screen.Length;
        public string ReadOutput(int start, int length) => Screen.Substring(start, length);
        public string GetConsoleText() => Screen;
        public TerminalHealthState HealthState => TerminalHealthState.Alive;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
        public void NoteInputAttempt(string source) { }
        public void Dispose() { }
    }
}
