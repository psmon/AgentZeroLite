using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOne.Tools;

/// <summary>
/// What Claude Code would load for this workspace, found on disk so the file
/// tools can search it: the project's own <c>.claude/skills|commands|agents</c>,
/// the user's skills, and every plugin that is <em>enabled</em> for the
/// workspace — resolved through <c>settings.json</c>'s <c>enabledPlugins</c> and
/// <c>plugins/installed_plugins.json</c> to the folder it was installed into.
///
/// Measured (2026-11-05, a Claude-skill-based repository): asked five times to
/// "find it in the Claude skills or the guidance", the model never saw a skill.
/// The walk skipped every dot-folder, so <c>.claude/skills</c> was invisible to
/// list_files, find_files and grep, and plugin skills live outside the root
/// altogether. The model found one script by luck and rebuilt the method from
/// it. This type is the detection; <see cref="LocalFileToolbelt"/> widens the
/// search with it, so nothing depends on the model being told where to look.
///
/// A skill usually points at the repository's own knowledge
/// (<c>harness/knowledge/tools/x.md</c>, <c>tools/gen.js</c>) — those
/// workspace paths are collected as <see cref="LinkedPaths"/> and searched
/// first, so a hit in the guidance a skill relies on comes before a hit in a log.
/// </summary>
public sealed partial class ClaudeSkillScope
{
    public const int MaxLinkedPaths = 40;
    public const int MaxDescriptionChars = 110;

    /// <summary>Comma/semicolon-separated Claude config folders, replacing the default lookup (tests, unusual setups).</summary>
    public const string HomesEnvVar = "AGENT_ONE_CLAUDE_HOMES";

    public static ClaudeSkillScope Empty { get; } = new([], [], [], []);

    private ClaudeSkillScope(
        IReadOnlyList<SkillEntry> skills,
        IReadOnlyList<SkillSource> sources,
        IReadOnlyList<string> linkedPaths,
        IReadOnlyList<string> readRoots)
    {
        Skills = skills;
        Sources = sources;
        LinkedPaths = linkedPaths;
        ReadRoots = readRoots;
    }

    /// <summary>Every skill, command and agent definition found, project first.</summary>
    public IReadOnlyList<SkillEntry> Skills { get; }

    /// <summary>Folders holding skill definitions: walked by find_files/grep before the rest of the workspace.</summary>
    public IReadOnlyList<SkillSource> Sources { get; }

    /// <summary>Full paths inside the workspace that a skill names (files or folders) — searched right after the skills.</summary>
    public IReadOnlyList<string> LinkedPaths { get; }

    /// <summary>Folders outside the workspace that read_file may open: a plugin's install folder, the user's skills folder.</summary>
    public IReadOnlyList<string> ReadRoots { get; }

    public bool IsEmpty => Skills.Count == 0 && LinkedPaths.Count == 0;

