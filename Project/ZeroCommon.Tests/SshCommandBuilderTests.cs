namespace ZeroCommon.Tests;

public class SshCommandBuilderTests
{
    [Theory]
    [InlineData("cmd.exe", SshShellKind.Cmd)]
    [InlineData("CMD.EXE", SshShellKind.Cmd)]
    [InlineData(@"C:\Windows\System32\cmd.exe", SshShellKind.Cmd)]
    [InlineData("powershell.exe", SshShellKind.PowerShell)]
    [InlineData("pwsh.exe", SshShellKind.PowerShell)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", SshShellKind.PowerShell)]
    [InlineData("bash.exe", SshShellKind.Other)]
    [InlineData("", SshShellKind.Other)]
    public void DetectShellKind_classifies_the_three_supported_shells(string exe, SshShellKind expected)
    {
        Assert.Equal(expected, SshCommandBuilder.DetectShellKind(exe));
    }

    [Theory]
    [InlineData("PublicKey", SshAuthMode.PublicKey)]
    [InlineData("publickey", SshAuthMode.PublicKey)]
    [InlineData("Password", SshAuthMode.Password)]
    [InlineData("PASSWORD", SshAuthMode.Password)]
    [InlineData("", SshAuthMode.PublicKey)]
    [InlineData(null, SshAuthMode.PublicKey)]
    public void ParseAuthMethod_round_trips_known_constants(string? raw, SshAuthMode expected)
    {
        Assert.Equal(expected, SshCommandBuilder.ParseAuthMethod(raw));
    }

    [Fact]
    public void BuildSshCommand_publickey_quotes_the_pem_path()
    {
        var ssh = new SshLaunchSettings(
            IsRemote: true,
            Host: "192.168.0.50",
            User: "psmac",
            AuthMode: SshAuthMode.PublicKey,
            KeyPath: @"C:\keys\id_rsa.pem");

        var cmd = SshCommandBuilder.BuildSshCommand(ssh);

        Assert.Equal(@"ssh -i ""C:\keys\id_rsa.pem"" psmac@192.168.0.50", cmd);
    }

    [Fact]
    public void BuildSshCommand_publickey_without_pem_falls_back_to_plain_ssh()
    {
        // Operator forgot to attach a key — composer should still emit a valid
        // ssh command so user can fix it interactively rather than getting a
        // silently-empty arg list.
        var ssh = new SshLaunchSettings(true, "host", "alice", SshAuthMode.PublicKey, KeyPath: null);
        Assert.Equal("ssh alice@host", SshCommandBuilder.BuildSshCommand(ssh));
    }

    [Fact]
    public void BuildSshCommand_password_mode_never_embeds_credentials_on_argv()
    {
        // OpenSSH refuses passwords on argv (by design — process listings would
        // leak the password). The launcher delivers the stored password
        // out-of-band (clipboard) so the user can paste at ssh's prompt.
        // Keep this invariant explicit so a future "make it auto-fill" attempt
        // can't silently regress security by sneaking a flag into argv.
        var ssh = new SshLaunchSettings(true, "host", "alice", SshAuthMode.Password, KeyPath: null);
        var cmd = SshCommandBuilder.BuildSshCommand(ssh);
        Assert.Equal("ssh alice@host", cmd);
        Assert.DoesNotContain("-pw", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plink", cmd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSshCommand_returns_empty_when_host_or_user_missing()
    {
        Assert.Equal("", SshCommandBuilder.BuildSshCommand(new(true, "", "alice", SshAuthMode.PublicKey, null)));
        Assert.Equal("", SshCommandBuilder.BuildSshCommand(new(true, "host", null, SshAuthMode.PublicKey, null)));
    }

    [Fact]
    public void ComposeArguments_when_not_remote_returns_base_unchanged()
    {
        var ssh = new SshLaunchSettings(IsRemote: false, "host", "alice", SshAuthMode.PublicKey, null);
        Assert.Equal("-NoExit -Command claude", SshCommandBuilder.ComposeArguments("pwsh.exe", "-NoExit -Command claude", ssh));
    }

    [Fact]
    public void ComposeArguments_cmd_wraps_with_slash_K()
    {
        var ssh = new SshLaunchSettings(true, "192.168.0.50", "psmac", SshAuthMode.PublicKey, null);
        var args = SshCommandBuilder.ComposeArguments("cmd.exe", baseArguments: null, ssh);
        Assert.Equal("/K ssh psmac@192.168.0.50", args);
    }

    [Fact]
    public void ComposeArguments_powershell_5_uses_NoExit_Command()
    {
        var ssh = new SshLaunchSettings(true, "192.168.0.50", "psmac", SshAuthMode.PublicKey, null);
        var args = SshCommandBuilder.ComposeArguments("powershell.exe", baseArguments: "-NoExit -Command claude", ssh);
        // Mission body's example verbatim — keeps the contract honest.
        Assert.Equal("-NoExit -Command ssh psmac@192.168.0.50", args);
    }

    [Fact]
    public void ComposeArguments_powershell_7_with_pem_quotes_the_path()
    {
        var ssh = new SshLaunchSettings(true, "10.0.0.5", "root", SshAuthMode.PublicKey, @"C:\keys\prod.pem");
        var args = SshCommandBuilder.ComposeArguments(@"C:\Program Files\PowerShell\7\pwsh.exe", null, ssh);
        Assert.Equal(@"-NoExit -Command ssh -i ""C:\keys\prod.pem"" root@10.0.0.5", args);
    }

    [Fact]
    public void ComposeArguments_other_shell_appends_to_existing_args()
    {
        // bash / nushell / etc — the operator's existing args (e.g. `--login`)
        // remain valid; we just append a literal ssh call after them. The user
        // accepts that their REPL will see it as the first command.
        var ssh = new SshLaunchSettings(true, "host", "alice", SshAuthMode.PublicKey, null);
        var args = SshCommandBuilder.ComposeArguments(@"C:\Program Files\Git\bin\bash.exe", "--login", ssh);
        Assert.Equal("--login ssh alice@host", args);
    }

    [Fact]
    public void ComposeArguments_invalid_remote_keeps_base_args()
    {
        // Host missing → fall back so the tab still opens normally instead of
        // breaking with an unusable command.
        var ssh = new SshLaunchSettings(IsRemote: true, Host: "", User: "alice", AuthMode: SshAuthMode.PublicKey, KeyPath: null);
        var args = SshCommandBuilder.ComposeArguments("pwsh.exe", "-NoExit -Command claude", ssh);
        Assert.Equal("-NoExit -Command claude", args);
    }
}
