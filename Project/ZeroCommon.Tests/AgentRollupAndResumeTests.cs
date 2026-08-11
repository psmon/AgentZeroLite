using System.Linq;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>Headless tests for state rollup (H2) and native resume (H3).</summary>
[Trait("Category", "AgentRollupResume")]
public sealed class AgentRollupAndResumeTests
{
    private static AgentTabState T(AgentActivity a, bool seen = true) => new("t", a, seen);

    // ── H2 rollup ───────────────────────────────────────────────────────────

    [Fact]
    public void Rollup_BlockedBeatsEverything()
    {
        var r = AgentStateRollup.Rollup(new[] { T(AgentActivity.Working), T(AgentActivity.Blocked), T(AgentActivity.Idle) });
        Assert.Equal(AgentActivity.Blocked, r);
    }

    [Fact]
    public void Rollup_WorkingBeatsDoneAndIdle()
    {
        var r = AgentStateRollup.Rollup(new[] { T(AgentActivity.Idle), T(AgentActivity.Working), T(AgentActivity.Done, seen: false) });
        Assert.Equal(AgentActivity.Working, r);
    }

    [Fact]
    public void Rollup_UnseenDoneBeatsIdle()
    {
        var r = AgentStateRollup.Rollup(new[] { T(AgentActivity.Idle), T(AgentActivity.Done, seen: false) });
        Assert.Equal(AgentActivity.Done, r);
    }

    [Fact]
    public void Rollup_SeenDone_DoesNotBeatIdle()
    {
        var r = AgentStateRollup.Rollup(new[] { T(AgentActivity.Idle), T(AgentActivity.Done, seen: true) });
        Assert.Equal(AgentActivity.Idle, r);
    }

    [Fact]
    public void Rollup_Empty_IsUnknown()
    {
        Assert.Equal(AgentActivity.Unknown, AgentStateRollup.Rollup(System.Array.Empty<AgentTabState>()));
    }

    [Fact]
    public void NeedsAttention_OnBlockedOrUnseenDone()
    {
        Assert.True(AgentStateRollup.NeedsAttention(new[] { T(AgentActivity.Working), T(AgentActivity.Blocked) }));
        Assert.True(AgentStateRollup.NeedsAttention(new[] { T(AgentActivity.Idle), T(AgentActivity.Done, seen: false) }));
        Assert.False(AgentStateRollup.NeedsAttention(new[] { T(AgentActivity.Working), T(AgentActivity.Done, seen: true) }));
        Assert.Equal(2, AgentStateRollup.AttentionCount(new[] { T(AgentActivity.Blocked), T(AgentActivity.Done, seen: false), T(AgentActivity.Idle) }));
    }

    // ── H3 resume ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Claude 1", "abc123", "claude --resume abc123")]
    [InlineData("codex-main", "sess_9", "codex resume sess_9")]
    [InlineData("cursor", "id1", "cursor-agent --resume id1")]
    [InlineData("opencode", "x", "opencode --session x")]
    [InlineData("copilot", "y", "copilot --resume=y")]
    public void BuildResumeCommand_KnownAgents(string name, string id, string expected)
    {
        Assert.Equal(expected, AgentResumeCatalog.BuildResumeCommand(name, id));
    }

    [Fact]
    public void BuildResumeCommand_UnknownAgent_IsNull()
    {
        Assert.Null(AgentResumeCatalog.BuildResumeCommand("RTX-NOTE", "abc"));
    }

    [Fact]
    public void BuildResumeCommand_UnsafeSessionId_IsNull()
    {
        // Injection attempt / spaces / empty are rejected.
        Assert.Null(AgentResumeCatalog.BuildResumeCommand("claude", "abc; rm -rf /"));
        Assert.Null(AgentResumeCatalog.BuildResumeCommand("claude", ""));
        Assert.Null(AgentResumeCatalog.BuildResumeCommand("claude", "a b"));
    }

    [Fact]
    public void SupportedAgents_IncludesCommonClis()
    {
        var supported = AgentResumeCatalog.SupportedAgents.ToList();
        Assert.Contains("claude", supported);
        Assert.Contains("codex", supported);
        Assert.Contains("cursor", supported);
    }
}
