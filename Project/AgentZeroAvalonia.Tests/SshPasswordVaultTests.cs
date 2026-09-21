using AgentZeroAvalonia.Security;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// The CLI-definition password column is shared with the WPF host, so the blob this host
/// writes must be the one that host reads: raw base64, no marker.
/// </summary>
public class SshPasswordVaultTests
{
    [Fact]
    public void Round_trips_a_password_in_the_format_the_other_host_reads()
    {
        var sealedPw = SshPasswordVault.Protect("hunter2");
        Assert.NotEqual("hunter2", sealedPw);
        // The settings-file markers belong to the other vault; this one is bare base64.
        Assert.DoesNotContain(":", sealedPw);
        Assert.Equal("hunter2", SshPasswordVault.Unprotect(sealedPw));
    }

    [Fact]
    public void Nothing_stored_opens_to_nothing_and_garbage_does_not_throw()
    {
        Assert.Equal("", SshPasswordVault.Protect(null));
        Assert.Null(SshPasswordVault.Unprotect(null));
        Assert.Null(SshPasswordVault.Unprotect(""));
        Assert.Null(SshPasswordVault.Unprotect("not base64 at all"));
    }
}
