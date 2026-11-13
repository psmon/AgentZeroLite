using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// Settings asks for the shell and the command to start in it (<c>claude</c>); the flags that make
/// each shell run it and stay open are composed here and must read back from what is stored —
/// including every row the seeding writes — or the page would show raw flags again.
/// </summary>
public class ShellLaunchTests
{
    [Theory]
    [InlineData("cmd.exe", ShellFamily.Cmd)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", ShellFamily.Cmd)]
    [InlineData("powershell.exe", ShellFamily.PowerShell)]
    [InlineData("pwsh.exe", ShellFamily.PowerShell)]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellFamily.PowerShell)]
    [InlineData("/bin/zsh", ShellFamily.Posix)]
    [InlineData("/usr/bin/fish", ShellFamily.Posix)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", ShellFamily.Posix)]
    [InlineData("claude", ShellFamily.Other)]
    [InlineData("", ShellFamily.Other)]
    public void Family_comes_from_the_file_name(string exe, ShellFamily expected) =>
        Assert.Equal(expected, ShellLaunch.FamilyOf(exe));

    [Theory]
    [InlineData("powershell.exe", "claude", "-NoExit -Command claude")]
    [InlineData("pwsh.exe", "agent-one chat", "-NoExit -Command agent-one chat")]
    [InlineData("cmd.exe", "claude", "/K claude")]
    [InlineData("/bin/bash", "claude", "-l -i -c \"claude; exec bash -l\"")]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", "codex", "-l -i -c \"codex; exec bash -l\"")]
    [InlineData("/bin/zsh", "", "-l")]
    [InlineData("powershell.exe", "", null)]
    [InlineData("cmd.exe", "  ", null)]
    [InlineData("claude", "anything", null)]
    public void Compose_writes_the_shell_specific_wrapper(string exe, string command, string? expected) =>
        Assert.Equal(expected, ShellLaunch.Compose(exe, command));

    [Theory]
    [InlineData("powershell.exe", "claude")]
    [InlineData("pwsh.exe", "netclaw chat")]
    [InlineData("cmd.exe", "claude --resume")]
    [InlineData("/bin/zsh", "codex")]
    [InlineData("/bin/bash", "echo \"hi\"")]                  // quotes survive the POSIX wrapper
    [InlineData("/bin/zsh", "")]
    [InlineData("powershell.exe", "")]
    public void Extract_reads_back_what_compose_wrote(string exe, string command)
    {
        Assert.True(ShellLaunch.TryExtract(exe, ShellLaunch.Compose(exe, command), out var back));
        Assert.Equal(command, back);
    }

    [Fact]
    public void Seeded_rows_read_back_as_their_command()
    {
        // Exactly what AppDbContext seeds for Windows and POSIX agent rows.
        Assert.True(ShellLaunch.TryExtract("powershell.exe", "-NoExit -Command claude", out var win));
        Assert.Equal("claude", win);
        Assert.True(ShellLaunch.TryExtract("/bin/bash", "-l -i -c \"agent-one chat; exec bash -l\"", out var posix));
        Assert.Equal("agent-one chat", posix);
        Assert.True(ShellLaunch.TryExtract("/bin/zsh", "-l", out var bare));
        Assert.Equal("", bare);
    }

    [Theory]
    [InlineData("powershell.exe", "-NoLogo -NoExit -Command claude")]   // hand-written flags
    [InlineData("cmd.exe", "/C dir")]
    [InlineData("/bin/zsh", "-c \"claude\"")]
    [InlineData("claude", "--resume")]                                 // not a shell at all
    public void Arguments_compose_did_not_write_are_left_raw(string exe, string args) =>
        Assert.False(ShellLaunch.TryExtract(exe, args, out _));

    [Fact]
    public void Windows_lists_pwsh_and_git_bash_only_when_installed()
    {
        Assert.Equal(new[] { "cmd.exe", "powershell.exe" },
            InstalledShells.Windows(_ => false, "C:\\Windows\\System32", "C:\\Program Files"));

        var onPath = InstalledShells.Windows(p => p == "C:\\pwsh7\\pwsh.exe", "C:\\Windows;C:\\pwsh7", "C:\\Program Files");
        Assert.Equal(new[] { "cmd.exe", "powershell.exe", "pwsh.exe" }, onPath);

        var installedOnly = InstalledShells.Windows(
            p => p is "C:\\Program Files\\PowerShell\\7\\pwsh.exe" or "C:\\Program Files\\Git\\bin\\bash.exe",
            "C:\\Windows", "C:\\Program Files");
        Assert.Equal(new[] { "cmd.exe", "powershell.exe", "C:\\Program Files\\PowerShell\\7\\pwsh.exe", "C:\\Program Files\\Git\\bin\\bash.exe" },
            installedOnly);
    }

    [Fact]
    public void Posix_lists_existing_known_shells_with_the_users_shell_first()
    {
        var etc = new[] { "# comment", "/bin/sh", "/bin/bash", "/bin/zsh", "/bin/csh", "/usr/bin/fish", "/bin/ksh" };
        var mac = InstalledShells.Posix(p => p is not "/bin/ksh", etc, "/bin/zsh");
        Assert.Equal(new[] { "/bin/zsh", "/bin/sh", "/bin/bash", "/usr/bin/fish" }, mac);   // csh: not drivable; ksh: missing

        var ubuntu = InstalledShells.Posix(p => p is "/bin/sh" or "/bin/bash", etc, "/bin/bash");
        Assert.Equal(new[] { "/bin/bash", "/bin/sh" }, ubuntu);
    }
}
