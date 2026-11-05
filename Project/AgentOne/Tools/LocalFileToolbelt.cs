using System.IO.Enumeration;
using System.Text;
using AgentOne.Agent;

namespace AgentOne.Tools;

/// <summary>
/// One directory tree: read anywhere in it, write anywhere in it, and nothing
/// outside it — with one exception. A folder the person names by its absolute
/// path can be <em>granted</em> for reading for the rest of the session; it is
/// never writable, whatever is asked. The root is the only place a file is
/// ever created or changed.
///
/// The search also covers what Claude Code would load here
/// (<see cref="ClaudeSkillScope"/>): the project's <c>.claude</c> definitions,
/// the user's skills and the enabled plugins' install folders. Those are
/// readable, and a search of the whole workspace walks them — and the
/// workspace paths they name — before everything else.
///
/// Every path the model supplies is resolved and rejected if it lands outside
/// — symlinks included, because the containment check is made on the fully
/// resolved real path.
/// </summary>
public sealed class LocalFileToolbelt : IToolbelt
{
    public const int MaxWriteChars = 512 * 1024;
    public const int MaxListedSkills = 30;

    private readonly Lazy<ClaudeSkillScope> _skills;

    /// <param name="root">The workspace root.</param>
    /// <param name="skills">The Claude skills to search; null detects them from disk on first use.</param>
    public LocalFileToolbelt(string root, ClaudeSkillScope? skills = null)
    {
        Root = Path.GetFullPath(root);
        _skills = new Lazy<ClaudeSkillScope>(() => skills ?? ClaudeSkillScope.Detect(Root));
    }

    /// <summary>The Claude skills, commands, agents and plugins found for this workspace.</summary>
    public ClaudeSkillScope Skills => _skills.Value;

    private readonly List<string> _readGrants = [];

    /// <summary>Folders outside the root this belt may read, in the order they were granted.</summary>
    public IReadOnlyList<string> ReadGrants => _readGrants;

