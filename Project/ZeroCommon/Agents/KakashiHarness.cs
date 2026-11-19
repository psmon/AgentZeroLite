using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Common.Agents;

/// <summary>One expert of a harness, from its <c>harness/agents/&lt;name&gt;.md</c> frontmatter.</summary>
/// <param name="Name">The file's name and the config's <c>agents</c> entry ("lyricist", "security-guard").</param>
/// <param name="Persona">How the harness calls it ("작사가", "Security Guard"); null when the file says nothing.</param>
/// <param name="Type">"specialist" · "sage" · "keeper" when the file says; null otherwise.</param>
public sealed record HarnessExpert(string Name, string? Persona, string? Type, string? Description);

/// <summary>A harness found above a session's folder — the only kind recognised is <see cref="KakashiHarness.Schema"/>.</summary>
/// <param name="Root">The folder that holds <c>harness/</c>.</param>
public sealed record HarnessRoster(string Root, string Name, string? Version, IReadOnlyDictionary<string, HarnessExpert> Experts)
{
    public HarnessExpert? Expert(string name) => Experts.TryGetValue(name, out var e) ? e : null;
}

/// <summary>
/// The garden harness (harness-kakashi plugin, <c>"$schema": "kakashi-harness"</c>) and the two ways
/// a Claude session puts one of its experts to work — the only harness the Agent Stage draws, and
/// only for Claude. Measured on this repository and music-harness (2026-11-19):
/// <list type="bullet">
/// <item>As a <b>sub-agent</b>: a general-purpose Agent call whose prompt opens "You are acting as the
/// **lyricist (작사가)** specialist …" and names exactly one <c>harness/agents/lyricist.md</c> to read;
/// its description leads with the name ("lyricist review of Comma"). An Explore that surveys the harness
/// names several agent files and is not an expert — <see cref="ExpertOf"/> refuses an ambiguous prompt.</item>
/// <item><b>Inline</b>: the main conversation itself Reads <c>harness/agents/&lt;name&gt;.md</c> and follows
/// it (the harness-creator skill's "read the matched agent's definition"), or <c>/harness-creator</c> is
/// invoked — the gardener (<c>tamer</c>) at work. See <see cref="ClaudeHarnessDutyReader"/>.</item>
/// </list>
/// </summary>
public static partial class KakashiHarness
{
    public const string Schema = "kakashi-harness";

    /// <summary>The meta-agent every garden has; the one a <c>/harness-creator</c> call puts on duty.</summary>
    public const string Gardener = "tamer";

    [GeneratedRegex(@"harness[\\/]+agents[\\/]+([A-Za-z0-9][A-Za-z0-9_-]*)\.md", RegexOptions.IgnoreCase)]
    internal static partial Regex AgentFile();

