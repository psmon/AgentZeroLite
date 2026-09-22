using System.Diagnostics;
using System.Text;
using AgentOne.Agent;

namespace AgentOne.Tools;

/// <param name="Allowed">False means the command is not run at all.</param>
/// <param name="Reason">Why, in a sentence the model can act on.</param>
public readonly record struct GateVerdict(bool Allowed, string Reason)
{
    public static GateVerdict Allow(string reason = "allowed") => new(true, reason);
    public static GateVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The one verb that runs something: <c>run_command</c>, through the
/// platform's own shell — PowerShell on Windows, bash (or sh) elsewhere —
/// with the workspace root as its working directory. Nothing runs without the
/// <see cref="Gate"/> saying so; the belt itself never decides that. Output is
/// captured, capped, and handed back with the exit code, and a command that
/// outlives the timeout is killed rather than waited for.
/// </summary>
public sealed class ShellToolbelt(string root, TimeSpan timeout) : IToolbelt
{
    public const int MaxOutputChars = 24_000;

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Decides whether a command may run. Null means nothing may: a belt with
    /// no gate refuses everything, which is the safe way to be misconfigured.
    /// </summary>
    public Func<string, CancellationToken, Task<GateVerdict>>? Gate { get; set; }

    public string Scope => $"{ShellName} in the workspace root";

    /// <summary>The shell this machine gets: the one its operator already uses.</summary>
    public static string ShellName => OperatingSystem.IsWindows() ? "PowerShell" : "bash";

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Tool, "run_command", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failure($"unknown tool '{call.Tool}'");

        var command = call.Arg("command").Trim();
        if (command.Length == 0) return ToolResult.Failure("run_command needs a 'command' argument");

        if (Gate is null) return ToolResult.Failure("not run: no approval gate is configured for commands in this session");

        var verdict = await Gate(command, ct);
        if (!verdict.Allowed) return ToolResult.Failure($"not run: {verdict.Reason}");

        return await RunAsync(command, ct);
    }

    private async Task<ToolResult> RunAsync(string command, CancellationToken ct)
    {
        var start = Shell(command);
        start.WorkingDirectory = Root;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;          // and closed at once: nothing here can answer a prompt
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;

        using var process = new Process { StartInfo = start };
        var sw = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ToolResult.Failure($"could not start {ShellName}: {ex.Message}");
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            return ToolResult.Failure(
                $"killed after {timeout.TotalSeconds:0}s (commandTimeoutSeconds) — output so far:\n{Clip(await SafeAsync(stdout))}");
        }

        var output = await SafeAsync(stdout);
        var errors = await SafeAsync(stderr);
        sw.Stop();

        var sb = new StringBuilder();
        sb.Append("exit code ").Append(process.ExitCode).Append(" · ").Append(sw.ElapsedMilliseconds).Append(" ms");
        if (output.Length > 0) sb.Append('\n').Append(Clip(output));
        if (errors.Length > 0) sb.Append("\n[stderr]\n").Append(Clip(errors));

        // A non-zero exit is a failed step for the loop's bookkeeping, but the
        // text still goes back to the model: the error output is what it needs.
        return process.ExitCode == 0 ? ToolResult.Success(sb.ToString()) : ToolResult.Failure(sb.ToString());
    }

    private static ProcessStartInfo Shell(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows PowerShell 5 writes in the console code page; ask it for
            // UTF-8 first, or Korean output comes back as question marks.
            var utf8 = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";
            var exe = Exists("pwsh") ? "pwsh" : "powershell";
            var info = new ProcessStartInfo(exe);
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(utf8 + command);
            return info;
        }

        var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var posix = new ProcessStartInfo(shell);
        posix.ArgumentList.Add("-c");
        posix.ArgumentList.Add(command);
        return posix;
    }

    private static bool Exists(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe))) return true;
        }
        return false;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }

    private static async Task<string> SafeAsync(Task<string> read)
    {
        try { return (await read).TrimEnd(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return ""; }
    }

    private static string Clip(string text) =>
        text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + $"\n… truncated ({text.Length} chars total)";
}
