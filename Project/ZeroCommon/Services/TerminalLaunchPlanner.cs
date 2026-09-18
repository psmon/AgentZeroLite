using System.Collections;
using Agent.Common.Data.Entities;
using Agent.Common.Module;

namespace Agent.Common.Services;

/// <summary>What a PTY host needs to start one terminal tab (M0033).</summary>
public sealed record TerminalLaunchSpec(
    string App,
    IReadOnlyList<string> Args,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    string DisplayName)
{
    /// <summary>The Windows form — ConPTY takes one command line, not argv.</summary>
    public string CommandLine => Args.Count == 0
        ? Quote(App)
        : Quote(App) + " " + CommandLineSplitter.Join(Args);

    private static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;
}

/// <summary>
/// Turns a <see cref="CliDefinition"/> into a <see cref="TerminalLaunchSpec"/> for this OS.
/// The WPF host wraps every tab in <c>cmd /c "set PATH=…&amp;&amp;pushd …&amp;&amp;…"</c>; with a
/// PTY host that accepts a working directory and an environment, neither wrapper is
/// needed, so the app-folder PATH entry moves into the environment and the definition's
/// <c>ExePath</c> is launched as-is.
/// </summary>
public static class TerminalLaunchPlanner
{
    /// <summary>
    /// Definitions written for the other OS are hidden rather than shown and failed:
    /// a <c>.exe</c> makes no sense on macOS, an absolute POSIX path none on Windows.
    /// </summary>
    public static bool IsAvailableOnThisOs(CliDefinition definition, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var exe = (definition.ExePath ?? "").Trim();
        if (exe.Length == 0) return false;
        if (windows) return !exe.StartsWith('/');
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (definition.IsRemote) return false;   // SshCommandBuilder is Windows-shell-only in phase 1
        return true;
    }

    public static TerminalLaunchSpec? Plan(CliDefinition definition, string workDir, string? appDir,
        out string? error, bool? isWindows = null, IDictionary? sourceEnvironment = null)
    {
        error = null;
        var windows = isWindows ?? OperatingSystem.IsWindows();
        if (!IsAvailableOnThisOs(definition, windows))
        {
            error = definition.IsRemote && !windows
                ? "remote (SSH) definitions are not supported on this platform yet"
                : $"'{definition.ExePath}' is not available on this platform";
            return null;
        }

        var exe = definition.ExePath.Trim();
        var arguments = ReducedMotionArguments.Append(definition.Arguments, definition.ExePath, definition.ReducedMotion);
        var args = CommandLineSplitter.Split(arguments);

        IDictionary<string, string> env = windows
            ? TerminalEnvironment.Build(sourceEnvironment)
            : TerminalEnvironment.BuildPosix(sourceEnvironment);
        if (!string.IsNullOrWhiteSpace(appDir))
            TerminalEnvironment.PrependPath(env, appDir, windows ? ';' : ':');

        return new TerminalLaunchSpec(exe, args, workDir, new Dictionary<string, string>(env, env is SortedDictionary<string, string> ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal), definition.Name);
    }
}
