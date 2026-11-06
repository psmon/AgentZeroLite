using System.Diagnostics;
using System.Text;

namespace AgentOne.Tools.Cli;

/// <param name="TimedOut">True when the process was killed at the deadline.</param>
public sealed record CliRun(int ExitCode, string Output, string Errors, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;

    public string Combined => Errors.Trim().Length == 0 ? Output : $"{Output}\n{Errors}".Trim();
}

/// <summary>
/// Runs one short-lived tool process — an exe and an argument LIST, never a
/// shell line — and returns what it printed.
///
/// The end is the process's exit, not its pipes. playwright-cli starts a
/// daemon on first use, and a child that inherits the output pipe would keep
/// a ReadToEnd waiting for good — the same trap the Flask reloader set for the
/// shell belt (measured 2026-10-06, 44 minutes). So output is read as it
/// arrives, the wait is on exit, and after the exit the readers get a short
/// grace before whatever they have is taken.
/// </summary>
public static class CliRunner
{
    private static readonly TimeSpan Drain = TimeSpan.FromMilliseconds(750);

    public static async Task<CliRun> RunAsync(string exe, IEnumerable<string> args, string workingDirectory,
        TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        // The npm update banner and colour codes are noise in a tool result.
        info.Environment["NO_COLOR"] = "1";
        info.Environment["NO_UPDATE_NOTIFIER"] = "1";
        info.Environment["npm_config_update_notifier"] = "false";

        var output = new StringBuilder();
        var errors = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) outDone.TrySetResult();
            else lock (output) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) errDone.TrySetResult();
            else lock (errors) errors.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start()) return new CliRun(-1, "", $"could not start {exe}", false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CliRun(-1, "", $"could not start {exe}: {ex.Message}", false);
        }

        try { process.StandardInput.Close(); } catch (IOException) { /* gone already */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ct.ThrowIfCancellationRequested();
                timedOut = true;
            }
        }

        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(Drain, CancellationToken.None));

        string o, e2;
        lock (output) o = output.ToString();
        lock (errors) e2 = errors.ToString();
        var code = timedOut ? -1 : SafeExitCode(process);
        return new CliRun(code, o.TrimEnd(), e2.TrimEnd(), timedOut);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
