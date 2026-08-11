using System;
using System.Collections.Generic;
using System.IO;

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
        // Actively generating — interrupt hint, or the "✻ Gerund…" spinner with
        // its live token counter (as opposed to the "✻ Cooked for 7s" done line).
        new("working_interrupt", AgentActivity.Working, 900, ScreenRegion.BottomLines,
            new MatchGroup
            {
                Any = new[]
                {
                    C("esc to interrupt"),
                    C("esc to stop"),
                    Rx(@"✻\s+\w+…"),          // spinner: "✻ Gallivanting…"
                    Rx(@"\(\d+s\b.*tokens\)"), // live token counter while streaming
                },
            }) { BottomLines = 8 },
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

    /// <summary>
    /// Directory scanned for JSON manifest overrides (herdr's "rules are data").
    /// Drop <c>&lt;agentId&gt;.json</c> here to override the built-in rules without a
    /// rebuild. Defaults to <c>%LOCALAPPDATA%\AgentZeroLite\agent-detection</c>.
    /// </summary>
    public static string OverrideDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "agent-detection");

    private static readonly Dictionary<string, AgentManifest> _cache = new();

    /// <summary>Maps an agent/tab name to its manifest id (claude/codex/generic).</summary>
    public static string ResolveId(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("claude")) return "claude";
        if (n.Contains("codex")) return "codex";
        return "generic";
    }

    private static AgentManifest BuiltIn(string id) => id switch
    {
        "claude" => Claude,
        "codex" => Codex,
        _ => Generic,
    };

    /// <summary>
    /// Picks a manifest by agent/tab name: a JSON override in
    /// <see cref="OverrideDir"/> wins over the built-in. Results are cached;
    /// call <see cref="Reload"/> after editing an override.
    /// </summary>
    public static AgentManifest ForAgent(string? name)
    {
        var id = ResolveId(name);
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var resolved = AgentManifestJson.LoadOverride(id, OverrideDir) ?? BuiltIn(id);
        _cache[id] = resolved;
        return resolved;
    }

    /// <summary>Clears the manifest cache so overrides are re-read.</summary>
    public static void Reload() => _cache.Clear();
}
