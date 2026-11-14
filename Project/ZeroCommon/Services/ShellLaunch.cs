using System.Text.RegularExpressions;
using Agent.Common.Platform;

namespace Agent.Common.Services;

/// <summary>How a shell is told "run this, then stay open".</summary>
public enum ShellFamily
{
    /// <summary><c>cmd.exe</c>: <c>/K &lt;command&gt;</c>.</summary>
    Cmd,
    /// <summary><c>powershell.exe</c> / <c>pwsh</c>: <c>-NoExit -Command &lt;command&gt;</c>.</summary>
    PowerShell,
    /// <summary>bash, zsh, sh, dash, ksh, fish (Git Bash too): <c>-l -i -c "&lt;command&gt;; exec &lt;shell&gt; -l"</c>.</summary>
    Posix,
    /// <summary>Anything else — a program run directly; its arguments are its own.</summary>
    Other,
}

/// <summary>
/// The shell half of a terminal definition's arguments. A definition is a shell plus, usually,
/// one command to start in it (<c>claude</c>); the flags that make the shell run that command
/// and stay alive afterwards differ per shell and are not something a person should have to
/// remember. Settings shows only the command and this class writes — and reads back — the rest,
/// so the stored <see cref="Data.Entities.CliDefinition.Arguments"/> stay byte-identical to what
/// the seeding and the WPF host write.
/// </summary>
public static partial class ShellLaunch
{
    private static readonly string[] PosixNames = ["bash", "zsh", "sh", "dash", "ksh", "fish"];

    public static ShellFamily FamilyOf(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return ShellFamily.Other;
        var name = Path.GetFileName(exePath.Trim().Trim('"')).ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        return name switch
        {
            "cmd" => ShellFamily.Cmd,
            "powershell" or "pwsh" => ShellFamily.PowerShell,
            _ when PosixNames.Contains(name) => ShellFamily.Posix,
            _ => ShellFamily.Other,
        };
    }

    /// <summary>
    /// The arguments that start <paramref name="command"/> in the shell and keep the shell open.
    /// No command means the bare shell: nothing for cmd / PowerShell, a login shell (<c>-l</c>)
    /// for a POSIX one, as the seeded zsh / bash rows have it. Null for <see cref="ShellFamily.Other"/>:
    /// there is no wrapper to write.
    /// </summary>
    public static string? Compose(string? exePath, string? command)
    {
        var cmd = command?.Trim() ?? "";
        switch (FamilyOf(exePath))
        {
            case ShellFamily.Cmd:
                return cmd.Length == 0 ? null : $"/K {cmd}";
            case ShellFamily.PowerShell:
                return cmd.Length == 0 ? null : $"-NoExit -Command {cmd}";
            case ShellFamily.Posix:
                return cmd.Length == 0 ? "-l" : PosixArguments(cmd, ShellName(exePath!));
            default:
                return null;
        }
    }

    /// <summary>
    /// A POSIX row's arguments: an <em>interactive</em> login shell runs the command, then becomes
    /// a login shell. Interactive matters: Node under nvm (and Volta / fnm) puts its bin folder on
    /// PATH from ~/.bashrc / ~/.zshrc, and Ubuntu's ~/.bashrc returns at once for a non-interactive
    /// shell — measured, <c>bash -l -c</c> could not find an npm-installed codex that
    /// <c>bash -l -i -c</c> finds.
    /// </summary>
    public static string PosixArguments(string command, string shellName) =>
        $"-l -i -c \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}; exec {shellName} -l\"";

