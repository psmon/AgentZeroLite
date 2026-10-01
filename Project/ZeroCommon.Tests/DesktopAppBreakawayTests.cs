using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// The launcher behind the Store build's npm install (<see cref="DesktopAppBreakaway"/>).
/// What it is for — keeping the child out of an MSIX package's private AppData — can only
/// be seen inside a registered package (measured by hand, see the class remarks); what can
/// be pinned here is that the launch itself works: the attribute is accepted outside a
/// package too, output is captured, the exit code comes back, cancel kills the tree.
/// </summary>
[Trait("Category", "AgentCli")]
public sealed class DesktopAppBreakawayTests
{
    [Fact]
    public void A_test_runner_is_not_a_packaged_process()
    {
        // Every caller keys off this: false means "take the old path", which is what the
        // regular install and the WPF host must keep doing.
        Assert.False(DesktopAppBreakaway.IsPackagedProcess());
    }

    [Fact]
    public async Task Captures_merged_output_and_returns_the_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();

        var exit = await DesktopAppBreakaway.RunCapturedAsync(
            "cmd.exe /d /c \"echo out-line& echo err-line 1>&2& exit /b 3\"", lines.Add);

        Assert.Equal(3, exit);
        Assert.Contains(lines, l => l.Trim() == "out-line");
        Assert.Contains(lines, l => l.Trim() == "err-line");   // stderr shares the pipe
    }

    [Fact]
    public async Task Stdin_is_closed_so_a_prompt_fails_instead_of_hanging()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // `set /p` waits for a line; with stdin at EOF it returns at once.
        var exit = await DesktopAppBreakaway.RunCapturedAsync(
            "cmd.exe /d /c \"set /p x=? & echo done\"", null, timeout.Token);

        Assert.NotEqual(-1, exit);
    }

    [Fact]
    public async Task Cancel_stops_the_process_and_reports_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;

        var exit = await DesktopAppBreakaway.RunCapturedAsync("cmd.exe /d /c ping -n 30 127.0.0.1", lines.Add, cts.Token);

        Assert.Equal(-1, exit);
        Assert.Contains("Cancelled.", lines);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }
}
