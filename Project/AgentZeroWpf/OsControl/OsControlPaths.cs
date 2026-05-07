using System.IO;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// Central path resolution for OS-control artefacts (screenshots, audit logs,
/// E2E captures). All output funnels under <c>tmp/os-cli/</c> next to the
/// repository root so a fresh clone can `git clean -fdx tmp/` without
/// destroying anything users care about. CLI and toolbelt callers share this
/// — keeps the on-disk layout consistent regardless of caller.
/// </summary>
internal static class OsControlPaths
{
    /// <summary>
    /// Subfolder under the repository root (or AppContext.BaseDirectory as
    /// fallback when running from an installed location). Mission M0014
    /// pinned this name; downstream tooling (harness-view, E2E scripts) keys
    /// off it directly.
    /// </summary>
    public const string TmpRoot = "tmp/os-cli";

    public static string Resolve(params string[] segments)
    {
        var basePath = ResolveBase();
        var combined = Path.Combine(basePath, TmpRoot);
        foreach (var seg in segments)
            combined = Path.Combine(combined, seg);
        return combined;
    }

    public static string ResolveAndEnsureDirectory(params string[] segments)
    {
        var path = Resolve(segments);
        var dir = Path.HasExtension(path) ? Path.GetDirectoryName(path)! : path;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return path;
    }

    public static string ScreenshotDir() => ResolveAndEnsureDirectory("screenshots");
    public static string AuditDir() => ResolveAndEnsureDirectory("audit");
    public static string E2eDir() => ResolveAndEnsureDirectory("e2e");

    /// <summary>
    /// Walk up from <see cref="AppContext.BaseDirectory"/> looking for the
    /// repo root marker (a <c>.git</c> directory or <c>AgentZeroLite.sln</c>).
    /// When running from an installed location (no repo around), fall back to
    /// <c>%LOCALAPPDATA%\AgentZeroLite</c> so artefacts still land somewhere
    /// predictable per-user.
    /// </summary>
    private static string ResolveBase()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "AgentZeroLite.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "AgentZeroLite");
    }
}