    /// <summary>
    /// Read the command back out of stored arguments. False when the arguments are not what
    /// <see cref="Compose"/> writes for this shell (hand-written flags, or a program that is not
    /// a shell) — the caller then shows the raw arguments instead of guessing.
    /// </summary>
    public static bool TryExtract(string? exePath, string? arguments, out string command)
    {
        command = "";
        var args = arguments?.Trim() ?? "";
        var family = FamilyOf(exePath);
        if (family == ShellFamily.Other) return false;
        if (args.Length == 0) return true;   // the bare shell: no command
        Match m;
        switch (family)
        {
            case ShellFamily.Cmd:
                m = CmdArgs().Match(args);
                break;
            case ShellFamily.PowerShell:
                m = PowerShellArgs().Match(args);
                break;
            default:
                if (args == "-l") return true;
                m = PosixArgs().Match(args);
                if (m.Success)
                {
                    command = m.Groups["cmd"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                    return command.Length > 0;
                }
                return false;
        }
        if (!m.Success) return false;
        command = m.Groups["cmd"].Value.Trim();
        return command.Length > 0;
    }

    private static string ShellName(string exePath)
    {
        var name = Path.GetFileName(exePath.Trim().Trim('"'));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    [GeneratedRegex(@"^/K\s+(?<cmd>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CmdArgs();

    [GeneratedRegex(@"^-NoExit\s+-Command\s+(?<cmd>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PowerShellArgs();

    [GeneratedRegex(@"^-l\s+-i\s+-c\s+""(?<cmd>(?:[^""\\]|\\.)*); exec \S+ -l""$", RegexOptions.Singleline)]
    private static partial Regex PosixArgs();
}

/// <summary>
/// The shells this machine can open a terminal in, for the Settings picker — only ones that are
/// actually here: PowerShell 7 (<c>pwsh</c>) is not part of Windows, and stock Ubuntu has no zsh.
/// The file check and environment are injected so a Windows test run can stand in for a Mac.
/// </summary>
public static class InstalledShells
{
    public static IReadOnlyList<string> Detect(bool? isWindows = null) =>
        (isWindows ?? OperatingSystem.IsWindows())
            ? Windows(File.Exists, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("ProgramFiles"))
            : Posix(File.Exists, ReadEtcShells(), Environment.GetEnvironmentVariable("SHELL"));

    /// <summary>
    /// cmd and Windows PowerShell always (both ship with Windows, stored bare as the seeded rows
    /// are); pwsh when it is on PATH (bare) or in its default install folder (full path); Git Bash
    /// when Git for Windows is installed.
    /// </summary>
    public static IReadOnlyList<string> Windows(Func<string, bool> exists, string? path, string? programFiles)
    {
        var shells = new List<string> { "cmd.exe", "powershell.exe" };
        var pathEntries = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (pathEntries.Any(d => SafeExists(exists, d, "pwsh.exe")))
            shells.Add("pwsh.exe");
        else if (!string.IsNullOrEmpty(programFiles) && exists(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")))
            shells.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        if (!string.IsNullOrEmpty(programFiles))
        {
            var gitBash = Path.Combine(programFiles, "Git", "bin", "bash.exe");
            if (exists(gitBash)) shells.Add(gitBash);
        }
        return shells;
    }

    /// <summary>
    /// The login shells <c>/etc/shells</c> names that exist and that <see cref="ShellLaunch"/> knows
    /// how to drive, with the user's own shell first; <see cref="PosixShell"/>'s candidates when
    /// the file says nothing usable.
    /// </summary>
    public static IReadOnlyList<string> Posix(Func<string, bool> exists, IEnumerable<string> etcShells, string? shellVariable)
    {
        var found = etcShells
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('/') && ShellLaunch.FamilyOf(l) == ShellFamily.Posix && exists(l))
            .Distinct()
            .ToList();
        if (found.Count == 0) found.AddRange(new PosixShell(exists, null).Installed());
        var preferred = new PosixShell(exists, shellVariable).Default();
        found.Remove(preferred);
        found.Insert(0, preferred);
        return found;
    }

    private static IEnumerable<string> ReadEtcShells()
    {
        try { return File.ReadAllLines("/etc/shells"); }
        catch { return []; }
    }

    private static bool SafeExists(Func<string, bool> exists, string dir, string file)
    {
        try { return exists(Path.Combine(dir.Trim().Trim('"'), file)); }
        catch { return false; }
    }
}
