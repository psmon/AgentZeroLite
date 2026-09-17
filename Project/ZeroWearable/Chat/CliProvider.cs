using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ZeroWearable.Chat;

/// <summary>
/// Runs a "chat CLI" as a child process: netclaw, claude, codex, a script. Same
/// idea as amoled_chat_host's provider, reduced to what this host needs and kept
/// AOT-safe - process start plus <see cref="JsonDocument"/>, no reflection-based
/// serialization or DI.
/// </summary>
public sealed class CliProvider(string name, ProviderConfig config, Action<string, string> log)
{
    public string Name => name;
    public string? Description => config.Description;
    public bool UseReplyStyle => config.UseReplyStyle;

    // A chat CLI keeps per-session state on disk (netclaw holds an exclusive lock
    // on its session log), so two overlapping prompts on one session make the
    // second fail outright. One child at a time; preemption means abandoning an
    // answer, not running both.
    private readonly SemaphoreSlim _slot = new(1, 1);

    public async Task<string> AskAsync(string prompt, string? session, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            return await RunAsync(prompt, session, ct);
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task<string> RunAsync(string prompt, string? session, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = config.PromptVia == "stdin",
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // Without this the prompt is written in the console code page (949 here) and
        // a Korean question reaches the CLI as mojibake. No BOM: CLIs read the first
        // bytes as content.
        if (psi.RedirectStandardInput) psi.StandardInputEncoding = new UTF8Encoding(false);
        if (!string.IsNullOrEmpty(config.WorkingDir)) psi.WorkingDirectory = config.WorkingDir;

        var promptInArgs = false;
        foreach (var arg in config.Args)
        {
            if (arg == "{session-args}")
            {
                if (!string.IsNullOrEmpty(session))
                {
                    foreach (var sessionArg in config.SessionArgs)
                        psi.ArgumentList.Add(sessionArg.Replace("{session}", session));
                }
                continue;
            }
            if (arg.Contains("{prompt}")) promptInArgs = true;
            psi.ArgumentList.Add(arg.Replace("{prompt}", prompt).Replace("{session}", session ?? ""));
        }
        if (config.PromptVia == "arg" && !promptInArgs) psi.ArgumentList.Add(prompt);

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException($"could not start {config.Command}");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        if (config.PromptVia == "stdin")
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSec));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                : new TimeoutException($"{name} timed out after {config.TimeoutSec}s");
        }

        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        log("debug", $"{name} exited {process.ExitCode} in {sw.ElapsedMilliseconds}ms, {output.Length} chars");

        if (output.Length == 0)
        {
            throw new InvalidOperationException(error.Length > 0
                ? $"{name}: {Head(error, 200)}"
                : $"{name} produced no output (exit {process.ExitCode})");
        }

        return config.Output == "json" ? ExtractField(output, config.ResponseField) : output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // the child is already gone, which is the outcome we wanted
        }
    }

    /// <summary>Reads a dotted path such as "response" or "result.text" out of JSON stdout.</summary>
    private static string ExtractField(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out var next))
                    return json;  // shape changed: better the raw answer than nothing
                element = next;
            }
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        }
        catch (JsonException)
        {
            return json;  // the CLI printed plain text despite Output=json
        }
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
