using System.Text;
using AgentZeroAvalonia.Services;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The real backend for the OS running the tests: ConPTY on Windows, Porta.Pty elsewhere.</summary>
public class PtyHostTests
{
    [Fact]
    public void Echo_round_trip_through_this_os_backend()
    {
        Assert.True(PtyHostFactory.IsSupported, PtyHostFactory.BackendName);
        var marker = "AZ_PTY_TEST_" + Guid.NewGuid().ToString("N")[..8];
        var seen = new ManualResetEventSlim();
        var exited = new ManualResetEventSlim();
        var sb = new StringBuilder();

        using var host = PtyHostFactory.Start(PtyHostFactory.EchoSpec(marker), 100, 30);
        host.Output += s =>
        {
            lock (sb)
            {
                sb.Append(s);
                if (sb.ToString().Contains(marker, StringComparison.Ordinal)) seen.Set();
            }
        };
        host.Exited += exited.Set;

        Assert.True(seen.Wait(TimeSpan.FromSeconds(15)), $"marker not echoed | {host.Diagnostics}");
        host.Resize(120, 40); // must not throw while (or after) the child runs
        Assert.True(exited.Wait(TimeSpan.FromSeconds(15)), $"exit not signalled | {host.Diagnostics}");
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Dispose_before_exit_kills_the_child()
    {
        var spec = PtyHostFactory.EchoSpec("x") with
        {
            Args = OperatingSystem.IsWindows() ? new[] { "/c", "ping -n 30 127.0.0.1 >nul" } : new[] { "-c", "sleep 30" },
        };
        var exited = new ManualResetEventSlim();
        var host = PtyHostFactory.Start(spec, 80, 24);
        host.Exited += exited.Set;
        Assert.True(host.IsRunning);
        host.Dispose();
        Assert.False(host.IsRunning);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(10)), "exit not signalled after Dispose");
    }
}
