using System;
using System.Text;
using System.Threading;
using AgentZeroWpf.Services;
using Xunit;

namespace AgentTest;

/// <summary>
/// Live integration test for the WebViewXterm backend's native ConPTY host.
/// Unlike the ITerminalSession contract tests (which use fakes), this spawns a
/// real child process attached to a real pseudo-console and asserts its output
/// streams back — the one genuinely new native piece of the modern-terminal
/// spike. Windows + desktop session required (marker echoed by cmd.exe).
/// </summary>
public class ManagedConPtyHostTests
{
    [Fact]
    public void Spawns_child_and_streams_output()
    {
        const string marker = "AGENTZERO_CONPTY_OK";
        using var host = new ManagedConPtyHost();
        var sb = new StringBuilder();
        var seen = new ManualResetEventSlim();

        host.Output += chunk =>
        {
            lock (sb)
            {
                sb.Append(chunk);
                if (sb.ToString().Contains(marker)) seen.Set();
            }
        };

        // /k keeps cmd alive so ConPTY renders the echoed marker + prompt
        // (a fast /c process can exit before ConPTY paints).
        host.Start($"cmd.exe /k echo {marker}", workingDir: null, cols: 80, rows: 25);

        var ok = seen.Wait(TimeSpan.FromSeconds(15));
        Assert.True(ok, $"No marker in 15s. Diag: [{host.Diagnostics}] Output({sb.Length}): {sb.ToString().Replace("\x1b", "<ESC>")}");
        Assert.Contains(marker, sb.ToString());
    }

    [Fact]
    public void Write_reaches_child_stdin()
    {
        // A distinctive token that only appears if our stdin write reached cmd
        // AND cmd executed it. ConPTY interleaves cursor/VT codes into the echo,
        // so we strip ANSI (via the production parser) before matching.
        using var host = new ManagedConPtyHost();
        var sb = new StringBuilder();
        var ready = new ManualResetEventSlim();

        host.Output += chunk =>
        {
            lock (sb)
            {
                sb.Append(chunk);
                if (sb.ToString().Contains("PROMPTREADY")) ready.Set();
            }
        };

        // /k keeps cmd alive; the echoed READY token tells us the shell is up
        // and accepting input. Then we send `exit` over stdin — a DETERMINISTIC
        // proof: if the write reaches cmd, the cmd PROCESS exits, which we detect
        // by waiting on the process handle (ConPTY keeps the output pipe open
        // past child exit, so pipe-EOF is NOT a valid exit signal).
        host.Start("cmd.exe /k echo PROMPTREADY", workingDir: null, cols: 80, rows: 25);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(15)),
            $"cmd prompt never became ready. Diag:[{host.Diagnostics}]");

        Thread.Sleep(500); // let cmd park at its read prompt
        host.Write("exit\r".AsSpan());

        Assert.True(host.WaitForProcessExit(10000),
            $"cmd did not exit after stdin `exit` — the write never reached the child. Diag:[{host.Diagnostics}]");
    }
}
