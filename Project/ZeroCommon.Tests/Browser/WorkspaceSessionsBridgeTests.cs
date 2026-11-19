using System.Text.Json;
using Agent.Common.Agents;
using Agent.Common.Browser;
using Xunit;

namespace ZeroCommon.Tests.Browser;

/// <summary>
/// sessions.list / sessions.focus — what the Agent Stage plugin reads. The feed is process-wide,
/// so every case lives in this one class (xUnit runs a class's tests one at a time) and resets it.
/// </summary>
[Trait("Category", "ZeroBridge")]
public sealed class WorkspaceSessionsBridgeTests : IDisposable
{
    private sealed class NoHost : IZeroBridgeHost
    {
        public Task<object?>? TryDispatchHostOp(string op, JsonElement? args) => null;
        public Task<string?> PickFolderAsync(string title, string? initialFolder) => Task.FromResult<string?>(null);
        public Task<byte[]?> CaptureFramePngAsync() => Task.FromResult<byte[]?>(null);
        public void UseMp3Folder(string? folder) { }
    }

    private readonly ZeroBridgeDispatcher _d = new(new ZeroBrowserCore(), new NoHost(), (_, _) => { });

    public WorkspaceSessionsBridgeTests() => WorkspaceSessionsFeed.Reset();
    public void Dispose() => WorkspaceSessionsFeed.Reset();

    private async Task<JsonElement> Call(string op, string args = "{}")
        => JsonDocument.Parse(await _d.HandleRequestAsync($$"""{"id":1,"op":"{{op}}","args":{{args}}}""")).RootElement.Clone();

    [Fact]
    public async Task A_host_that_never_published_says_unsupported()
    {
        var r = await Call("sessions.list");
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.False(r.GetProperty("result").GetProperty("supported").GetBoolean());
        Assert.Equal(0, r.GetProperty("result").GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public async Task The_published_picture_reaches_the_plugin_in_camel_case()
    {
        WorkspaceSessionsFeed.Publish([
            new WorkspaceView(0, "proj", "/p", Active: true, [
                new WorkspaceSessionView(0, 1, "Codex", "codex", "working", Focused: true, NeedsAttention: false)]),
        ]);

        var ws = (await Call("sessions.list")).GetProperty("result").GetProperty("workspaces")[0];
        Assert.Equal("proj", ws.GetProperty("name").GetString());
        Assert.True(ws.GetProperty("active").GetBoolean());
        var s = ws.GetProperty("sessions")[0];
        Assert.Equal("codex", s.GetProperty("agent").GetString());
        Assert.Equal("working", s.GetProperty("state").GetString());
        Assert.Equal(1, s.GetProperty("tab").GetInt32());
        Assert.False(s.GetProperty("needsAttention").GetBoolean());
        Assert.False(s.TryGetProperty("subagents", out _));   // not a Claude tab: left out, not []
    }

    [Fact]
    public async Task A_claude_tab_carries_its_subagents()
    {
        WorkspaceSessionsFeed.Publish([
            new WorkspaceView(0, "proj", "/p", Active: true, [
                new WorkspaceSessionView(0, 0, "Claude", "claude", "working", Focused: false, NeedsAttention: false, [
                    new SubagentView("a1", null, "Explore", "Survey", 1, true, "working", "Read · x.cs", 3, 1000, null),
                    new SubagentView("c1", "a1", "Plan", "Nested", 2, true, "done", null, 0, 2000, 3000)])]),
        ]);

        var subs = (await Call("sessions.list")).GetProperty("result").GetProperty("workspaces")[0]
            .GetProperty("sessions")[0].GetProperty("subagents");
        Assert.Equal(2, subs.GetArrayLength());
        Assert.Equal("Explore", subs[0].GetProperty("type").GetString());
        Assert.Equal("Read · x.cs", subs[0].GetProperty("tool").GetString());
        Assert.False(subs[0].TryGetProperty("parentId", out _));
        Assert.Equal("a1", subs[1].GetProperty("parentId").GetString());
        Assert.Equal(3000, subs[1].GetProperty("endedAt").GetInt64());
    }

    [Fact]
    public async Task A_claude_tab_in_a_garden_carries_the_harness_and_its_experts()
    {
        var lyricist = new HarnessExpert("lyricist", "작사가", "specialist", "가사 전문가");
        WorkspaceSessionsFeed.Publish([
            new WorkspaceView(0, "music", "/m", Active: true, [
                new WorkspaceSessionView(0, 0, "Claude", "claude", "working", Focused: false, NeedsAttention: false,
                    [new SubagentView("a1", null, "general-purpose", "lyricist review", 1, true, "working", null, 0, 1000, null, lyricist)],
                    new HarnessView("Music Harness", "1.14.0", 14,
                        [new HarnessDutyView("tamer", "정원지기", null, "command", "working", 900, null)]))]),
        ]);

        var s = (await Call("sessions.list")).GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[0];
        var expert = s.GetProperty("subagents")[0].GetProperty("expert");
        Assert.Equal("lyricist", expert.GetProperty("name").GetString());
        Assert.Equal("작사가", expert.GetProperty("persona").GetString());
        var h = s.GetProperty("harness");
        Assert.Equal("Music Harness", h.GetProperty("name").GetString());
        Assert.Equal(14, h.GetProperty("experts").GetInt32());
        var duty = h.GetProperty("onDuty")[0];
        Assert.Equal("command", duty.GetProperty("via").GetString());
        Assert.False(duty.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task Focus_goes_through_the_host_handler_and_needs_group_and_tab()
    {
        (int, int)? asked = null;
        WorkspaceSessionsFeed.SetFocusHandler((g, t) => { asked = (g, t); return true; });

        var ok = await Call("sessions.focus", """{"group":2,"tab":3}""");
        Assert.True(ok.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal((2, 3), asked);

        var bad = await Call("sessions.focus", """{"group":2}""");
        Assert.False(bad.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("Claude", "claude")]
    [InlineData("Codex-2", "codex")]
    [InlineData("AgentOne", "agentone")]
    [InlineData("Netclaw", "netclaw")]
    [InlineData("PW5", "shell")]
    public void The_agent_comes_from_the_tab_title(string title, string agent)
        => Assert.Equal(agent, WorkspaceSessionsFeed.AgentOf(title));

    [Fact]
    public void States_map_to_wire_names()
    {
        Assert.Equal("running", WorkspaceSessionsFeed.StateOf(null));
        Assert.Equal("working", WorkspaceSessionsFeed.StateOf(new TerminalAgentState(0, 0, "x", AgentActivity.Working, false, null)));
        Assert.Equal("done", WorkspaceSessionsFeed.StateOf(new TerminalAgentState(0, 0, "x", AgentActivity.Done, false, null)));
        Assert.Equal("done-seen", WorkspaceSessionsFeed.StateOf(new TerminalAgentState(0, 0, "x", AgentActivity.Done, true, null)));
        Assert.Equal("blocked", WorkspaceSessionsFeed.StateOf(new TerminalAgentState(0, 0, "x", AgentActivity.Blocked, false, null)));
    }
}
