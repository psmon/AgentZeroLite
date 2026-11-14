using System;
using Agent.Common.Agents;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>A tab's own agent conversations, and which one the resume question offers as "this tab's last".</summary>
[Trait("Category", "AgentSession")]
public sealed class TabSessionMemoryTests
{
    private static AgentSessionRef Ref(string agent, string id) => new(agent, "C:/home/.claude", id, "C:/ws");

    private static AgentSessionInfo Info(string id) => new(id, null, "prompt " + id, DateTime.UtcNow, 100);

    [Fact]
    public void Push_PutsTheNewestFirst_AndReportsNoChangeForTheSameOne()
    {
        var stored = TabSessionMemory.Push(null, Ref("claude", "aaa"));
        Assert.Equal("claude:aaa", stored);
        Assert.Null(TabSessionMemory.Push(stored, Ref("claude", "aaa")));

        stored = TabSessionMemory.Push(stored, Ref("claude", "bbb"));
        Assert.Equal("claude:bbb|claude:aaa", stored);

        // An earlier one coming back moves to the front instead of appearing twice.
        stored = TabSessionMemory.Push(stored, Ref("claude", "aaa"));
        Assert.Equal("claude:aaa|claude:bbb", stored);
    }

    [Fact]
    public void Push_KeepsOnlyAFew()
    {
        string? stored = null;
        foreach (var id in new[] { "s1", "s2", "s3", "s4", "s5" })
            stored = TabSessionMemory.Push(stored, Ref("codex", id)) ?? stored;
        Assert.Equal(TabSessionMemory.Keep, TabSessionMemory.Parse(stored).Count);
        Assert.Equal("codex:s5|codex:s4|codex:s3", stored);
    }

    [Fact]
    public void Push_RefusesAnUnsafeId()
    {
        Assert.Null(TabSessionMemory.Push(null, Ref("claude", "x; rm -rf /")));
        Assert.Null(TabSessionMemory.Push(null, Ref("claude", "a|b")));
    }

    [Fact]
    public void Parse_SkipsMalformedAndUnsafeEntries()
    {
        var entries = TabSessionMemory.Parse("claude:ok|garbage|:noagent|codex:|claude:bad id|CODEX:thread-1");
        Assert.Equal(new[] { ("claude", "ok"), ("codex", "thread-1") }, entries);
    }

    [Fact]
    public void Pick_ReturnsTheNewestOfThisToolThatTheStoreStillOffers()
    {
        // Newest first: a session left without a prompt (not in the list), then another tool's, then the real one.
        var stored = "claude:empty|codex:c1|claude:real|claude:older";
        var sessions = new[] { Info("other-tab"), Info("older"), Info("real") };

        var pick = TabSessionMemory.Pick(stored, AgentCliTools.Claude, sessions);

        Assert.NotNull(pick);
        Assert.Equal("real", pick!.Id);
        Assert.Same(sessions[2], pick);
    }

    [Fact]
    public void Pick_IsNullWhenNoneOfThisTabsSessionsIsListed()
    {
        Assert.Null(TabSessionMemory.Pick("claude:gone", AgentCliTools.Claude, new[] { Info("another") }));
        Assert.Null(TabSessionMemory.Pick("codex:another", AgentCliTools.Claude, new[] { Info("another") }));
        Assert.Null(TabSessionMemory.Pick(null, AgentCliTools.Claude, new[] { Info("another") }));
    }

    [Fact]
    public void Has_LooksOnlyAtTheGivenTool()
    {
        Assert.True(TabSessionMemory.Has("codex:a|claude:b", AgentCliTools.Claude));
        Assert.False(TabSessionMemory.Has("codex:a", AgentCliTools.Claude));
    }
}