    /// <summary>Allows reading under <paramref name="directory"/> for the life of this belt. Reading only.</summary>
    public void GrantRead(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_readGrants.Contains(full, GrantComparer)) _readGrants.Add(full);
    }

    public void ClearGrants() => _readGrants.Clear();

    private static StringComparer GrantComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public const int MaxReadBytes = 64 * 1024;
    public const int MaxListEntries = 300;
    public const int MaxFindResults = 200;
    public const int MaxGrepHits = 100;
    public const int MaxGrepLineChars = 200;
    public const int MaxGrepFileBytes = 2 * 1024 * 1024;

    public string Root { get; }

    public string Scope => Root;

    public Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        var result = call.Tool.ToLowerInvariant() switch
        {
            "list_files" => ListFiles(call.Arg("path", ".")),
            "read_file" => ReadFile(call.Arg("path")),
            "find_files" => FindFiles(call.Arg("pattern"), call.Arg("path", ".")),
            "grep" => Grep(call.Arg("text"), call.Arg("path", "."), call.Arg("glob", "*")),
            "write_file" => WriteFile(call.Arg("path"), call.Arg("content")),
            _ => ToolResult.Failure(
                $"unknown tool '{call.Tool}'. Available: {string.Join(", ", ToolCatalog.All.Select(t => t.Name))}")
        };
        return Task.FromResult(result);
    }

    private ToolResult ListFiles(string relative)
    {
        if (!TryResolve(relative, out var full, out var error)) return ToolResult.Failure(error);
        if (!Directory.Exists(full)) return ToolResult.Failure($"not a directory: {Display(full)}");

        var sb = new StringBuilder();
        sb.Append(Display(full)).Append(":\n");

        int count = 0;
        foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (IsExcluded(name)) continue;
            if (++count > MaxListEntries) break;
            sb.Append("  ").Append(name).Append("/\n");
        }

        foreach (var file in Directory.EnumerateFiles(full).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (++count > MaxListEntries) break;
            var info = new FileInfo(file);
            sb.Append("  ").Append(info.Name).Append(" (").Append(info.Length).Append(" B)\n");
        }

        if (count == 0) sb.Append("  (empty)\n");
        else if (count > MaxListEntries) sb.Append($"  ... truncated at {MaxListEntries} entries\n");

        if (full.Equals(Root, PathComparison)) AppendSkills(sb);

        return ToolResult.Success(sb.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// The whole file, every time. A partial edit verb would need the model to
    /// quote the exact old text, which small models get wrong more often than
    /// they get right; rewriting the file is dumber and works.
    /// </summary>
    private ToolResult WriteFile(string relative, string content)
    {
        if (relative.Length == 0) return ToolResult.Failure("write_file needs a 'path' argument");
        if (content.Length > MaxWriteChars) return ToolResult.Failure($"write_file: content is {content.Length} chars; the limit is {MaxWriteChars}");
        if (!TryResolve(relative, out var full, out var error, forWrite: true)) return ToolResult.Failure(error);
        if (Directory.Exists(full)) return ToolResult.Failure($"write_file: {Display(full)} is a directory");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var existed = File.Exists(full);
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var lines = content.Length == 0 ? 0 : content.Split('\n').Length;
            return ToolResult.Success($"{(existed ? "overwrote" : "created")} {Display(full)} ({content.Length} chars, {lines} lines)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure($"write_file: {ex.Message}");
        }
    }

    private ToolResult ReadFile(string relative)
    {
        if (relative.Length == 0) return ToolResult.Failure("read_file needs a 'path' argument");
        if (!TryResolve(relative, out var full, out var error)) return ToolResult.Failure(error);
        if (!File.Exists(full)) return ToolResult.Failure($"no such file: {Display(full)}");

        var info = new FileInfo(full);
        using var stream = File.OpenRead(full);
        var buffer = new byte[Math.Min(info.Length, MaxReadBytes)];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (info.Length > MaxReadBytes)
            text += $"\n... truncated ({info.Length} bytes total, {MaxReadBytes} shown)";

        return ToolResult.Success(text);
    }

    private ToolResult FindFiles(string pattern, string relative)
    {
        if (pattern.Length == 0) return ToolResult.Failure("find_files needs a 'pattern' argument, e.g. *.cs");
        if (!TryResolve(relative, out var full, out var error)) return ToolResult.Failure(error);
        if (!Directory.Exists(full)) return ToolResult.Failure($"not a directory: {Display(full)}");

        var matches = SearchFiles(full)
            .Where(f => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f), ignoreCase: true))
            .Take(MaxFindResults + 1)
            .ToList();

        if (matches.Count == 0)
            return ToolResult.Success($"no files matching '{pattern}' under {Display(full)}{SkillsNote(full)}");

        var sb = new StringBuilder();
        sb.Append(Math.Min(matches.Count, MaxFindResults)).Append(" file(s) matching '")
          .Append(pattern).Append("' under ").Append(Display(full)).Append(":\n");

        foreach (var file in matches.Take(MaxFindResults))
            sb.Append("  ").Append(Display(file)).Append('\n');

        if (matches.Count > MaxFindResults) sb.Append($"  … truncated at {MaxFindResults}\n");

        return ToolResult.Success(sb.ToString().TrimEnd('\n') + SkillsNote(full));
    }

    /// <summary>
    /// Plain case-insensitive substring search, deliberately not a regex: the
    /// pattern comes from a model, and a regex from an untrusted source is a way
    /// to hang the process on backtracking rather than a feature.
    /// </summary>
    private ToolResult Grep(string needle, string relative, string glob)
    {
        if (needle.Length == 0) return ToolResult.Failure("grep needs a 'text' argument");
        if (!TryResolve(relative, out var full, out var error)) return ToolResult.Failure(error);

        var files = Directory.Exists(full)
            ? SearchFiles(full).Where(f => FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(f), ignoreCase: true))
            : File.Exists(full) ? [full] : Enumerable.Empty<string>();

        var sb = new StringBuilder();
        int hits = 0, scanned = 0;

        foreach (var file in files)
        {
            if (hits >= MaxGrepHits) break;
            scanned++;

            foreach (var (line, number) in ReadLinesSafely(file))
            {
                if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;

                var trimmed = line.Trim();
                if (trimmed.Length > MaxGrepLineChars) trimmed = trimmed[..MaxGrepLineChars] + "…";

                sb.Append(Display(file)).Append(':').Append(number).Append(": ").Append(trimmed).Append('\n');

                if (++hits >= MaxGrepHits) break;
            }
        }

        if (hits == 0)
            return ToolResult.Success($"'{needle}' not found in {scanned} file(s) under {Display(full)}{SkillsNote(full)}");

        var header = hits >= MaxGrepHits
            ? $"first {hits} matches for '{needle}' (more may exist):\n"
            : $"{hits} match(es) for '{needle}':\n";

        return ToolResult.Success(header + sb.ToString().TrimEnd('\n') + SkillsNote(full));
    }

    /// <summary>
    /// The files a search of <paramref name="full"/> looks at. A search of the
    /// whole workspace walks the Claude skill folders first, then the workspace
    /// paths those skills name, then everything else — each file once — so under
    /// a hit cap the guidance a skill relies on is not crowded out by logs.
    /// A search of a subfolder is that subfolder only.
    /// </summary>
    private IEnumerable<string> SearchFiles(string full)
    {
        if (!full.Equals(Root, PathComparison) || Skills.IsEmpty)
        {
            foreach (var file in Walk(full)) yield return file;
            yield break;
        }

        var seen = new HashSet<string>(GrantComparer);
        foreach (var file in SkillsFirst())
            if (seen.Add(file)) yield return file;
    }

    private IEnumerable<string> SkillsFirst()
    {
        foreach (var source in Skills.Sources)
            foreach (var file in Walk(source.Directory)) yield return file;

        foreach (var linked in Skills.LinkedPaths)
        {
            if (File.Exists(linked)) yield return linked;
            else foreach (var file in Walk(linked)) yield return file;
        }

        foreach (var file in Walk(Root)) yield return file;
    }

    /// <summary>Footer of a whole-workspace search saying the skills were included, so a miss is not read as "there are none".</summary>
    private string SkillsNote(string full) =>
        full.Equals(Root, PathComparison) && !Skills.IsEmpty
            ? $"\n(searched Claude skills first: {Skills.Summary()} — list_files . names them)"
            : "";

    /// <summary>The Claude skills found for this workspace, under the root's listing.</summary>
    private void AppendSkills(StringBuilder sb)
    {
        if (Skills.IsEmpty) return;

        if (Skills.Skills.Count > 0)
        {
            sb.Append("\nClaude skills for this workspace (find_files/grep search them; read_file opens them):\n");
            foreach (var skill in Skills.Skills.Take(MaxListedSkills))
            {
                sb.Append("  ").Append(skill.Name);
                if (skill.Kind != SkillKind.Skill) sb.Append(" [").Append(skill.Kind.ToString().ToLowerInvariant()).Append(']');
                if (skill.Origin.Kind == SkillOriginKind.Plugin) sb.Append(" [plugin ").Append(skill.Origin.Label).Append(']');
                else if (skill.Origin.Kind == SkillOriginKind.User) sb.Append(" [user]");
                if (skill.Description.Length > 0) sb.Append(" — ").Append(skill.Description);
                sb.Append("\n    ").Append(Display(skill.Path)).Append('\n');
            }
            if (Skills.Skills.Count > MaxListedSkills)
                sb.Append($"  … {Skills.Skills.Count - MaxListedSkills} more\n");
        }

        if (Skills.LinkedPaths.Count > 0)
            sb.Append("Workspace paths the skills point to: ")
              .Append(string.Join(", ", Skills.LinkedPaths.Select(Display)))
              .Append('\n');
    }

    /// <summary>Every file under a directory, minus the folders nobody wants searched.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>([root]);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] entries;
            try { entries = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var file in entries.OrderBy(f => f, StringComparer.Ordinal))
                yield return file;

            try
            {
                foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                    if (!IsExcluded(Path.GetFileName(sub)) && !IsClaudeNoise(dir, sub))
                        stack.Push(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* skip */ }
        }
    }

    /// <summary>Lines of a text file, or nothing at all — a binary blob is not an error worth reporting.</summary>
    private static IEnumerable<(string Line, int Number)> ReadLinesSafely(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxGrepFileBytes) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        for (int i = 0; i < lines.Length; i++)
        {
            // A NUL byte means binary; searching it produces noise, not matches.
            if (lines[i].Contains('\0')) yield break;
            yield return (lines[i], i + 1);
        }
    }

    /// <summary>
    /// Directories that are noise for an agent and expensive to walk. A
    /// dot-folder is noise except <c>.claude</c>: that is where a project keeps
    /// its skills, commands and agents — skipping it hid every one of them.
    /// </summary>
    private static bool IsExcluded(string name) =>
        (name.StartsWith('.') && !name.Equals(".claude", StringComparison.OrdinalIgnoreCase)) ||
        name is "bin" or "obj" or "node_modules" or "dist" or "build" or "__pycache__";

    /// <summary>What Claude Code keeps under .claude that is not a definition: worktree checkouts, transcripts, history.</summary>
    private static bool IsClaudeNoise(string parent, string sub) =>
        Path.GetFileName(parent).Equals(".claude", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(sub) is "worktrees" or "projects" or "file-history" or "shell-snapshots" or "todos" or "logs";

    private bool TryResolve(string relative, out string full, out string error, bool forWrite = false)
    {
        full = "";
        error = "";

        if (relative.Length == 0) relative = ".";

        try
        {
            var combined = Path.IsPathRooted(relative)
                ? Path.GetFullPath(relative)
                : Path.GetFullPath(Path.Combine(Root, relative));

            // Resolve symlinks before the containment check, or a link inside the
            // root would be a way straight out of it.
            var real = ResolveReal(combined);

            if (!IsInsideRoot(real))
            {
                // Reading a granted folder is fine; writing there never is. The
                // message says which, so the model does not try a workaround.
                // A detected skill's own folder (a plugin install, the user's
                // skills) reads the same way: a skill's references sit beside it.
                var readable = _readGrants.Any(g => IsInside(real, g)) || Skills.ReadRoots.Any(g => IsInside(real, g));
                if (!forWrite && readable)
                {
                    full = real;
                    return true;
                }

                error = forWrite && readable
                    ? $"{relative} is outside the workspace root and read-only: files are only ever written under {Root}"
                    : $"path escapes the workspace root ({Root}): {relative}";
                return false;
            }

            full = real;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"invalid path '{relative}': {ex.Message}";
            return false;
        }
    }

    private static string ResolveReal(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private bool IsInsideRoot(string candidate) => IsInside(candidate, Root);

    private static bool IsInside(string candidate, string directory)
    {
        var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate, root, PathComparison)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private string Display(string full)
    {
        if (full.Equals(Root, PathComparison)) return ".";
        return full.StartsWith(Root, PathComparison)
            ? full[(Root.Length + 1)..].Replace('\\', '/')
            : full;
    }
}
