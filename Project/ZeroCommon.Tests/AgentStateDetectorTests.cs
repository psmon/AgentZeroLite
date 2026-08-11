using System.Collections.Generic;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the screen-manifest agent-state detector (herdr-adoption H1).
/// </summary>
[Trait("Category", "AgentState")]
public sealed class AgentStateDetectorTests
{
    private static ScreenSnapshot Screen(params string[] lines) => new(lines, null);
    private static ScreenSnapshot Titled(string oscTitle, params string[] lines) => new(lines, oscTitle);

    // ── Claude ────────────────────────────────────────────────────────────────

    [Fact]
    public void Claude_Blocked_OnConfirmationForm()
    {
        var s = Screen(
            "Do you want to proceed?",
            "  1. Yes",
            "  2. No",
            "(enter to confirm · esc to cancel)");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Claude, s);
        Assert.Equal(AgentActivity.Blocked, r.State);
        Assert.NotNull(r.MatchedRuleId);
    }

    [Fact]
    public void Claude_Working_OnInterruptHint()
    {
        var s = Screen("Crafting a response…", "(esc to interrupt)");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Claude, s);
        Assert.Equal(AgentActivity.Working, r.State);
    }

    [Fact]
    public void Claude_Working_OnBrailleTitle()
    {
        var s = Titled("⠋ Claude", "some output");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Claude, s);
        Assert.Equal(AgentActivity.Working, r.State); // osc_title spinner wins (priority 1100)
    }

    [Fact]
    public void Claude_Idle_OnEmptyPrompt()
    {
        var s = Screen(
            "Done. Anything else?",
            "❯ ");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Claude, s);
        Assert.Equal(AgentActivity.Idle, r.State);
    }

    // ── generic + fallback ──────────────────────────────────────────────────────

    [Fact]
    public void Generic_Blocked_OnYesNo()
    {
        var s = Screen("Overwrite file? (y/n)");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Generic, s);
        Assert.Equal(AgentActivity.Blocked, r.State);
    }

    [Fact]
    public void Generic_Idle_WhenNothingMatches()
    {
        var s = Screen("$ ", "some normal shell output");
        var r = AgentStateDetector.Detect(AgentManifestCatalog.Generic, s);
        Assert.Equal(AgentActivity.Idle, r.State);     // fallback
        Assert.Null(r.MatchedRuleId);
    }

    // ── priority + skip_state_update ────────────────────────────────────────────

    [Fact]
    public void HigherPriorityRule_Wins()
    {
        var manifest = new AgentManifest("t", new DetectRule[]
        {
            new("low", AgentActivity.Idle, 10, ScreenRegion.Full, new MatchGroup { Contains = new[] { "x" } }),
            new("high", AgentActivity.Blocked, 100, ScreenRegion.Full, new MatchGroup { Contains = new[] { "x" } }),
        });
        var r = AgentStateDetector.Detect(manifest, Screen("x"));
        Assert.Equal(AgentActivity.Blocked, r.State);
        Assert.Equal("high", r.MatchedRuleId);
    }

    [Fact]
    public void SkipStateUpdate_ReportsNoChange()
    {
        var manifest = new AgentManifest("t", new DetectRule[]
        {
            new("viewer", AgentActivity.Unknown, 100, ScreenRegion.Full, new MatchGroup { Contains = new[] { "transcript" } })
                { SkipStateUpdate = true },
        });
        var r = AgentStateDetector.Detect(manifest, Screen("showing detailed transcript"));
        Assert.False(r.StateChanged);
        Assert.Equal("viewer", r.MatchedRuleId);
    }

    // ── match semantics ─────────────────────────────────────────────────────────

    [Fact]
    public void Contains_AllMustBePresent()
    {
        var g = new MatchGroup { Contains = new[] { "a", "b" } };
        var manifest = new AgentManifest("t", new[] { new DetectRule("r", AgentActivity.Blocked, 1, ScreenRegion.Full, g) });
        Assert.Equal(AgentActivity.Blocked, AgentStateDetector.Detect(manifest, Screen("a", "b")).State);
        Assert.Equal(AgentActivity.Idle, AgentStateDetector.Detect(manifest, Screen("a")).State); // fallback
    }

    [Fact]
    public void NotContains_Blocks()
    {
        var g = new MatchGroup { Contains = new[] { "a" }, NotContains = new[] { "b" } };
        var manifest = new AgentManifest("t", new[] { new DetectRule("r", AgentActivity.Blocked, 1, ScreenRegion.Full, g) });
        Assert.Equal(AgentActivity.Idle, AgentStateDetector.Detect(manifest, Screen("a", "b")).State); // b present → rule fails
    }

    [Fact]
    public void BottomLines_OnlyLooksAtTail()
    {
        var g = new MatchGroup { Contains = new[] { "needle" } };
        var manifest = new AgentManifest("t", new[]
        {
            new DetectRule("r", AgentActivity.Blocked, 1, ScreenRegion.BottomLines, g) { BottomLines = 2 },
        });
        // "needle" is far above the last 2 non-empty lines → not matched.
        var s = Screen("needle", "l1", "l2", "l3");
        Assert.Equal(AgentActivity.Idle, AgentStateDetector.Detect(manifest, s).State);
    }

    [Fact]
    public void ForAgent_PicksByName()
    {
        Assert.Equal("claude", AgentManifestCatalog.ForAgent("Claude 1").Id);
        Assert.Equal("codex", AgentManifestCatalog.ForAgent("codex-main").Id);
        Assert.Equal("generic", AgentManifestCatalog.ForAgent("RTX-NOTE").Id);
    }
}