    // "acting as the **lyricist (작사가)** specialist", "You are the security-guard agent"
    // "You are acting as …" must not stop at "acting": the lookahead lets the scan go on to "acting as".
    [GeneratedRegex(@"\b(?:acting\s+as|you\s+are)\s+(?:the\s+|an?\s+)?\**`?(?!acting\b)([A-Za-z0-9][A-Za-z0-9_-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ActingAs();

    [GeneratedRegex(@"^\s*([A-Za-z0-9][A-Za-z0-9_-]*)")]
    private static partial Regex LeadWord();

    /// <summary>
    /// Which expert a sub-agent was launched as, or null. In order: the one agent file its prompt names
    /// (several → the one it is told it is "acting as", else none); the name after "acting as" alone;
    /// the description's first word when that is an expert's name.
    /// </summary>
    public static HarnessExpert? ExpertOf(HarnessRoster roster, string? prompt, string? description)
    {
        prompt ??= "";
        var named = AgentFile().Matches(prompt).Select(m => m.Groups[1].Value)
            .Where(n => roster.Experts.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var acting = ActingAs().Matches(prompt).Select(m => m.Groups[1].Value)
            .FirstOrDefault(n => roster.Experts.ContainsKey(n));

        if (named.Count == 1) return roster.Expert(named[0]);
        if (named.Count > 1) return acting is not null && named.Contains(acting, StringComparer.OrdinalIgnoreCase) ? roster.Expert(acting) : null;
        if (acting is not null) return roster.Expert(acting);
        return LeadWord().Match(description ?? "") is { Success: true } lead ? roster.Expert(lead.Groups[1].Value) : null;
    }

    /// <summary>The expert an agent-file path names, when it is this roster's own file.</summary>
    public static HarnessExpert? ExpertOfPath(HarnessRoster roster, string? path)
    {
        if (path is null || AgentFile().Match(path) is not { Success: true } m) return null;
        // Only this garden's files: a path under another repository's harness is not this session's expert.
        return InGarden(roster, path) ? roster.Expert(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"harness[\\/]+logs[\\/]+([A-Za-z0-9][A-Za-z0-9_-]*)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex LogFile();

    // Shell forms that change a file rather than read it: redirection (not 2>/dev/null, 2>&1, >$null),
    // in-place edits, moves, deletes.
    [GeneratedRegex(@"(?<![\d&])>>?(?!\s*(?:/dev/null|\$null|nul\b|&))|\btee\b|\bsed\s+-i|\b(?:Set|Add|Clear)-Content\b|\bOut-File\b|\b(?:mv|cp|rm)\s|\b(?:Move|Copy|Remove|Rename)-Item\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShellWrites();

    /// <summary>
    /// The expert whose log a path is — <c>harness/logs/&lt;name&gt;/…</c> — when that name is in this roster.
    /// The garden's own rule is that an expert's work ends with its log, so writing one is proof of duty even
    /// when the definition was already in the conversation and was not read again (measured: a turn that
    /// worked as build-doctor wrote <c>harness/logs/build-doctor/…</c>).
    /// </summary>
    public static HarnessExpert? ExpertOfLog(HarnessRoster roster, string? path)
    {
        if (path is null || LogFile().Match(path) is not { Success: true } m || !InGarden(roster, path)) return null;
        return roster.Expert(m.Groups[1].Value);
    }

    /// <summary>
    /// The one expert a shell command names by its agent file, or null (none, or several — a survey such
    /// as <c>grep triggers harness/agents/*.md</c> is not anyone's duty). Measured: a turn that worked as
    /// build-doctor read its definition with Bash <c>cat harness/agents/build-doctor.md</c>, not the Read tool.
    /// </summary>
    /// <param name="writes">True when the command changes files — tending the garden, not working as the expert.</param>
    public static HarnessExpert? ExpertOfCommand(HarnessRoster roster, string? command, out bool writes)
    {
        writes = false;
        if (string.IsNullOrEmpty(command)) return null;
        var named = new List<string>();
        foreach (Match m in AgentFile().Matches(command))
        {
            // The path token the match sits in (back to a space or quote), so another repository's
            // absolute path is told apart from this garden's.
            var start = m.Index;
            while (start > 0 && !char.IsWhiteSpace(command[start - 1]) && command[start - 1] is not ('"' or '\'' or '=')) start--;
            var token = command[start..(m.Index + m.Length)];
            if (ExpertOfPath(roster, token) is { } e && !named.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) named.Add(e.Name);
        }
        if (named.Count != 1) return null;
        writes = ShellWrites().IsMatch(command);
        return roster.Expert(named[0]);
    }

    private static bool InGarden(HarnessRoster roster, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return true;
        var root = roster.Root.Replace('\\', '/').TrimEnd('/');
        return path.Replace('\\', '/').StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A Skill call or slash command that runs the gardener (current and deprecated names).</summary>
    public static bool IsGardenerCommand(string? text) =>
        text is not null && (text.Contains("harness-creator", StringComparison.OrdinalIgnoreCase)
                             || text.Contains("harness-kakashi-creator", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads <c>harness/harness.config.json</c> under <paramref name="root"/>; null unless it is a garden harness.</summary>
    public static HarnessRoster? Load(string root)
    {
        var harness = Path.Combine(root, "harness");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness, "harness.config.json")));
            var r = doc.RootElement;
            if (Str(r, "$schema") != Schema) return null;
            var experts = new Dictionary<string, HarnessExpert>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
                foreach (var a in agents.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } name)
                        experts[name] = ReadExpert(Path.Combine(harness, "agents", name + ".md"), name);
            return new HarnessRoster(root, Str(r, "name") ?? Path.GetFileName(root), Str(r, "version"), experts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The frontmatter's one-line fields; a missing or odd file still gives the expert its name.</summary>
    internal static HarnessExpert ReadExpert(string path, string name)
    {
        string? persona = null, type = null, description = null;
        try
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine()?.Trim() == "---")
                for (var i = 0; i < 80 && reader.ReadLine() is { } line && line.Trim() != "---"; i++)
                {
                    if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;   // list items, folded text
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (value.Length == 0) continue;
                    switch (line[..colon].Trim())
                    {
                        case "persona": persona = value; break;
                        case "type": type = value; break;
                        case "description": description = value.Length > 200 ? value[..200] + "…" : value; break;
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new HarnessExpert(name, persona, type, description);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Finds the garden harness a session works in: <c>harness/harness.config.json</c> in its folder or
/// above it, stopping at the repository root (a folder with <c>.git</c>). Asked every 1.5 s per Claude
/// tab on the UI thread, so a folder's answer is kept for <see cref="RecheckMs"/> and the config is
/// re-read only when its write time moved.
/// </summary>
public sealed class HarnessLocator
{
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(long CheckedAt, string? ConfigPath, DateTime Written, HarnessRoster? Roster);

    public long RecheckMs { get; init; } = 15_000;

    /// <summary>How far up a session's folder is searched.</summary>
    public int MaxLevels { get; init; } = 6;

    public HarnessLocator(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    public HarnessRoster? Find(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var nowMs = _now().ToUnixTimeMilliseconds();
        if (_cache.TryGetValue(cwd, out var e) && nowMs - e.CheckedAt < RecheckMs) return e.Roster;

        var config = Locate(cwd);
        DateTime written = default;
        try { if (config is not null) written = File.GetLastWriteTimeUtc(config); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { config = null; }

        var roster = e is not null && e.ConfigPath == config && e.Written == written
            ? e.Roster
            : config is null ? null : KakashiHarness.Load(Path.GetDirectoryName(Path.GetDirectoryName(config))!);
        _cache[cwd] = new Entry(nowMs, config, written, roster);
        return roster;
    }

    private string? Locate(string cwd)
    {
        try
        {
            var dir = new DirectoryInfo(cwd);
            for (var i = 0; dir is not null && i <= MaxLevels; i++, dir = dir.Parent)
            {
                var config = Path.Combine(dir.FullName, "harness", "harness.config.json");
                if (File.Exists(config)) return config;
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return null;   // the repository's root: a harness above it belongs to someone else
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
