using Agent.Common.Llm.Tools;
using Agent.Common.Models;
using Agent.Common.Platform;
using Agent.Common.Voice;
using Agent.Common.Voice.KittenTts2;

namespace ZeroCommon.Tests.Models;

/// <summary>
/// When models load: on use and on save always, at startup only per kind and only when switched on (default off),
/// never twice, never blocking the caller. Real model loads are measured by hand (wearable --speak --repeat).
/// </summary>
public sealed class ModelResidencyTests
{
    private static string Scratch(string name) => Directory.CreateTempSubdirectory(name).FullName;

    [Fact]
    public void Startup_preload_is_off_for_every_kind_until_switched_on()
    {
        var root = Scratch("preload");
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            var p = ModelPreloadSettingsStore.Load();
            Assert.False(p.LocalLlm);
            Assert.False(p.VoiceOut);
            Assert.False(p.VoiceIn);

            ModelPreloadSettingsStore.Save(new ModelPreloadSettings { VoiceOut = true });
            var back = ModelPreloadSettingsStore.Load();
            Assert.True(back.VoiceOut);
            Assert.False(back.LocalLlm);

            File.WriteAllText(Path.Combine(root, "model-preload.json"), "{ not json");
            Assert.False(ModelPreloadSettingsStore.Load().VoiceOut);   // a broken file means "nothing at startup"
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Startup_preload_with_everything_off_returns_at_once()
    {
        var root = Scratch("preload-off");
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            var log = new List<string>();
            Assert.True(ModelResidency.PreloadAtStartupAsync(log.Add, TimeSpan.FromSeconds(30)).Wait(TimeSpan.FromSeconds(5)));
            Assert.Empty(log);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_local_llm_is_not_loaded_unless_the_local_backend_is_active()
    {
        var root = Scratch("llm");
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            // Default settings: External backend.
            Assert.False(ModelResidency.EnsureLocalLlmAsync().Result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Voice_warmups_skip_what_is_not_installed()
    {
        var root = Scratch("voice-warm");
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            var log = new List<string>();
            Assert.False(ModelResidency.WarmVoiceOutAsync(new VoiceSettings { TtsProvider = TtsProviderNames.KittenTts2 }, log.Add).Result);
            Assert.False(ModelResidency.WarmVoiceOutAsync(new VoiceSettings { TtsProvider = TtsProviderNames.Off }, log.Add).Result);
            Assert.False(ModelResidency.WarmVoiceInAsync(new VoiceSettings { SttWhisperModel = "tiny" }, log.Add).Result);   // never downloads
            Assert.False(VoiceEngines.Create(new VoiceSettings { TtsProvider = TtsProviderNames.KittenTts2 })!.IsLoaded);
            VoiceEngines.ReleaseAllExcept(TtsProviderNames.Supertonic);   // nothing loaded: a no-op, not a crash
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_deferred_loop_reports_a_backend_that_never_came_up_and_otherwise_delegates()
    {
        var failing = new DeferredAgentLoop(_ => Task.FromResult<IAgentLoop?>(null), "not ready");
        var ex = Assert.Throws<AggregateException>(() => failing.RunAsync("hi").Wait());
        Assert.Equal("not ready", ex.InnerException!.Message);

        var created = 0;
        var inner = new StubLoop();
        var deferred = new DeferredAgentLoop(_ => { created++; return Task.FromResult<IAgentLoop?>(inner); }, "not ready");
        Assert.Equal(0, deferred.UserSendCount);
        deferred.RunAsync("one").Wait();
        deferred.RunAsync("two").Wait();
        Assert.Equal(1, created);                // created once, on the first turn
        Assert.Equal(["one", "two"], inner.Requests);
        Assert.Equal(2, deferred.UserSendCount);
    }

    [Fact]
    public void An_update_removes_files_the_new_manifest_no_longer_lists()
    {
        var dir = Scratch("kt2-orphans");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "decoder"));
            File.WriteAllText(Path.Combine(dir, "decoder", "new.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "decoder", "old_fp16.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "decoder", "big.onnx_data.part"), "x");
            File.WriteAllText(Path.Combine(dir, KittenTts2ModelStore.ManifestFile), "{}");
            var manifest = new KittenTts2Manifest("b", 1, 1f, 24000, ["Bruno"], "decoder/new.onnx",
                [new KittenTts2File("decoder/new.onnx", 1, "")], 1);

            KittenTts2ModelDownloader.RemoveUnlisted(dir, manifest);

            Assert.True(File.Exists(Path.Combine(dir, "decoder", "new.onnx")));
            Assert.False(File.Exists(Path.Combine(dir, "decoder", "old_fp16.onnx")));
            Assert.True(File.Exists(Path.Combine(dir, "decoder", "big.onnx_data.part")));   // left for a resume
            Assert.True(File.Exists(Path.Combine(dir, KittenTts2ModelStore.ManifestFile)));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class StubLoop : IAgentLoop
    {
        public List<string> Requests { get; } = [];
        public int UserSendCount => Requests.Count;

        public Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
        {
            Requests.Add(userRequest);
            return Task.FromResult<AgentLoopRun>(null!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
