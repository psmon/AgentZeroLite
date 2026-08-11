using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agent.Common.Agents;

/// <summary>
/// Lifecycle state of a hosted coding-agent CLI, detected from its terminal
/// screen (herdr-adoption H1). Broader than approval detection: covers the full
/// working/blocked/idle/done arc for ANY CLI, including ones without hooks.
/// </summary>
public enum AgentActivity { Unknown, Idle, Working, Blocked, Done }

/// <summary>A snapshot of a terminal's current screen for state detection.</summary>
public sealed record ScreenSnapshot(IReadOnlyList<string> Lines, string? OscTitle = null);

/// <summary>Which part of the screen a rule looks at.</summary>
public enum ScreenRegion { Full, BottomLines, OscTitle }

/// <summary>
/// A match group: matches when ALL specified sub-conditions hold. Empty
/// sub-conditions are trivially satisfied.
///   Contains    — every needle appears on some line (case-insensitive)
///   Regex       — at least one pattern matches some line
///   NotContains — none of these needles appear on any line
///   Any         — at least one sub-group matches
/// </summary>
public sealed record MatchGroup
{
    public IReadOnlyList<string> Contains { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Regex { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NotContains { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MatchGroup> Any { get; init; } = Array.Empty<MatchGroup>();
}

/// <summary>One detection rule (mirrors a herdr manifest rule).</summary>
public sealed record DetectRule(
    string Id,
    AgentActivity State,
    int Priority,
    ScreenRegion Region,
    MatchGroup Match)
{
    /// <summary>For BottomLines region — how many trailing non-empty lines to consider.</summary>
    public int BottomLines { get; init; } = 5;
    /// <summary>When true the rule only classifies context (e.g. a viewer) and does not change state.</summary>
    public bool SkipStateUpdate { get; init; }
}

/// <summary>A per-agent set of detection rules + fallback.</summary>
public sealed record AgentManifest(string Id, IReadOnlyList<DetectRule> Rules)
{
    /// <summary>State when no rule matches (known agents fall back to Idle).</summary>
    public AgentActivity Fallback { get; init; } = AgentActivity.Idle;
}

/// <summary>Result of detection — the state plus which rule decided it (for `explain`).</summary>
public sealed record DetectResult(AgentActivity State, string? MatchedRuleId, bool StateChanged = true);

/// <summary>
/// Pure (WPF-free) rule engine that classifies an agent's lifecycle state from a
/// terminal screen snapshot (herdr-adoption H1). Rules are evaluated highest
/// priority first; the first matching rule wins. A matching rule flagged
/// <see cref="DetectRule.SkipStateUpdate"/> means "context only — keep the prior
/// state". No rule matching → the manifest fallback.
/// </summary>
public static class AgentStateDetector
{
    public static DetectResult Detect(AgentManifest manifest, ScreenSnapshot snapshot)
    {
        foreach (var rule in manifest.Rules.OrderByDescending(r => r.Priority))
        {
            var region = RegionLines(rule, snapshot);
            if (Matches(rule.Match, region))
            {
                return rule.SkipStateUpdate
                    ? new DetectResult(AgentActivity.Unknown, rule.Id, StateChanged: false)
                    : new DetectResult(rule.State, rule.Id);
            }
        }
        return new DetectResult(manifest.Fallback, null);
    }

    private static IReadOnlyList<string> RegionLines(DetectRule rule, ScreenSnapshot snap) => rule.Region switch
    {
        ScreenRegion.OscTitle => snap.OscTitle is null ? Array.Empty<string>() : new[] { snap.OscTitle },
        ScreenRegion.BottomLines => snap.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Reverse().Take(Math.Max(1, rule.BottomLines)).Reverse().ToList(),
        _ => snap.Lines,
    };

    private static bool Matches(MatchGroup g, IReadOnlyList<string> lines)
    {
        foreach (var needle in g.Contains)
            if (!AnyLineContains(lines, needle)) return false;

        if (g.Regex.Count > 0 && !g.Regex.Any(p => AnyLineMatches(lines, p)))
            return false;

        foreach (var needle in g.NotContains)
            if (AnyLineContains(lines, needle)) return false;

        if (g.Any.Count > 0 && !g.Any.Any(sub => Matches(sub, lines)))
            return false;

        return true;
    }

    private static bool AnyLineContains(IReadOnlyList<string> lines, string needle)
        => lines.Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool AnyLineMatches(IReadOnlyList<string> lines, string pattern)
    {
        try
        {
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            return lines.Any(rx.IsMatch);
        }
        catch { return false; }
    }
}
