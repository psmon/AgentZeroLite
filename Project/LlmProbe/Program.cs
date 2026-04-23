using System.Diagnostics;
using System.Text.Json;
using Agent.Common;
using Agent.Common.Llm;

// Forward AppLogger (including llama.cpp native log callback hooked by
// LlamaSharpLocalLlm) to stderr so the probe's caller sees the real failure
// reasons instead of the LLamaSharp wrapper exception alone.
AppLogger.EnableConsoleOutput();

// LlmProbe — minimal harness for exercising LLamaSharp / llama.cpp under
// different backend + env-var combinations. Invoked by the test suite as a
// subprocess so that native AV crashes (Vulkan CreateContext, etc.) surface
// as a non-zero exit code rather than killing the xUnit test host.
//
// Usage:
//   LlmProbe.exe <backend> <phase> [modelPath]
//     backend:  Cpu | Vulkan
//     phase:    load | session | complete
//     modelPath: optional, falls back to LlmModelLocator
//
// Env vars passed in by the caller (GGML_VK_*, etc.) take effect because
// LlamaSharpLocalLlm sets them before native init.
//
// Output contract (stdout, last line only — stderr is free-form llama.cpp log):
//   {"ok":bool,"phase":"load|session|complete","reply":"...","error":"..."}
// Exit codes:
//   0  success
//   2  managed exception
//   >= 128 native crash (OS-assigned)

string phase = "";
string? reply = null;
string? error = null;

try
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: LlmProbe <backend> <phase> [modelPath]");
        return 64;
    }

    var backend = Enum.Parse<LocalLlmBackend>(args[0], ignoreCase: true);
    phase = args[1].ToLowerInvariant();
    var modelPath = args.Length >= 3 ? args[2] : LlmModelLocator.ResolveExistingOrTarget();

    if (!File.Exists(modelPath))
    {
        error = $"model not found: {modelPath}";
        Emit(ok: false);
        return 66;
    }

    var settings = new LlmRuntimeSettings
    {
        Backend = backend,
        ContextSize = 2048,
        MaxTokens = 16,
        Temperature = 0.1f,
        GpuLayerCount = 999,
        VulkanDeviceIndex = backend == LocalLlmBackend.Vulkan
            ? VulkanDeviceEnumerator.PickDefaultIndex(VulkanDeviceEnumerator.Enumerate())
            : -1,
        FlashAttention = true,
        NoKqvOffload = Environment.GetEnvironmentVariable("PROBE_NO_KQV") == "0" ? false
                       : backend == LocalLlmBackend.Vulkan,
        UseMemoryMap = false
    };

    Console.Error.WriteLine($"[probe] Load backend={backend} vkDev={settings.VulkanDeviceIndex} ctx={settings.ContextSize}");
    var sw = Stopwatch.StartNew();
    await LlmService.LoadAsync(settings, modelPath);
    Console.Error.WriteLine($"[probe] LOAD_OK in {sw.ElapsedMilliseconds}ms");

    if (phase == "load") { Emit(ok: true); return 0; }

    await using var session = LlmService.OpenSession();
    Console.Error.WriteLine($"[probe] SESSION_OK in {sw.ElapsedMilliseconds}ms (cumulative)");

    if (phase == "session") { Emit(ok: true); return 0; }

    if (phase == "reload")
    {
        // Verify whether Unload → Load → Session works cleanly in the same
        // process. Historically guarded by _hasLoadedOnceInProcess because
        // the iGPU/bfloat16 crash fallout corrupted native state. Now that
        // those root causes are patched, re-validate.
        await session.DisposeAsync();
        Console.Error.WriteLine("[probe] RELOAD step 1: session disposed");

        await LlmService.UnloadAsync();
        Console.Error.WriteLine("[probe] RELOAD step 2: UnloadAsync done");

        await LlmService.LoadAsync(settings, modelPath);
        Console.Error.WriteLine("[probe] RELOAD step 3: LoadAsync round-2 done");

        await using var s2 = LlmService.OpenSession();
        Console.Error.WriteLine("[probe] RELOAD step 4: session re-opened");

        var r = await s2.SendAsync("Reply: ok");
        reply = $"round2-reply={r.Trim()}";
        Console.Error.WriteLine($"[probe] RELOAD_OK {reply}");
        Emit(ok: true);
        return 0;
    }

    if (phase == "bench")
    {
        // Sustained-generation benchmark. Close the default short-MaxTokens
        // session and open a fresh one dedicated to a longer decode so we
        // measure real throughput rather than being capped by the 16-token
        // ceiling used for fast `load`/`session`/`complete` smoke tests.
        await session.DisposeAsync();

        // Rebuild options with a benchmark-sized MaxTokens and reload.
        await LlmService.UnloadAsync();
        settings.MaxTokens = 128;
        await LlmService.LoadAsync(settings, modelPath);
        var benchSession = LlmService.OpenSession();

        // Warm-up generation (not measured) — primes shader / KV caches.
        _ = await benchSession.SendAsync("Say: ready");

        var sw2 = Stopwatch.StartNew();
        int tokenCount = 0;
        await foreach (var _ in benchSession.SendStreamAsync(
            "Write a short poem about algorithms. Six lines. Just the poem, no preamble."))
        {
            tokenCount++;
        }
        sw2.Stop();
        await benchSession.DisposeAsync();

        var tps = tokenCount / sw2.Elapsed.TotalSeconds;
        reply = $"tokens={tokenCount} elapsed={sw2.Elapsed.TotalSeconds:0.00}s tps={tps:0.0}";
        Console.Error.WriteLine($"[probe] BENCH_OK {reply}");
        Emit(ok: true);
        return 0;
    }

    var text = await session.SendAsync("Reply with only the single word: hello");
    reply = text;
    Console.Error.WriteLine($"[probe] COMPLETE_OK reply={text}");
    Emit(ok: true);
    return 0;
}
catch (Exception ex)
{
    error = ex.GetType().Name + ": " + ex.Message;
    Console.Error.WriteLine($"[probe] MANAGED_ERROR: {ex}");
    Emit(ok: false);
    return 2;
}

void Emit(bool ok)
{
    var json = JsonSerializer.Serialize(new { ok, phase, reply, error });
    Console.WriteLine(json);
}
