using System.IO;

namespace Agent.Common.Module;

public enum SshShellKind
{
    Cmd,
    PowerShell,
    Other,
}

public enum SshAuthMode
{
    PublicKey,
    Password,
}

public sealed record SshLaunchSettings(
    bool IsRemote,
    string? Host,
    string? User,
    SshAuthMode AuthMode,
    string? KeyPath);

public static class SshCommandBuilder
{
    public const string AuthMethodPublicKey = "PublicKey";
    public const string AuthMethodPassword = "Password";

    public static SshShellKind DetectShellKind(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return SshShellKind.Other;
        var name = Path.GetFileName(exePath.Trim().Trim('"'));
        return name.ToLowerInvariant() switch
        {
            "cmd.exe" or "cmd" => SshShellKind.Cmd,
            "powershell.exe" or "powershell" or "pwsh.exe" or "pwsh" => SshShellKind.PowerShell,
            _ => SshShellKind.Other,
        };
    }

    public static SshAuthMode ParseAuthMethod(string? raw, SshAuthMode fallback = SshAuthMode.PublicKey) =>
        string.Equals(raw, AuthMethodPassword, StringComparison.OrdinalIgnoreCase) ? SshAuthMode.Password :
        string.Equals(raw, AuthMethodPublicKey, StringComparison.OrdinalIgnoreCase) ? SshAuthMode.PublicKey :
        fallback;

    /// <summary>
    /// Returns the ssh subcommand only (no shell wrapper). Empty when host/user
    /// missing. PublicKey mode embeds <c>-i "&lt;key&gt;"</c>; Password mode emits
    /// plain ssh — OpenSSH refuses passwords on argv, so the caller is
    /// responsible for delivering the saved password out-of-band (clipboard
    /// paste). Earlier follow-ups explored auto-fill via PTY output watching
    /// (race-prone) and plink (requires PuTTY install); operator settled on
    /// openssh + clipboard as the simplest reliable option.
    /// </summary>
    public static string BuildSshCommand(SshLaunchSettings ssh)
    {
        if (string.IsNullOrWhiteSpace(ssh.Host) || string.IsNullOrWhiteSpace(ssh.User))
            return string.Empty;

        var target = $"{ssh.User!.Trim()}@{ssh.Host!.Trim()}";
        if (ssh.AuthMode == SshAuthMode.PublicKey && !string.IsNullOrWhiteSpace(ssh.KeyPath))
            return $"ssh -i \"{ssh.KeyPath!.Trim()}\" {target}";
        return $"ssh {target}";
    }

    /// <summary>
    /// Composes the launch arguments for the chosen shell. When remote is OFF,
    /// returns <paramref name="baseArguments"/> unchanged. Shell-specific
    /// wrappers:
    /// <list type="bullet">
    ///   <item><c>cmd.exe</c> → <c>/K ssh …</c></item>
    ///   <item><c>powershell.exe</c> / <c>pwsh.exe</c> → <c>-NoExit -Command ssh …</c></item>
    ///   <item>Other shells → appends <c>ssh …</c> to whatever <paramref name="baseArguments"/> the user wrote, since we can't guess their REPL convention.</item>
    /// </list>
    /// </summary>
    public static string? ComposeArguments(
        string exePath,
        string? baseArguments,
        SshLaunchSettings ssh)
    {
        if (!ssh.IsRemote) return baseArguments;

        var sshCmd = BuildSshCommand(ssh);
        if (sshCmd.Length == 0) return baseArguments;

        var kind = DetectShellKind(exePath);
        return kind switch
        {
            SshShellKind.Cmd => $"/K {sshCmd}",
            SshShellKind.PowerShell => $"-NoExit -Command {sshCmd}",
            _ => string.IsNullOrWhiteSpace(baseArguments) ? sshCmd : $"{baseArguments} {sshCmd}",
        };
    }
}
