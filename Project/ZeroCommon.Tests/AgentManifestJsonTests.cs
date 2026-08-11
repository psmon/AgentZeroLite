using System;
using System.IO;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for JSON manifest overrides + improved built-in detection
/// (herdr-adoption H1 data-driven rules).
/// </summary>
[Trait("Category", "AgentManifestJson")]
public sealed class AgentManifestJsonTests : IDisposable
{
    private readonly string _dir;

    public AgentManifestJsonTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aztest-manifest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        AgentManifestCatalog.OverrideDir = _dir;
        AgentManifestCatalog.Reload();
    }

    public void Dispose()
    {
        AgentManifestCatalog.OverrideDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentZeroLite", "agent-detection");
        AgentManifestCatalog.Reload();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Parse_BuildsManifestFromJson()
    {
        var json = """
        { "id":"claude", "fallback":"idle", "rules":[
            { "id":"blocked", "state":"blocked", "priority":900, "region":"bottom", "bottomLines":6,
              "match": { "contains":["approve?"] } } ] }
        """;
        var m = AgentManifestJson.Parse(json);
        Assert.Equal("claude", m.Id);
        Assert.Single(m.Rules);
        var r = AgentStateDetector.Detect(m, new ScreenSnapshot(new[] { "approve? (y/n)" }));
        Assert.Equal(AgentActivity.Blocked, r.State);
    }

    [Fact]
    public void Parse_SupportsNestedAnyAndNot()
    {
        var json = """
        { "id":"t", "rules":[
            { "id":"r", "state":"blocked", "priority":10, "region":"full",
              "match": { "contains":["esc to cancel"],
                         "any":[ {"contains":["enter to confirm"]}, {"contains":["do you want"]} ],
                         "notContains":["transcript"] } } ] }
        """;
        var m = AgentManifestJson.Parse(json);
        Assert.Equal(AgentActivity.Blocked,
            AgentStateDetector.Detect(m, new ScreenSnapshot(new[] { "esc to cancel", "enter to confirm" })).State);
        // notContains blocks it
        Assert.Equal(AgentActivity.Idle,
            AgentStateDetector.Detect(m, new ScreenSnapshot(new[] { "esc to cancel", "enter to confirm", "transcript" })).State);
    }

    [Fact]
    public void ForAgent_OverrideWinsOverBuiltin()
    {
        // Override claude so that "MARKER" → blocked (built-in has no such rule).
        File.WriteAllText(Path.Combine(_dir, "claude.json"), """
        { "id":"claude", "rules":[
            { "id":"custom", "state":"blocked", "priority":9999, "region":"full",
              "match": { "contains":["MARKER"] } } ] }
        """);
        AgentManifestCatalog.Reload();
        var m = AgentManifestCatalog.ForAgent("Claude 1");
        var r = AgentStateDetector.Detect(m, new ScreenSnapshot(new[] { "MARKER here" }));
        Assert.Equal(AgentActivity.Blocked, r.State);
        Assert.Equal("custom", r.MatchedRuleId);
    }

    [Fact]
    public void ForAgent_FallsBackToBuiltin_WhenNoOverride()
    {
        // No override file → built-in claude manifest.
        var m = AgentManifestCatalog.ForAgent("Claude 1");
        Assert.Equal("claude", m.Id);
    }

    // ── improved built-in working detection ─────────────────────────────────

    [Fact]
    public void Claude_Working_OnSpinnerGerund()
    {
        var m = AgentManifestCatalog.ForAgent("Claude");
        var s = new ScreenSnapshot(new[] { "✻ Gallivanting… (25s · ↓ 935 tokens)", "" });
        Assert.Equal(AgentActivity.Working, AgentStateDetector.Detect(m, s).State);
    }

    [Fact]
    public void Claude_Working_OnTokenCounter()
    {
        var m = AgentManifestCatalog.ForAgent("Claude");
        var s = new ScreenSnapshot(new[] { "Some output", "(12s · 4200 tokens)" });
        Assert.Equal(AgentActivity.Working, AgentStateDetector.Detect(m, s).State);
    }
}