    /// <summary>One line for a status block or a tool footer: "2 project, 4 plugin (harness-kakashi)".</summary>
    public string Summary()
    {
        if (Skills.Count == 0) return "no Claude skills found";
        var parts = Skills.GroupBy(s => s.Origin.Kind)
            .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}" + (g.Key == SkillOriginKind.Plugin
                ? " (" + string.Join(", ", g.Select(s => s.Origin.Label).Distinct()) + ")"
                : ""));
        return string.Join(", ", parts) + $" skill file(s){(LinkedPaths.Count > 0 ? $", {LinkedPaths.Count} linked path(s)" : "")}";
    }

    // ------------------------------------------------------------ detection

    /// <summary>
    /// Looks under <paramref name="root"/> and the Claude config folders. Never
    /// throws: a malformed settings file or an unreadable folder only means
    /// fewer skills.
    /// </summary>
    public static ClaudeSkillScope Detect(string root, IReadOnlyList<string>? claudeHomes = null)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        claudeHomes ??= DefaultHomes();

        var skills = new List<SkillEntry>();
        var sources = new List<SkillSource>();
        var readRoots = new List<string>();

        // 1. The project's own .claude folder.
        var projectClaude = Path.Combine(root, ".claude");
        var projectOrigin = new SkillOrigin(SkillOriginKind.Project, ".claude", projectClaude);
        AddDefinitionFolders(projectClaude, projectOrigin, skills, sources);

        // 2. The user's skills and the plugins enabled for this workspace, per config folder.
        var projectEnabled = ReadEnabledPlugins(Path.Combine(projectClaude, "settings.json"));
        foreach (var (id, on) in ReadEnabledPlugins(Path.Combine(projectClaude, "settings.local.json")))
            projectEnabled[id] = on;

        var seenInstalls = new HashSet<string>(PathComparer);
        // One copy per plugin: the same plugin installed under ~/.claude and a
        // ~/.claude-<profile> is one set of skills, listed from the first home.
        var seenPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in claudeHomes)
        {
            if (!Directory.Exists(home)) continue;

            var userSkills = Path.Combine(home, "skills");
            var userOrigin = new SkillOrigin(SkillOriginKind.User, Path.GetFileName(home.TrimEnd('\\', '/')), userSkills);
            if (seenInstalls.Add(userSkills) && AddSkillFolder(userSkills, userOrigin, skills, sources))
                readRoots.Add(userSkills);

            var enabled = ReadEnabledPlugins(Path.Combine(home, "settings.json"));
            foreach (var (id, on) in projectEnabled) enabled[id] = on;   // project settings win

            var installs = ReadInstalls(Path.Combine(home, "plugins", "installed_plugins.json"));
            foreach (var (id, on) in enabled)
            {
                if (!on || seenPlugins.Contains(id) || !installs.TryGetValue(id, out var entries)) continue;
                var installPath = PickInstall(entries, root);
                if (installPath is null || !Directory.Exists(installPath) || !seenInstalls.Add(installPath)) continue;
                seenPlugins.Add(id);

                var origin = new SkillOrigin(SkillOriginKind.Plugin, id.Split('@')[0], installPath);
                if (AddDefinitionFolders(installPath, origin, skills, sources))
                    readRoots.Add(installPath);
            }
        }

        var linked = LinkedPathsOf(skills, root);
        return new ClaudeSkillScope(skills, sources, linked, readRoots);
    }

    /// <summary>
    /// <c>$AGENT_ONE_CLAUDE_HOMES</c> when set; otherwise <c>$CLAUDE_CONFIG_DIR</c>,
    /// <c>~/.claude</c>, and every <c>~/.claude-*</c> profile folder that has a
    /// settings.json (a wrapper that runs Claude under another profile keeps
    /// its plugins there).
    /// </summary>
    public static IReadOnlyList<string> DefaultHomes()
    {
        var overridden = Environment.GetEnvironmentVariable(HomesEnvVar);
        if (overridden is not null)
            return overridden.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var homes = new List<string>();
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) homes.Add(Path.GetFullPath(configDir));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length == 0) return homes;

        homes.Add(Path.Combine(profile, ".claude"));
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(profile, ".claude-*").OrderBy(d => d, StringComparer.Ordinal))
                if (File.Exists(Path.Combine(dir, "settings.json"))) homes.Add(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* none */ }

        return homes.Distinct(PathComparer).ToList();
    }

    /// <summary>skills/, commands/ and agents/ under <paramref name="baseDir"/>. True when any held a definition.</summary>
    private static bool AddDefinitionFolders(string baseDir, SkillOrigin origin, List<SkillEntry> skills, List<SkillSource> sources)
    {
        if (!Directory.Exists(baseDir)) return false;
        var any = AddSkillFolder(Path.Combine(baseDir, "skills"), origin, skills, sources);
        any |= AddMarkdownFolder(Path.Combine(baseDir, "commands"), origin, SkillKind.Command, skills, sources);
        any |= AddMarkdownFolder(Path.Combine(baseDir, "agents"), origin, SkillKind.Agent, skills, sources);
        return any;
    }

    private static bool AddSkillFolder(string dir, SkillOrigin origin, List<SkillEntry> skills, List<SkillSource> sources)
    {
        var found = false;
        foreach (var sub in SafeDirectories(dir))
        {
            var file = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(file)) continue;
            skills.Add(Entry(file, Path.GetFileName(sub), SkillKind.Skill, origin));
            found = true;
        }
        if (found) sources.Add(new SkillSource(dir, origin));
        return found;
    }

    private static bool AddMarkdownFolder(string dir, SkillOrigin origin, SkillKind kind, List<SkillEntry> skills, List<SkillSource> sources)
    {
        if (!Directory.Exists(dir)) return false;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        if (files.Length == 0) return false;

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            skills.Add(Entry(file, Path.GetFileNameWithoutExtension(file), kind, origin));
        sources.Add(new SkillSource(dir, origin));
        return true;
    }

    private static SkillEntry Entry(string file, string fallbackName, SkillKind kind, SkillOrigin origin)
    {
        var (name, description) = ReadFrontMatter(file);
        return new SkillEntry(name.Length > 0 ? name : fallbackName, description, file, kind, origin);
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        try { return Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ------------------------------------------------------------ front matter

    /// <summary>
    /// <c>name</c> and <c>description</c> from a Markdown file's YAML front
    /// matter — the plain, quoted and folded (<c>&gt;-</c> / <c>|</c>) forms
    /// skills use. Not a YAML parser; anything it does not understand is "".
    /// </summary>
    public static (string Name, string Description) ReadFrontMatter(string file)
    {
        string[] lines;
        try { lines = File.ReadLines(file).Take(80).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ("", ""); }

        if (lines.Length == 0 || lines[0].Trim() != "---") return ("", "");

        string name = "", description = "";
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            if (line.StartsWith("name:", StringComparison.Ordinal)) name = Unquote(line[5..].Trim());
            else if (line.StartsWith("description:", StringComparison.Ordinal))
            {
                var value = line[12..].Trim();
                if (value is ">-" or ">" or "|" or "|-" or ">+" or "|+")
                {
                    var sb = new StringBuilder();
                    while (i + 1 < lines.Length && (lines[i + 1].StartsWith(' ') || lines[i + 1].StartsWith('\t') || lines[i + 1].Length == 0))
                    {
                        i++;
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(lines[i].Trim());
                    }
                    value = sb.ToString();
                }
                description = Unquote(value);
            }
        }

        description = Regex.Replace(description, @"\s+", " ").Trim();
        if (description.Length > MaxDescriptionChars) description = description[..MaxDescriptionChars].TrimEnd() + "…";
        return (name, description);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
            ? value[1..^1]
            : value;

    // ------------------------------------------------------------ plugins

    private static Dictionary<string, bool> ReadEnabledPlugins(string settingsFile)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(settingsFile)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("enabledPlugins", out var enabled) ||
                enabled.ValueKind != JsonValueKind.Object) return result;

            foreach (var p in enabled.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    result[p.Name] = p.Value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* none */ }
        return result;
    }

    private sealed record Install(string Scope, string ProjectPath, string InstallPath);

    private static Dictionary<string, List<Install>> ReadInstalls(string file)
    {
        var result = new Dictionary<string, List<Install>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(file)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var plugin in plugins.EnumerateObject())
            {
                if (plugin.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<Install>();
                foreach (var e in plugin.Value.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var install = Str(e, "installPath");
                    if (install.Length == 0) continue;
                    list.Add(new Install(Str(e, "scope"), Str(e, "projectPath"), install));
                }
                result[plugin.Name] = list;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* none */ }
        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// The install Claude Code would use here: one made for this project (or a
    /// folder above it) beats the user-wide one; an install for some other
    /// project is not this workspace's.
    /// </summary>
    private static string? PickInstall(List<Install> entries, string root)
    {
        Install? best = null;
        var bestDepth = -1;
        foreach (var e in entries)
        {
            if (e.Scope is "project" or "local" && e.ProjectPath.Length > 0)
            {
                string project;
                try { project = Path.GetFullPath(e.ProjectPath).TrimEnd('\\', '/'); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
                if (!PathGrants.IsUnder(root, project)) continue;
                if (project.Length > bestDepth) { best = e; bestDepth = project.Length; }
            }
            else if (e.Scope == "user" && best is null)
            {
                best = e;
            }
        }
        return best?.InstallPath;
    }

    // ------------------------------------------------------------ linked paths

    /// <summary>
    /// Workspace files and folders named in the skills' text that exist. Most
    /// specific first: files a project skill names, then its folders, then what
    /// plugin and user skills name — a plugin describing a layout ("harness/logs",
    /// "harness/agents") is less pointed than a project skill naming the one
    /// document to read. Record folders (logs, tmp, cache…) are left out:
    /// measured, a linked <c>harness/logs</c> filled the head of every search
    /// with old run logs ahead of the knowledge the skills point to.
    /// </summary>
    private static IReadOnlyList<string> LinkedPathsOf(List<SkillEntry> skills, string root)
    {
        var found = new List<(string Full, int Rank)>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var skill in skills)
        {
            var fromProject = skill.Origin.Kind == SkillOriginKind.Project;
            string text;
            try
            {
                var info = new FileInfo(skill.Path);
                if (info.Length > 256 * 1024) continue;
                text = File.ReadAllText(skill.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (Match m in RelativePath().Matches(text))
            {
                var token = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '`', '"', '\'', '/');
                if (token.Length == 0 || token.StartsWith(".claude", StringComparison.OrdinalIgnoreCase)) continue;
                string full;
                try { full = Path.GetFullPath(Path.Combine(root, token)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!PathGrants.IsUnder(full, root) || string.Equals(full, root, PathComparison)) continue;
                var isFile = File.Exists(full);
                if (!isFile && !Directory.Exists(full)) continue;
                // Any folder on the way counts: harness/logs/tamer is a record folder too.
                var segments = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.SkipLast(isFile ? 1 : 0).Any(IsRecordFolder)) continue;
                if (!seen.Add(full)) continue;
                found.Add((full, (fromProject ? 0 : 2) + (isFile ? 0 : 1)));
            }
        }

        // OrderBy is stable: inside a rank, the order the skills named them.
        return found.OrderBy(f => f.Rank).Select(f => f.Full).Take(MaxLinkedPaths).ToList();
    }

    private static bool IsRecordFolder(string name) =>
        name.ToLowerInvariant() is "logs" or "log" or "tmp" or "temp" or "cache" or "history" or "archive" or "out" or "output";

    // a/b, a/b/c.md, harness/knowledge/ — at least one slash, not part of a URL or an absolute path.
    [GeneratedRegex(@"(?<![\w.:/\\~-])[A-Za-z0-9_][\w.-]*(?:/[\w.-]+)+/?")]
    private static partial Regex RelativePath();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public enum SkillOriginKind { Project, User, Plugin }

public enum SkillKind { Skill, Command, Agent }

/// <summary>Where a definition came from: the project's .claude, a user config folder, or a plugin (Label = plugin name).</summary>
public sealed record SkillOrigin(SkillOriginKind Kind, string Label, string BaseDirectory);

public sealed record SkillEntry(string Name, string Description, string Path, SkillKind Kind, SkillOrigin Origin);

/// <summary>A folder of definitions the search walks before the rest of the workspace.</summary>
public sealed record SkillSource(string Directory, SkillOrigin Origin);
