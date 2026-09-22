using System.Text;
using AgentOne.Agent;

namespace AgentOne.Tools;

/// <summary>
/// Read-only view of one directory tree. v0 deliberately ships no write and no
/// shell verb: the sandbox stays easy to reason about while the loop, the
/// envelope and the packaging path are being proven, and widening it later is
/// additive.
///
/// Every path the model supplies is resolved against <see cref="Root"/> and
/// rejected if it lands outside — symlinks included, because the containment
/// check is made on the fully resolved real path.
/// </summary>
public sealed class LocalFileToolbelt(string root) : IToolbelt
{
    public const int MaxReadBytes = 64 * 1024;
    public const int MaxListEntries = 300;

    public string Root { get; } = Path.GetFullPath(root);

    public string Scope => Root;

    public Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        var result = call.Tool.ToLowerInvariant() switch
        {
            "list_files" => ListFiles(call.Arg("path", ".")),
            "read_file" => ReadFile(call.Arg("path")),
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

    /// <summary>Directories that are noise for an agent and expensive to walk.</summary>
    private static bool IsExcluded(string name) =>
        name.StartsWith('.') ||
        name is "bin" or "obj" or "node_modules" or "dist" or "build" or "__pycache__";

    private bool TryResolve(string relative, out string full, out string error)
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
                error = $"path escapes the workspace root ({Root}): {relative}";
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

    private bool IsInsideRoot(string candidate)
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
