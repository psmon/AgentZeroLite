using System;
using System.Collections.Generic;

namespace Agent.Common.Agents;

/// <summary>
/// Built-in agent-state detection manifests (herdr-adoption H1). Ported in spirit
/// from herdr's TOML manifests; a data-driven default set that classifies common
/// coding-agent CLIs. Runtime JSON overrides can be layered on later; the point
/// is the same as herdr's — detection rules are DATA, not code.
/// </summary>
public static class AgentManifestCatalog
{
    private static MatchGroup C(params string[] contains) => new() { Contains = contains };
    private static MatchGroup Rx(params string[] regex) => new() { Regex = regex };

    /// <summary>Claude Code.</summary>
    public static AgentManifest Claude { get; } = new("claude", new DetectRule[]
    {
        // Braille spinner in the OS window title → working.
        new("osc_title_spinner", AgentActivity.Working, 1100, ScreenRegion.OscTitle,
            Rx(@"[⠀-⣿]")),
        // Approval / confirmation form → blocked.
        new("blocked_confirm", AgentActivity.Blocked, 980, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Contains = new[] { "esc to cancel" },
                Any = new[] { C("enter to confirm"), C("do you want to proceed"), C("enter to select") },
            }) { BottomLines = 6 },
        new("blocked_proceed", AgentActivity.Blocked, 975, ScreenRegion.BottomLines,
            C("do you want to proceed?")) { BottomLines = 6 },
        // Actively generating.
        new("working_interrupt", AgentActivity.Working, 900, ScreenRegion.BottomLines,
            new MatchGroup { Any = new[] { C("esc to interrupt"), C("esc to stop") } }) { BottomLines = 6 },
        // Empty prompt caret with no blocker markers → idle.
        new("idle_prompt", AgentActivity.Idle, 800, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Regex = new[] { @"^\s*❯", @"^\s*>" },
                NotContains = new[] { "esc to cancel", "enter to select", "do you want to proceed" },
            }) { BottomLines = 4 },
    });

    /// <summary>OpenAI Codex CLI.</summary>
    public static AgentManifest Codex { get; } = new("codex", new DetectRule[]
    {
        new("osc_title_spinner", AgentActivity.Working, 1100, ScreenRegion.OscTitle,
            Rx(@"[⠀-⣿]")),
        new("blocked_approve", AgentActivity.Blocked, 950, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Any = new[] { C("allow command"), C("approve"), C("[y/n]"), C("(y/n)"), C("press enter to run") },
            }) { BottomLines = 6 },
        new("working_interrupt", AgentActivity.Working, 900, ScreenRegion.BottomLines,
            new MatchGroup { Any = new[] { C("esc to interrupt"), C("working"), C("thinking") } }) { BottomLines = 6 },
    });

    /// <summary>
    /// Generic fallback for any CLI. Conservative: blocked on common approval
    /// phrasings, working on explicit interrupt/progress markers, else idle.
    /// </summary>
    public static AgentManifest Generic { get; } = new("generic", new DetectRule[]
    {
        new("blocked_prompt", AgentActivity.Blocked, 700, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Any = new[]
                {
                    C("(y/n)"), C("[y/n]"), C("y/n]"), C("do you want to proceed"),
                    C("press enter to continue"), C("continue? "), C("approve?"), C("allow this"),
                },
            }) { BottomLines = 6 },
        new("working_marker", AgentActivity.Working, 600, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Any = new[] { C("esc to interrupt"), C("esc to cancel"), C("generating"), C("compiling"), C("running…") },
            }) { BottomLines = 6 },
    });

    /// <summary>Picks a manifest by agent/tab name (substring, case-insensitive).</summary>
    public static AgentManifest ForAgent(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("claude")) return Claude;
        if (n.Contains("codex")) return Codex;
        return Generic;
    }
}
