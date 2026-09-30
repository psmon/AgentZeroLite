using System.Diagnostics;

namespace AgentOne.Tools;

/// <summary>The dialects run_command can land in; each needs different advice.</summary>
public enum ShellDialect { Pwsh, WindowsPowerShell, Bash, Sh }

/// <summary>
/// The shell run_command actually uses, found once per process by asking it for
/// its version — and the one description of it every prompt shares. It used to
/// be the constant "PowerShell" on Windows: the model could not tell pwsh 7
/// from Windows PowerShell 5.1 (where `&amp;&amp;` does not exist) and wrote bash.
/// Measured (2026-09-30): `mkdir -p a b c` failed three times running under
/// pwsh 7.5, in a turn whose `a &amp;&amp; b` had just worked.
///
/// Whatever starts the process and whatever the model is told come from the
/// same instance, so the two cannot disagree.
/// </summary>
public sealed record ShellInfo(string Exe, ShellDialect Dialect, string Version)
{
    private static readonly Lazy<ShellInfo> Detected = new(Detect);

    /// <summary>The shell for this process, probed on first use.</summary>
    public static ShellInfo Current => Detected.Value;

    /// <summary>"PowerShell 7.5.3 (pwsh)", "Windows PowerShell 5.1 (powershell)", "bash 5.2.21", "sh".</summary>
    public string Describe => Dialect switch
    {
        ShellDialect.Pwsh => $"PowerShell {Or(Version, "7")} (pwsh)",
        ShellDialect.WindowsPowerShell => $"Windows PowerShell {Or(Version, "5.1")} (powershell)",
        ShellDialect.Bash => $"bash {Version}".TrimEnd(),
        _ => "sh"
    };

    /// <summary>What the model gets wrong in this dialect, said once in every prompt that plans commands.</summary>
    public string Hints => Dialect switch
    {
        ShellDialect.Pwsh =>
            "It is PowerShell, not bash: chain with && or ;, make folders with " +
            "`New-Item -ItemType Directory -Force a, b` (mkdir takes one path and has no -p), delete with " +
            "`Remove-Item -Recurse -Force`, read variables as $env:NAME.",
        ShellDialect.WindowsPowerShell =>
            "It is Windows PowerShell 5.1, not bash and not PowerShell 7: && and || do NOT exist — chain with ; " +
            "or `if ($?) { … }`; make folders with `New-Item -ItemType Directory -Force a, b` (mkdir takes one path " +
            "and has no -p), delete with `Remove-Item -Recurse -Force`, read variables as $env:NAME.",
        ShellDialect.Bash => "Chain with &&, make folders with `mkdir -p a b`, read variables as $NAME.",
        _ => "It is POSIX sh, not bash: no [[ ]], no arrays; chain with &&, make folders with `mkdir -p a b`."
    };

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    // ------------------------------------------------------------ detection

    private static ShellInfo Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            // Same order ShellToolbelt always used: pwsh when it is on PATH.
            var (exe, dialect) = OnPath("pwsh") ? ("pwsh", ShellDialect.Pwsh) : ("powershell", ShellDialect.WindowsPowerShell);
            return new ShellInfo(exe, dialect, Probe(exe, ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]));
        }

        if (File.Exists("/bin/bash"))
            return new ShellInfo("/bin/bash", ShellDialect.Bash, Probe("/bin/bash", ["-c", "echo ${BASH_VERSION%%(*}"]));
        return new ShellInfo("/bin/sh", ShellDialect.Sh, "");
    }

    /// <summary>The shell's own answer, first line; empty when it cannot be asked. Never fatal.</summary>
    private static string Probe(string exe, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return "";
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return "";
            }
            var line = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return line.Length <= 32 ? line : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    internal static bool OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe))) return true;
        }
        return false;
    }
}
