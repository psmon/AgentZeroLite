using System.Linq;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the served skill guides + anti-drift stub (missions W4/W5).
/// </summary>
[Trait("Category", "SkillGuides")]
public sealed class AgentSkillGuidesTests
{
    [Fact]
    public void Get_KnownTopic_ReturnsGuide()
    {
        var g = AgentSkillGuides.Get("agentzero");
        Assert.NotNull(g);
        Assert.Contains("terminal-wait", g);
        Assert.Contains("worktree", g);
    }

    [Fact]
    public void Get_UnknownTopic_ReturnsNull()
    {
        Assert.Null(AgentSkillGuides.Get("nope"));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.NotNull(AgentSkillGuides.Get("AgentZero"));
    }

    [Fact]
    public void BuildStub_PointsAtServedGuide_NotInlineCommands()
    {
        var stub = AgentSkillGuides.BuildStub("agentzero");
        // Anti-drift: the stub must direct to the served guide...
        Assert.Contains("-cli help agentzero", stub);
        // ...and carry the marker for safe uninstall...
        Assert.Contains(AgentSkillGuides.StubMarker, stub);
        // ...and explicitly declare itself a stub, not the guide.
        Assert.Contains("discovery stub", stub);
    }

    [Fact]
    public void BuildStub_HasSkillFrontmatter()
    {
        var stub = AgentSkillGuides.BuildStub();
        Assert.StartsWith("---", stub);
        Assert.Contains("name: agentzero-control", stub);
        Assert.Contains("description:", stub);
    }
}
