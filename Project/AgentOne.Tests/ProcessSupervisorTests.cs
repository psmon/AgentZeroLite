using System.Diagnostics;
using AgentOne.Agent;
using AgentOne.Processes;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// The process sub-agent: commands run under ProcessActors, the belt watches
/// (notification + check-in) and asks instead of hanging. The case that shaped
/// it — a child left holding the output pipe — is reproduced on both shells.
/// </summary>
public class ProcessSupervisorTests : IDisposable
{
    private readonly string _root;

    public ProcessSupervisorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-one-procs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private ShellToolbelt Belt(TimeSpan timeout, Func<ChoiceRequest, int>? choose = null) =>
        new(_root, timeout)
        {
            Gate = (_, _) => Task.FromResult(GateVerdict.Allow()),
            Choose = choose is null ? null : (ask, _) => Task.FromResult(choose(ask)),
            CheckIn = TimeSpan.FromMilliseconds(300),
            ReadyWait = TimeSpan.FromSeconds(15),
        };

    private static ToolCall Run(string command, bool background = false) => background
        ? TestCalls.Make("run_command", ("command", command), ("background", "true"))
        : TestCalls.Make("run_command", ("command", command));

    /// <summary>Sleeps for <paramref name="seconds"/> in the shell the belt starts.</summary>
    private static string Sleep(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    [Fact]
    public async Task AChildLeftHoldingTheOutputDoesNotHangTheStep()
    {
        // The shell exits at once; the child it started inherits stdout and lives on.
        // The old belt awaited that pipe's EOF and never came back.
        var command = OperatingSystem.IsWindows()
            ? "cmd /c \"start /b ping -n 60 127.0.0.1 >nul\"; echo shell-done"
            : "(sleep 60 &) ; echo shell-done";

        using var belt = Belt(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        var result = await belt.InvokeAsync(Run(command), CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.Contains("shell-done", result.Text);
    }

    [Fact]
    public async Task AnOverrunIsPutToTheChooserAndStopIsHonoured()
    {
        ChoiceRequest? asked = null;
        using var belt = Belt(TimeSpan.FromSeconds(1), ask => { asked = ask; return 2; });
        var checkIns = new List<string>();
        belt.Activity += checkIns.Add;

        var sw = Stopwatch.StartNew();
        var result = await belt.InvokeAsync(Run(Sleep(60)), CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.NotNull(asked);
        Assert.Contains("still running", asked!.Question);
        Assert.Equal([ShellToolbelt.WaitOption, ShellToolbelt.BackgroundOption, ShellToolbelt.StopOption], asked.Options);
        Assert.False(result.Ok);
        Assert.Contains("killed after", result.Text);
        Assert.NotEmpty(checkIns);   // the parent checked in before deciding
    }

    [Fact]
    public async Task UnattendedOverrunTakesTheRecommendationWhichIsStop()
    {
        using var belt = Belt(TimeSpan.FromSeconds(1));   // no chooser

        var result = await belt.InvokeAsync(Run(Sleep(60)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("killed after", result.Text);
    }

    [Fact]
    public async Task AnOverrunMovedToTheBackgroundKeepsRunningAndCanBeCheckedAndStopped()
    {
        using var belt = Belt(TimeSpan.FromSeconds(1), _ => 1);
        var notes = new List<ProcessSnapshot>();
        belt.ProcessChanged += notes.Add;

        var moved = await belt.InvokeAsync(Run(Sleep(60)), CancellationToken.None);
        Assert.True(moved.Ok, moved.Text);
        Assert.Contains("left in the background as p1", moved.Text);

        var status = await belt.InvokeAsync(TestCalls.Make("process_status"), CancellationToken.None);
        Assert.Contains("p1 · task · running", status.Text);

        var stopped = await belt.InvokeAsync(TestCalls.Make("process_stop", ("id", "p1")), CancellationToken.None);
        Assert.True(stopped.Ok, stopped.Text);
        Assert.Contains("killed", stopped.Text);

        // The observer heard the end of a detached process.
        await WaitUntil(() => notes.Any(n => n.Id == "p1" && n.Ended));
    }

    [Fact]
    public async Task AServiceIsAskedAboutFirstAndSkipRunsNothing()
    {
        ChoiceRequest? asked = null;
        using var belt = Belt(TimeSpan.FromSeconds(30), ask => { asked = ask; return 2; });

        var result = await belt.InvokeAsync(Run("npm run dev"), CancellationToken.None);

        Assert.NotNull(asked);
        Assert.Equal(ShellToolbelt.ServiceSkipOption, asked!.Options[2]);
        Assert.False(result.Ok);
        Assert.Contains("chose not to start", result.Text);
        Assert.False(belt.HasProcesses);
    }

    [Fact]
    public async Task ABackgroundServiceReturnsOnceReadyWithItsAddressAndStaysUp()
    {
        // Prints a ready line with an address, then keeps running.
        var command = OperatingSystem.IsWindows()
            ? "echo ' * Running on http://127.0.0.1:5999'; Start-Sleep -Seconds 60"
            : "echo ' * Running on http://127.0.0.1:5999'; sleep 60";

        using var belt = Belt(TimeSpan.FromSeconds(30), _ => 0);
        var sw = Stopwatch.StartNew();
        var result = await belt.InvokeAsync(Run(command, background: true), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Contains("ready at http://127.0.0.1:5999", result.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");

        var list = await belt.Processes.ListAsync();
        Assert.Contains(list, s => s.Id == "p1" && s.State == ProcessState.Ready);
    }

    [Fact]
    public async Task ASmokeRunStopsTheServiceAfterItComesUp()
    {
        var command = OperatingSystem.IsWindows()
            ? "echo 'Listening on http://localhost:5998'; Start-Sleep -Seconds 60"
            : "echo 'Listening on http://localhost:5998'; sleep 60";

        using var belt = Belt(TimeSpan.FromSeconds(30), _ => 1);
        var result = await belt.InvokeAsync(Run(command, background: true), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Contains("smoke run of p1: ready at http://localhost:5998; stopped", result.Text);

        var snap = await belt.Processes.QueryAsync("p1");
        Assert.Equal(ProcessState.Killed, snap!.State);
    }

    [Fact]
    public async Task DisposingTheBeltStopsWhatItLeftRunning()
    {
        var belt = Belt(TimeSpan.FromSeconds(1), _ => 1);
        var moved = await belt.InvokeAsync(Run(Sleep(60)), CancellationToken.None);
        var snap = await belt.Processes.QueryAsync("p1");
        var pid = snap!.Pid!.Value;

        belt.Dispose();

        await WaitUntil(() =>
        {
            try { return Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return true; }
        });
        Assert.True(moved.Ok);
    }

    [Fact]
    public async Task StoppingKillsGrandchildrenToo()
    {
        // shell → cmd/sh → ping/sleep: the grandchild is what used to survive.
        var command = OperatingSystem.IsWindows() ? "cmd /c \"ping -n 60 127.0.0.1\"" : "sh -c 'sleep 60; true'";

        using var belt = Belt(TimeSpan.FromSeconds(1), _ => 1);
        await belt.InvokeAsync(Run(command), CancellationToken.None);
        var snap = await belt.Processes.QueryAsync("p1");
        var below = ProcessTree.Descendants(snap!.Pid!.Value);
        Assert.NotEmpty(below);

        await belt.InvokeAsync(TestCalls.Make("process_stop", ("id", "p1")), CancellationToken.None);

        await WaitUntil(() => below.All(Gone));
    }

    private static bool Gone(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public void LongRunningCommandsAreRecognised()
    {
        Assert.True(CommandLifetime.LooksLongRunning("flask run --debug"));
        Assert.True(CommandLifetime.LooksLongRunning("npm run dev"));
        Assert.True(CommandLifetime.LooksLongRunning("uvicorn main:app --reload"));
        Assert.True(CommandLifetime.LooksLongRunning("python manage.py runserver"));
        Assert.True(CommandLifetime.LooksLongRunning("dotnet watch run"));

        Assert.False(CommandLifetime.LooksLongRunning("npm install"));
        Assert.False(CommandLifetime.LooksLongRunning("python --version"));
        Assert.False(CommandLifetime.LooksLongRunning("dotnet build"));
        Assert.False(CommandLifetime.LooksLongRunning("pip install flask flask-sqlalchemy"));
    }

    [Fact]
    public void AScriptThatServesIsLongRunningWhenRunDirectly()
    {
        // The board-web case: `python app.py` where app.py ends in app.run(debug=True).
        File.WriteAllText(Path.Combine(_root, "app.py"), "from flask import Flask\napp = Flask(__name__)\nif __name__ == '__main__':\n    app.run(debug=True)\n");
        File.WriteAllText(Path.Combine(_root, "tool.py"), "print('hi')\n");

        Assert.True(CommandLifetime.LooksLongRunning("python app.py", _root));
        Assert.False(CommandLifetime.LooksLongRunning("python tool.py", _root));
        Assert.False(CommandLifetime.LooksLongRunning("python missing.py", _root));
    }

    [Fact]
    public void ReadinessIsReadFromTheOutput()
    {
        Assert.True(CommandLifetime.DetectReady(" * Running on http://127.0.0.1:5000\nPress CTRL+C to quit", out var flask));
        Assert.Equal("http://127.0.0.1:5000", flask);

        Assert.True(CommandLifetime.DetectReady("INFO:     Uvicorn running on http://0.0.0.0:8000 (Press CTRL+C to quit)", out var uvicorn));
        Assert.Equal("http://127.0.0.1:8000", uvicorn);

        Assert.True(CommandLifetime.DetectReady("webpack compiled successfully", out var none));
        Assert.Null(none);

        Assert.False(CommandLifetime.DetectReady("Installing dependencies…", out _));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(15)) Assert.Fail("condition not met within 15 s");
            await Task.Delay(100);
        }
    }
}
