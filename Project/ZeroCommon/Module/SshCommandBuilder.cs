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

    /// <summary>
    /// Host-key policy applied to every launched ssh. <c>accept-new</c> auto-
    /// accepts the key of a host we have never seen (skipping the interactive
    /// <c>Are you sure you want to continue connecting (yes/no)?</c> prompt that
    /// otherwise blocks the FIRST connection — and, because the password prompt
    /// only appears AFTER it, blocks the password autofill too). A host whose
    /// key has *changed* is still refused, so this keeps MITM protection for
    /// known hosts while removing the first-connect speed bump.
    /// </summary>
    public const string HostKeyOption = "-o StrictHostKeyChecking=accept-new";

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
    /// paste, or the PTY-output-watching autofill in <c>SshPasswordAutofill</c>).
    /// Every command carries <see cref="HostKeyOption"/> so a first-time
    /// connection does not stall on the host-key <c>yes/no</c> prompt (which
    /// also hid the password prompt from the autofill).
    /// </summary>
    public static string BuildSshCommand(SshLaunchSettings ssh)
    {
        if (string.IsNullOrWhiteSpace(ssh.Host) || string.IsNullOrWhiteSpace(ssh.User))
            return string.Empty;

        var target = $"{ssh.User!.Trim()}@{ssh.Host!.Trim()}";
        if (ssh.AuthMode == SshAuthMode.PublicKey && !string.IsNullOrWhiteSpace(ssh.KeyPath))
            return $"ssh {HostKeyOption} -i \"{ssh.KeyPath!.Trim()}\" {target}";
        return $"ssh {HostKeyOption} {target}";
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
