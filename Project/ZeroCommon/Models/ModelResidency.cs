using System.Text.Json;
using Agent.Common.Llm;
using Agent.Common.Platform;
using Agent.Common.Voice;

namespace Agent.Common.Models;

/// <summary>
/// Which model kinds load when AgentZero starts. All off by default — a model costs memory (the local LLM and
/// KittenTTS 2 several GB each) and is loaded anyway the first time something uses it. Turn one on to have it ready
/// before the first request. Stored per profile in <c>model-preload.json</c>.
/// </summary>
public sealed class ModelPreloadSettings
{
    /// <summary>The local LLM (Settings → LLM, when the Local backend is active and the model is on disk).</summary>
    public bool LocalLlm { get; set; }

    /// <summary>The voice output engine Settings → Voice names (Supertonic or KittenTTS 2).</summary>
    public bool VoiceOut { get; set; }

    /// <summary>The local ear (Whisper) Settings → Voice names.</summary>
    public bool VoiceIn { get; set; }
}

public static class ModelPreloadSettingsStore
{
    private static string Path => AppPaths.File("model-preload.json");

    public static ModelPreloadSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<ModelPreloadSettings>(File.ReadAllText(Path)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public static void Save(ModelPreloadSettings s) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// When models load, in one place, so no host waits for one it does not need yet and none loads twice:
/// <list type="bullet">
///   <item><b>on use</b> — always: an engine loads on its first request and keeps the model
///   (<see cref="IVoiceEngine"/>, Whisper's factory, <see cref="EnsureLocalLlmAsync"/> for the agent loop);</item>
///   <item><b>on save</b> — Settings → Voice → Save warms what it just picked (<see cref="WarmVoiceAsync"/>) and frees
///   the voice it replaced, so the next request is fast;</item>
///   <item><b>at startup</b> — only the kinds switched on in <see cref="ModelPreloadSettings"/>, on the pool, after the
///   window is up (<see cref="PreloadAtStartupAsync"/>), so starting the app never waits for a model.</item>
/// </list>
/// Every method here returns a task the caller may ignore; none throws (failures are logged).
/// </summary>
public static class ModelResidency
{
    private static readonly object LlmGate = new();
    private static Task<bool>? _llmLoad;

    /// <summary>
    /// Load the kinds <see cref="ModelPreloadSettings"/> switches on, one after another (they compete for the same
    /// disk and GPU), after <paramref name="delay"/> so the first frames paint first.
    /// </summary>
    public static Task PreloadAtStartupAsync(Action<string> log, TimeSpan? delay = null) => Task.Run(async () =>
    {
        var p = ModelPreloadSettingsStore.Load();
        if (!p.LocalLlm && !p.VoiceOut && !p.VoiceIn) return;
        await Task.Delay(delay ?? TimeSpan.FromSeconds(2));
        log($"[Models] startup preload: llm={p.LocalLlm} voiceOut={p.VoiceOut} voiceIn={p.VoiceIn}");
        var voice = VoiceSettingsStore.Load();
        if (p.VoiceIn) await WarmVoiceInAsync(voice, log);
        if (p.VoiceOut) await WarmVoiceOutAsync(voice, log);
        if (p.LocalLlm) await EnsureLocalLlmAsync(log);
    });

    /// <summary>After a Voice Save: free the voice that was replaced, then warm what is chosen now (out, then in).</summary>
    public static Task WarmVoiceAsync(VoiceSettings voice, Action<string> log) => Task.Run(async () =>
    {
        VoiceEngines.ReleaseAllExcept(voice.TtsProvider);
        await WarmVoiceOutAsync(voice, log);
        await WarmVoiceInAsync(voice, log);
    });

    /// <summary>Load the voice output engine's model if it is installed. Never downloads.</summary>
    public static Task<bool> WarmVoiceOutAsync(VoiceSettings voice, Action<string> log) => Task.Run(() =>
    {
        try
        {
            using var engine = VoiceEngines.Create(voice, (_, m) => log($"[Models] {m}"));
            if (engine is not { Available: true }) return false;
            if (engine.IsLoaded) return true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            engine.Warm();
            log($"[Models] voice out {engine.Name} loaded in {sw.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception ex)
        {
            log($"[Models] voice out preload failed: {ex.Message}");
            return false;
        }
    });

    /// <summary>Load the local ear (Whisper) if its model is on disk. Never downloads; a cloud ear needs nothing.</summary>
    public static async Task<bool> WarmVoiceInAsync(VoiceSettings voice, Action<string> log)
    {
        try
        {
            if (voice.SttProvider != SttProviderNames.WhisperLocal || !WhisperModelStore.IsDownloaded(voice.SttWhisperModel))
                return false;
            var stt = PortableVoiceRuntime.CreateStt(voice, out _);
            if (stt is null) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = await stt.EnsureReadyAsync(null).ConfigureAwait(false);
            log($"[Models] voice in whisper-{voice.SttWhisperModel} {(ok ? "loaded" : "not loaded")} in {sw.ElapsedMilliseconds} ms");
            return ok;
        }
        catch (Exception ex)
        {
            log($"[Models] voice in preload failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The local LLM Settings → LLM names, loaded if it is not yet — the agent loop calls this on use, startup
    /// preload calls it when switched on. One load at a time: a second caller awaits the first. False when the
    /// Local backend is not active, the model is not on disk, or the load failed. The load is shared, so it is not
    /// cancellable; a caller that gives up uses <c>WaitAsync(ct)</c>.
    /// </summary>
    public static Task<bool> EnsureLocalLlmAsync(Action<string>? log = null)
    {
        log ??= _ => { };
        lock (LlmGate)
        {
            if (LlmService.State == LlmServiceState.Loaded) return Task.FromResult(true);
            if (_llmLoad is { IsCompleted: false }) return _llmLoad;
            return _llmLoad = Task.Run(async () =>
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return false;
                    var s = LlmSettingsStore.Load();
                    if (s.ActiveBackend != LlmActiveBackend.Local) return false;
                    var entry = LlmModelCatalog.FindById(s.ModelId);
                    var path = LlmModelLocator.ResolveExistingOrTarget(entry);
                    if (!File.Exists(path))
                    {
                        log($"[Models] local LLM {entry.DisplayName} is not on disk — download it in Settings → LLM");
                        return false;
                    }
                    if (LlmService.State == LlmServiceState.Loaded) return true;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    log($"[Models] loading local LLM {entry.DisplayName} ({s.Backend})");
                    await LlmService.LoadAsync(s, path).ConfigureAwait(false);
                    log($"[Models] local LLM loaded in {sw.ElapsedMilliseconds} ms");
                    return true;
                }
                catch (Exception ex)
                {
                    log($"[Models] local LLM load failed: {ex.Message}");
                    return LlmService.State == LlmServiceState.Loaded;
                }
            });
        }
    }
}
