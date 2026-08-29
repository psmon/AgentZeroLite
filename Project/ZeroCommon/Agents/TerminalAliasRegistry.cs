using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Common.Agents;

/// <summary>
/// A stable name → terminal mapping (herdr-adoption H5). Terminals are otherwise
/// addressed only by a volatile <c>(group_index, tab_index)</c> pair that shifts
/// when tabs are added, closed, or reordered. An alias lets <c>-cli</c> commands
/// target a terminal by a name the operator assigns once.
///
/// The target is stored as the stable <c>(GroupName, Title)</c> pair — the same
/// identity that already forms a session id — NOT the volatile indices. The WPF
/// side resolves that pair to current indices against the live group list at call
/// time. Pure &amp; WPF-free so it is headlessly testable and persists as JSON.
///
/// Collision note: (GroupName, Title) is not guaranteed unique. The registry
/// stores whatever you assign; the resolver picks the first live terminal whose
/// group + title match. Assign aliases to distinctly-titled tabs to stay
/// unambiguous.
/// </summary>
public sealed class TerminalAliasRegistry
{
    /// <summary>Stable identity of the aliased terminal.</summary>
    public sealed record AliasTarget(string GroupName, string Title);

    private static readonly Regex ValidAlias = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    // Alias comparison is case-insensitive so `-cli ... --alias Build` and
    // `--alias build` hit the same terminal.
    private readonly Dictionary<string, AliasTarget> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current alias → target entries (read-only snapshot view).</summary>
    public IReadOnlyDictionary<string, AliasTarget> Entries => _map;

    /// <summary>Whether <paramref name="alias"/> is a legal alias token (alnum / dash / underscore, 1–64 chars).</summary>
    public static bool IsValidAlias(string? alias)
        => !string.IsNullOrWhiteSpace(alias) && ValidAlias.IsMatch(alias.Trim());

    /// <summary>Assign (or reassign) an alias to a terminal identity. Returns false on an invalid alias or empty group/title.</summary>
    public bool Set(string alias, string groupName, string title)
    {
        if (!IsValidAlias(alias)) return false;
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(title)) return false;
        _map[alias.Trim()] = new AliasTarget(groupName, title);
        return true;
    }

    /// <summary>Remove an alias. Returns false if it was not present.</summary>
    public bool Remove(string alias)
        => !string.IsNullOrWhiteSpace(alias) && _map.Remove(alias.Trim());

    /// <summary>Resolve an alias to its stable target, or null if unknown.</summary>
    public AliasTarget? Resolve(string? alias)
        => alias is not null && _map.TryGetValue(alias.Trim(), out var t) ? t : null;

    /// <summary>Drop aliases whose target is no longer among the live terminals. Returns the number pruned.</summary>
    public int Prune(IEnumerable<AliasTarget> liveTargets)
    {
        var live = new HashSet<AliasTarget>(liveTargets);
        var dead = new List<string>();
        foreach (var kv in _map)
            if (!live.Contains(kv.Value)) dead.Add(kv.Key);
        foreach (var k in dead) _map.Remove(k);
        return dead.Count;
    }

    // ── JSON persistence (mirrors BudgetSettingsStore) ────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "terminal-aliases.json");

    private sealed class Dto
    {
        public Dictionary<string, AliasTarget> Aliases { get; set; } = new();
    }

    /// <summary>Serialize to indented JSON.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(new Dto { Aliases = new(_map) }, JsonOpts);

    /// <summary>Parse from JSON; returns an empty registry on any error.</summary>
    public static TerminalAliasRegistry FromJson(string? json)
    {
        var reg = new TerminalAliasRegistry();
        if (string.IsNullOrWhiteSpace(json)) return reg;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto?.Aliases is not null)
                foreach (var kv in dto.Aliases)
                    if (kv.Value is not null)
                        reg.Set(kv.Key, kv.Value.GroupName, kv.Value.Title);
        }
        catch { /* corrupt file → empty registry */ }
        return reg;
    }

    /// <summary>Load from the default path (or an explicit one, for tests). Never throws.</summary>
    public static TerminalAliasRegistry Load(string? path = null)
    {
        path ??= DefaultPath;
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new TerminalAliasRegistry(); }
        catch { return new TerminalAliasRegistry(); }
    }

    /// <summary>Persist to the default path (or an explicit one, for tests).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }
}
