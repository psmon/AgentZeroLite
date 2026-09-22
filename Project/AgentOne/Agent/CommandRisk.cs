using System.Text.RegularExpressions;

namespace AgentOne.Agent;

/// <summary>
/// The floor under the command gate: patterns that are dangerous whatever a
/// decision engine says. The engine judges the grey area — "is `npm install`
/// fine here?" — but `rm -rf /`, `format`, `sudo` and a piped installer are
/// not a judgement call, and a gate that could be argued out of them by a
/// well-phrased command would not be a gate. Anything matched here is always
/// put to a person.
/// </summary>
public static partial class CommandRisk
{
    /// <param name="Dangerous">True when the command must be approved by a person.</param>
    /// <param name="Reason">What was matched, for the prompt that asks.</param>
    public readonly record struct Verdict(bool Dangerous, string Reason);

    public static Verdict Inspect(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return new Verdict(false, "");

        foreach (var (regex, reason) in Rules)
            if (regex.IsMatch(text))
                return new Verdict(true, reason);

        return new Verdict(false, "");
    }

    private static readonly (Regex Pattern, string Reason)[] Rules =
    [
        (DeleteOutsideProject(), "recursive delete aimed at the filesystem root, home, a parent folder or an absolute path"),
        (WindowsDeleteTree(), "recursive delete of a directory tree"),
        (DiskAndFilesystem(), "formats, partitions or writes a raw disk"),
        (Elevation(), "asks for elevated (root/administrator) rights"),
        (PowerAndSessions(), "shuts down, reboots or logs out the machine"),
        (Registry(), "edits the Windows registry"),
        (SystemLocations(), "writes under a system directory"),
        (PermissionsWide(), "changes permissions or ownership recursively"),
        (PipedInstaller(), "runs code downloaded from the network without looking at it"),
        (DestructiveGit(), "rewrites or discards git history (force-push, hard reset, clean)"),
        (SchedulingAndPolicy(), "changes scheduled tasks, cron, or the execution policy"),
        (NetworkAndFirewall(), "changes users, network or firewall settings"),
        (ForkBomb(), "fork bomb"),
    ];

    // rm -rf / ,  rm -rf ~ ,  rm -rf .. ,  rm -rf /abs ,  rm -rf C:\ — but not rm -rf bin
    [GeneratedRegex(@"\brm\s+(-[a-zA-Z]*r[a-zA-Z]*\s+|--recursive\s+)+(""|')?(/|~|\.\.|[A-Za-z]:[\\/]|\*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteOutsideProject();

    [GeneratedRegex(@"\b(rd|rmdir)\s+/s\b|\bdel\s+(/[a-z]\s+)*/s\b|\bRemove-Item\b[^\n|]*\s-Recurse\b[^\n|]*\s(""|')?([A-Za-z]:[\\/]|~|\.\.|/)|\bRemove-Item\b[^\n|]*\s(""|')?([A-Za-z]:[\\/]|~|\.\.|/)[^\n|]*\s-Recurse\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDeleteTree();

    [GeneratedRegex(@"\b(format(\.com)?\s+[a-z]:|mkfs(\.\w+)?\b|diskpart\b|dd\s+if=|fdisk\b|Clear-Disk\b|Format-Volume\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskAndFilesystem();

    [GeneratedRegex(@"(^|[\s;&|])(sudo|doas|su|runas|pkexec)\b|-Verb\s+RunAs\b", RegexOptions.IgnoreCase)]
    private static partial Regex Elevation();

    [GeneratedRegex(@"\b(shutdown|reboot|halt|poweroff|logoff|Restart-Computer|Stop-Computer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PowerAndSessions();

    [GeneratedRegex(@"\b(reg(\.exe)?\s+(add|delete|import)|regedit|Set-ItemProperty\s+[^\n]*HK(LM|CU)|New-ItemProperty\s+[^\n]*HK(LM|CU)|Remove-Item\s+[^\n]*HK(LM|CU))", RegexOptions.IgnoreCase)]
    private static partial Regex Registry();

    [GeneratedRegex(@"(>|>>|\btee\b|\bcp\b|\bmv\b|Copy-Item|Move-Item|Set-Content|Out-File)[^\n|]*\s(""|')?(/etc/|/usr/|/bin/|/boot/|/var/|[A-Za-z]:\\Windows\\|[A-Za-z]:\\Program Files)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemLocations();

    [GeneratedRegex(@"\b(chmod|chown|chgrp)\s+(-[a-zA-Z]*R[a-zA-Z]*|--recursive)\b|\bicacls\b[^\n]*\s/T\b|\btakeown\b", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionsWide();

    [GeneratedRegex(@"\b(curl|wget|Invoke-WebRequest|iwr)\b[^\n]*\|\s*(sudo\s+)?(sh|bash|zsh|pwsh|powershell|iex|Invoke-Expression)\b|\b(iex|Invoke-Expression)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex PipedInstaller();

    [GeneratedRegex(@"\bgit\s+(push\b[^\n]*(--force|-f\b)|reset\s+--hard|clean\s+-[a-z]*[fx]|branch\s+-D|filter-branch|push\b[^\n]*--delete)", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveGit();

    [GeneratedRegex(@"\b(schtasks|crontab\s+-r|crontab\s+-e|Set-ExecutionPolicy|Register-ScheduledTask|launchctl\s+(load|unload)|systemctl\s+(enable|disable|mask))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SchedulingAndPolicy();

    [GeneratedRegex(@"\b(net\s+(user|localgroup)|netsh\b|iptables\b|ufw\b|New-NetFirewallRule|Set-NetFirewallProfile|useradd|userdel|passwd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkAndFirewall();

    [GeneratedRegex(@":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:")]
    private static partial Regex ForkBomb();
}
