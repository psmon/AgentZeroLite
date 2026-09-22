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
