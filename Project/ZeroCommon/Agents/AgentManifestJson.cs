using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>
/// Loads agent-state detection manifests from JSON (herdr-adoption H1). Mirrors
/// herdr's "rules are data" model: a user can drop
/// <c>%LOCALAPPDATA%\AgentZeroLite\agent-detection\&lt;agent&gt;.json</c> to override or
/// add detection rules without a rebuild. Pure &amp; headlessly testable.
///
/// JSON shape:
/// <code>
/// { "id":"claude", "fallback":"idle", "rules":[
///     { "id":"blocked_confirm", "state":"blocked", "priority":980,
///       "region":"bottom", "bottomLines":6, "skipStateUpdate":false,
///       "match": { "contains":["esc to cancel"], "regex":[], "notContains":[],
///                  "any":[ {"contains":["enter to confirm"]} ] } } ] }
/// </code>
/// </summary>
public static class AgentManifestJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── DTOs (JSON-shaped) ───────────────────────────────────────────────────
    private sealed class ManifestDto
    {
        public string Id { get; set; } = "";
        public string? Fallback { get; set; }
        public List<RuleDto> Rules { get; set; } = new();
    }
    private sealed class RuleDto
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "idle";
        public int Priority { get; set; }
        public string Region { get; set; } = "full";
        public int BottomLines { get; set; } = 5;
        public bool SkipStateUpdate { get; set; }
        public GroupDto? Match { get; set; }
    }
    private sealed class GroupDto
    {
        public List<string>? Contains { get; set; }
        public List<string>? Regex { get; set; }
        public List<string>? NotContains { get; set; }
        public List<GroupDto>? Any { get; set; }
    }

    /// <summary>Parses a manifest JSON string. Throws on malformed JSON.</summary>
    public static AgentManifest Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, Opts)
                  ?? throw new JsonException("empty manifest");
        var rules = dto.Rules.Select(ToRule).ToList();
        var manifest = new AgentManifest(dto.Id, rules);
        if (dto.Fallback is not null && Enum.TryParse<AgentActivity>(dto.Fallback, true, out var fb))
            manifest = manifest with { Fallback = fb };
        return manifest;
    }

    /// <summary>
    /// Loads an override manifest for an agent id from <paramref name="dir"/>
    /// (<c>&lt;dir&gt;/&lt;agentId&gt;.json</c>), or null if absent/unreadable.
    /// </summary>
    public static AgentManifest? LoadOverride(string agentId, string dir)
    {
        try
        {
            var path = Path.Combine(dir, agentId + ".json");
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static DetectRule ToRule(RuleDto r)
    {
        var state = Enum.TryParse<AgentActivity>(r.State, true, out var s) ? s : AgentActivity.Idle;
        var region = r.Region.ToLowerInvariant() switch
        {
            "osc" or "osc_title" => ScreenRegion.OscTitle,
            "bottom" or "bottom_lines" => ScreenRegion.BottomLines,
            _ => ScreenRegion.Full,
        };
        return new DetectRule(r.Id, state, r.Priority, region, ToGroup(r.Match))
        {
            BottomLines = r.BottomLines,
            SkipStateUpdate = r.SkipStateUpdate,
        };
    }

    private static MatchGroup ToGroup(GroupDto? g)
    {
        if (g is null) return new MatchGroup();
        return new MatchGroup
        {
            Contains = g.Contains ?? new List<string>(),
            Regex = g.Regex ?? new List<string>(),
            NotContains = g.NotContains ?? new List<string>(),
            Any = (g.Any ?? new List<GroupDto>()).Select(ToGroup).ToList(),
        };
    }
}
