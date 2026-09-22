namespace AgentOne.Services;

/// <summary>
/// Every file agent-one owns lives under one root, never in the working
/// directory — same rule as CodeScan's ~/.codescan. AGENT_ONE_HOME relocates
/// the whole tree (tests point it at a temp dir; CI images at a cache mount).
/// </summary>
public static class AppPaths
{
    public const string HomeEnvVar = "AGENT_ONE_HOME";

    public static string BaseDir =>
        Environment.GetEnvironmentVariable(HomeEnvVar) is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agent-one");

    public static string ConfigPath => Path.Combine(BaseDir, "config.json");

    public static string SessionDir => Path.Combine(BaseDir, "sessions");

    /// <summary>Per-workspace memory and sessions live here, one folder per root.</summary>
    public static string WorkspacesDir => Path.Combine(BaseDir, "workspaces");

    /// <summary>
    /// The folder for one workspace root: its last path segment for a person
    /// reading the directory, plus a short hash of the full path so two
    /// folders called "api" do not share a memory.
    /// </summary>
    public static string WorkspaceDir(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();

        var name = Path.GetFileName(full);
        if (name.Length == 0) name = "root";
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        if (slug.Length > 32) slug = slug[..32];

        return Path.Combine(WorkspacesDir, $"{slug}-{hash}");
    }

    public static string LogDir => Path.Combine(BaseDir, "logs");

    public static string EnsureBaseDir()
    {
        Directory.CreateDirectory(BaseDir);
        return BaseDir;
    }

    public static string EnsureSessionDir()
    {
        Directory.CreateDirectory(SessionDir);
        return SessionDir;
    }

    public static string EnsureLogDir()
    {
        Directory.CreateDirectory(LogDir);
        return LogDir;
    }
}
