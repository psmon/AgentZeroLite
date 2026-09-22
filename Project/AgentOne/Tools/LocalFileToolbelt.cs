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
/// Every path the model supplies is resolved and rejected if it lands outside
/// — symlinks included, because the containment check is made on the fully
/// resolved real path.
/// </summary>
public sealed class LocalFileToolbelt(string root) : IToolbelt
{
    public const int MaxWriteChars = 512 * 1024;

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

    public string Root { get; } = Path.GetFullPath(root);

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

        var matches = Walk(full)
            .Where(f => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f), ignoreCase: true))
            .Take(MaxFindResults + 1)
            .ToList();

        if (matches.Count == 0)
            return ToolResult.Success($"no files matching '{pattern}' under {Display(full)}");

        var sb = new StringBuilder();
        sb.Append(Math.Min(matches.Count, MaxFindResults)).Append(" file(s) matching '")
          .Append(pattern).Append("' under ").Append(Display(full)).Append(":\n");

        foreach (var file in matches.Take(MaxFindResults))
            sb.Append("  ").Append(Display(file)).Append('\n');

        if (matches.Count > MaxFindResults) sb.Append($"  … truncated at {MaxFindResults}\n");

        return ToolResult.Success(sb.ToString().TrimEnd('\n'));
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
            ? Walk(full).Where(f => FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(f), ignoreCase: true))
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
            return ToolResult.Success($"'{needle}' not found in {scanned} file(s) under {Display(full)}");

        var header = hits >= MaxGrepHits
            ? $"first {hits} matches for '{needle}' (more may exist):\n"
            : $"{hits} match(es) for '{needle}':\n";

        return ToolResult.Success(header + sb.ToString().TrimEnd('\n'));
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
                    if (!IsExcluded(Path.GetFileName(sub)))
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

    /// <summary>Directories that are noise for an agent and expensive to walk.</summary>
    private static bool IsExcluded(string name) =>
        name.StartsWith('.') ||
        name is "bin" or "obj" or "node_modules" or "dist" or "build" or "__pycache__";

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
                if (!forWrite && _readGrants.Any(g => IsInside(real, g)))
                {
                    full = real;
                    return true;
                }

                error = forWrite && _readGrants.Any(g => IsInside(real, g))
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
