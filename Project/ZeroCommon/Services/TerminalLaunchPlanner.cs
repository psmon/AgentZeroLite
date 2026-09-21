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
    /// <summary>
    /// The argument string exactly as the definition (and the SSH composer) wrote it.
    /// <see cref="Args"/> is the POSIX reading of the same field; re-joining it for
    /// Windows would re-quote every argument, and <see cref="CommandLineSplitter.Join"/>
    /// doubles backslashes — which a shell like PowerShell does not undo, so
    /// <c>-i "C:\keys\id.pem"</c> would reach ssh as <c>C:\\keys\\id.pem</c>. Windows
    /// hands ConPTY one command line anyway, so it gets the original text.
    /// </summary>
    public string? RawArguments { get; init; }

    /// <summary>The Windows form — ConPTY takes one command line, not argv.</summary>
    public string CommandLine
    {
        get
        {
            var tail = RawArguments ?? (Args.Count == 0 ? null : CommandLineSplitter.Join(Args));
            return string.IsNullOrWhiteSpace(tail) ? Quote(App) : Quote(App) + " " + tail.Trim();
        }
    }

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

        // A remote definition is "this shell, then an ssh into the box" — the shell's
        // stored arguments are only half of it (`-NoExit -Command`, `/K`). Composing the
        // other half used to live in the WPF window that launches tabs, so a host that
        // planned from the definition alone started the bare shell with a dangling
        // `-Command` and got the shell's usage banner instead of a session.
        var ssh = new SshLaunchSettings(
            IsRemote: definition.IsRemote,
            Host: definition.SshHost,
            User: definition.SshUser,
            AuthMode: SshCommandBuilder.ParseAuthMethod(definition.SshAuthMethod),
            KeyPath: definition.SshKeyPath);
        if (definition.IsRemote && SshCommandBuilder.BuildSshCommand(ssh).Length == 0)
        {
            // Without both halves the composer returns the shell's own arguments, which
            // is exactly the broken launch above. Say what is missing instead.
            error = $"'{definition.Name}' is remote but has no SSH host/user to connect to";
            return null;
        }

        var arguments = SshCommandBuilder.ComposeArguments(exe, definition.Arguments, ssh);
        arguments = ReducedMotionArguments.Append(arguments, definition.ExePath, definition.ReducedMotion);
        var args = CommandLineSplitter.Split(arguments);

        IDictionary<string, string> env = windows
            ? TerminalEnvironment.Build(sourceEnvironment)
            : TerminalEnvironment.BuildPosix(sourceEnvironment);
        if (!string.IsNullOrWhiteSpace(appDir))
            TerminalEnvironment.PrependPath(env, appDir, windows ? ';' : ':');

        return new TerminalLaunchSpec(exe, args, workDir, new Dictionary<string, string>(env, env is SortedDictionary<string, string> ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal), definition.Name)
        {
            RawArguments = arguments,
        };
    }
}
