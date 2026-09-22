using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;
using ZeroCommon.Tests.Remote;

namespace ZeroCommon.Tests;

/// <summary>
/// The password half of SSH mode: OpenSSH takes no password on argv, so the stored one
/// is typed at its prompt. Ported out of the WPF host so both GUIs answer the prompt.
/// </summary>
[Trait("Category", "Terminal")]
public sealed class SshPasswordWatcherTests
{
    private static CliDefinition PasswordDefinition() => new()
    {
        Name = "M4MAC",
        ExePath = "powershell.exe",
        IsRemote = true,
        SshHost = "h",
        SshUser = "u",
        SshAuthMethod = SshCommandBuilder.AuthMethodPassword,
        EncryptedPassword = "sealed:hunter2",
    };

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(15);
        }
        return condition();
    }

    [Fact]
    public async Task Types_the_password_once_the_prompt_appears()
    {
        var session = new FakeTerminalSession("Last login: Sun Sep 21\r\n");
        using var watcher = new SshPasswordWatcher(session, "hunter2", log: _ => { }, pollMs: 10, settleMs: 10);

        session.RaiseOutput("The authenticity of host 'h' cannot be established.\r\n");
        Assert.False(await WaitFor(() => session.Writes.Count > 0, 150));

        session.RaiseOutput("u@h's password: ");
        Assert.True(await WaitFor(() => session.Writes.Count > 0));
        Assert.Equal("hunter2", Assert.Single(session.Writes));

        // Once delivered it stays quiet — a second prompt is the operator's to answer.
        session.RaiseOutput("Permission denied, please try again.\r\nu@h's password: ");
        await Task.Delay(80);
        Assert.Single(session.Writes);
        Assert.True(watcher.Delivered);
    }

    [Fact]
    public async Task Stops_watching_when_disposed_with_the_tab()
    {
        var session = new FakeTerminalSession();
        var watcher = new SshPasswordWatcher(session, "hunter2", log: _ => { }, pollMs: 10, settleMs: 10);
        watcher.Dispose();

        session.RaiseOutput("u@h's password: ");
        await Task.Delay(120);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(false, SshCommandBuilder.AuthMethodPassword, "sealed:pw")]   // local definition
    [InlineData(true, SshCommandBuilder.AuthMethodPublicKey, "sealed:pw")]   // key auth needs no typing
    [InlineData(true, SshCommandBuilder.AuthMethodPassword, "")]             // nothing stored
    public void Arms_nothing_when_the_launch_needs_no_password(bool remote, string auth, string stored)
    {
        var def = PasswordDefinition();
        def.IsRemote = remote;
        def.SshAuthMethod = auth;
        def.EncryptedPassword = stored;

        Assert.Null(SshPasswordWatcher.ArmFor(def, new FakeTerminalSession(), Unseal, _ => { }));
    }

    [Fact]
    public void Arms_for_password_auth_and_skips_a_blob_this_account_cannot_open()
    {
        var session = new FakeTerminalSession();
        using var armed = SshPasswordWatcher.ArmFor(PasswordDefinition(), session, Unseal, _ => { });
        Assert.NotNull(armed);

        // A password sealed on another machine/account: launch without it rather than
        // typing something wrong into the remote shell.
        Assert.Null(SshPasswordWatcher.ArmFor(PasswordDefinition(), session, _ => null, _ => { }));
        Assert.Null(SshPasswordWatcher.ArmFor(PasswordDefinition(), session,
            _ => throw new InvalidOperationException("no key"), _ => { }));
    }

    private static string? Unseal(string? stored) =>
        stored is not null && stored.StartsWith("sealed:", StringComparison.Ordinal) ? stored["sealed:".Length..] : null;
}
