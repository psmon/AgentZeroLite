using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

// Subprocess-based tests for LlamaSharp integration. Each test spawns the
// standalone LlmProbe binary so that native Vulkan AV crashes translate to a
// non-zero exit code instead of killing the xUnit test host — and so that the
// once-per-process NativeLibraryConfig doesn't force us to pick a single
// backend for the entire test assembly.
[Trait("Category", "LlmProbe")]
public sealed class LlmProbeTests
{
    private readonly ITestOutputHelper _output;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("GEMMA_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    public LlmProbeTests(ITestOutputHelper output) => _output = output;

    private static string ProbeExePath()
    {
        var here = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(here,
            "..", "..", "..", "..", "LlmProbe", "bin", "Debug", "net10.0", "LlmProbe.exe"));
        return candidate;
    }

    private sealed record ProbeResult(bool ok, string? phase, string? reply, string? error);

    private sealed record ProbeRun(int ExitCode, ProbeResult? Json, string Stderr);

    private async Task<ProbeRun> RunProbe(string backend, string phase, TimeSpan timeout)
    {
        var exe = ProbeExePath();
        Skip.IfNot(File.Exists(exe), $"LlmProbe.exe not built at {exe} — build Project/LlmProbe first.");
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var psi = new ProcessStartInfo(exe, $"{backend} {phase}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var finished = proc.WaitForExit((int)timeout.TotalMilliseconds);
        if (!finished)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"probe exceeded {timeout}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        ProbeResult? json = null;
        var lastLine = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(lastLine))
        {
            try { json = JsonSerializer.Deserialize<ProbeResult>(lastLine,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
        }

        _output.WriteLine($"probe exit={proc.ExitCode} phase={phase} backend={backend}");
        _output.WriteLine($"stderr (last 600 chars): ...{stderr[Math.Max(0, stderr.Length - 600)..]}");
        return new ProbeRun(proc.ExitCode, json, stderr);
    }

    [SkippableFact]
    public async Task Cpu_backend_loads_session_and_generates_token()
    {
        var r = await RunProbe("Cpu", "complete", TimeSpan.FromMinutes(2));
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Json?.ok, $"probe reported failure: {r.Json?.error}");
        Assert.False(string.IsNullOrWhiteSpace(r.Json?.reply), "expected non-empty reply");
    }

    // Regression guard for the full triage path:
    //  - GGML_VK_DISABLE_BFLOAT16 propagating through _putenv_s to the CRT
    //  - CreateContext completing (Session open)
    //  - First token streamed
    // If this breaks, `Docs/gemma4-gpu-load-failures.md` (failure #2/#6) should
    // be the first reference for re-diagnosis.
    [SkippableFact]
    public async Task Vulkan_backend_loads_session_and_generates_token()
    {
        var r = await RunProbe("Vulkan", "complete", TimeSpan.FromMinutes(2));
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Json?.ok, $"probe reported failure: {r.Json?.error}\nstderr tail: {r.Stderr[Math.Max(0, r.Stderr.Length - 400)..]}");
        Assert.False(string.IsNullOrWhiteSpace(r.Json?.reply), "expected non-empty reply");
    }
}
