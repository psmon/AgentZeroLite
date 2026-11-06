namespace AgentOne.Tools.Cli;

/// <summary>
/// Finds a command on PATH the way a shell would, without starting one.
///
/// On Windows a bare name resolves through PATHEXT, and npm installs its
/// tools as <c>.cmd</c> shims — so <c>playwright-cli</c> is
/// <c>%APPDATA%\npm\playwright-cli.cmd</c>, which <see cref="ShellInfo.OnPath"/>
/// (looking for <c>.exe</c> only) does not see. Elsewhere the file itself is
/// the command.
/// </summary>
public static class ExecutableLocator
{
    /// <summary>The full path of <paramref name="name"/> on PATH, or null.</summary>
    public static string? Find(string name) => Find(name, Environment.GetEnvironmentVariable("PATH") ?? "");

    internal static string? Find(string name, string path)
    {
        if (name.Length == 0) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), name); }
            catch (ArgumentException) { continue; }

            if (OperatingSystem.IsWindows())
            {
                // A bare file with no extension is not runnable on Windows; npm
                // leaves one beside the .cmd (a sh script for Git Bash).
                foreach (var ext in extensions)
                    if (File.Exists(candidate + ext.ToLowerInvariant())) return candidate + ext.ToLowerInvariant();
            }
            else if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
