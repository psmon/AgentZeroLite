using System.Collections.ObjectModel;
using System.Diagnostics;
using Agent.Common;
using Agent.Common.Llm;
using Agent.Common.Models;
using Agent.Common.Voice;
using Agent.Common.Voice.KittenTts2;
using AgentZeroAvalonia.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>A GPU entry for the Whisper device combo; index -1 is "auto".</summary>
public sealed record GpuChoice(int Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings → Voice. Edits the WPF host's <c>voice-settings.json</c> through
/// <see cref="VoiceSettingsStore"/> — the same file, the same field names — for the
/// providers that run on every OS (<see cref="PortableVoiceRuntime"/>): Whisper.net and
/// OpenAI Whisper for the ear, Supertonic, KittenTTS 2 and OpenAI TTS for the voice. These are also
/// exactly what the wearable host reads, so this page is where its voice and ear get set.
///
/// <para>Two rules keep the shared file honest. A save starts from the file as it is now
/// and writes only the fields this page shows, so the WPF host's mic device, VAD and
/// stream settings survive. And a provider this host cannot run (Windows SAPI, the
/// LocalGemma stub) stays in the picker while it is the stored choice, so opening the
/// page and pressing Save does not quietly switch the WPF host's provider.</para>
///
/// <para>The test is mic-free on purpose: "Speak" synthesises and plays a line, "Round
/// trip" also feeds that audio back through STT and shows what it heard. Microphone
/// capture is the next step (README-Avalonia: AgentBot voice).</para>
/// </summary>
public partial class VoiceSettingsViewModel : ObservableObject
{
    private readonly WavPlayer _player = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> SttProviders { get; } = [.. PortableVoiceRuntime.SttProviders];
    public ObservableCollection<string> TtsProviders { get; } = [.. PortableVoiceRuntime.TtsProviders];
    public IReadOnlyList<string> WhisperModels { get; } = WhisperModelStore.DownloadableModels;
    public IReadOnlyList<string> SttLanguages { get; } = ["auto", "ko", "en", "ja", "zh"];
    public IReadOnlyList<string> SupertonicVoices { get; } = SuperTonicModelStore.BuiltinVoices;
    public IReadOnlyList<string> SupertonicLanguages { get; } = ["ko", "en", "ja", "na"];
    public IReadOnlyList<string> OpenAiVoices { get; } = OpenAiTts.Voices;
    public ObservableCollection<string> Kitten2Voices { get; } = [];
    public IReadOnlyList<string> Kitten2Languages { get; } = ["ko", "en", "ja", "zh"];
    public IReadOnlyList<string> Kitten2DeviceChoices { get; } = Kitten2Devices.All;
    public ObservableCollection<GpuChoice> GpuChoices { get; } = [];

    [ObservableProperty] private string _sttProvider = SttProviderNames.WhisperLocal;
    [ObservableProperty] private string _sttWhisperModel = "small";
    [ObservableProperty] private string _sttLanguage = "auto";
    [ObservableProperty] private bool _sttUseGpu;
    [ObservableProperty] private GpuChoice? _sttGpu;
    [ObservableProperty] private string _sttOpenAIApiKey = "";

    [ObservableProperty] private string _ttsProvider = TtsProviderNames.Off;
    [ObservableProperty] private string _openAiVoice = "alloy";
    [ObservableProperty] private string _ttsOpenAIApiKey = "";
    [ObservableProperty] private string _supertonicVoice = "F1";
    [ObservableProperty] private string _supertonicLanguage = "ko";
    [ObservableProperty] private decimal _supertonicSteps = 8;
    [ObservableProperty] private decimal _supertonicSpeed = 1.05m;
    [ObservableProperty] private string _kitten2Voice = KittenTts2ModelStore.DefaultVoice;
    [ObservableProperty] private string _kitten2Language = "ko";
    [ObservableProperty] private string _kitten2Source = "";
    [ObservableProperty] private string _kitten2Device = Kitten2Devices.Auto;

    [ObservableProperty] private string _testText = "안녕하세요, 에이전트제로입니다.";
    [ObservableProperty] private string _heard = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _whisperState = "";
    [ObservableProperty] private string _supertonicState = "";
    [ObservableProperty] private string _kitten2State = "";
    [ObservableProperty] private string _voiceMemoryState = "";

    /// <summary>Load the voice output / the local ear when AgentZero starts (off by default — first use loads them).</summary>
    [ObservableProperty] private bool _preloadVoiceOut = ModelPreloadSettingsStore.Load().VoiceOut;
    [ObservableProperty] private bool _preloadVoiceIn = ModelPreloadSettingsStore.Load().VoiceIn;

    partial void OnPreloadVoiceOutChanged(bool value) => SavePreload(p => p.VoiceOut = value);
    partial void OnPreloadVoiceInChanged(bool value) => SavePreload(p => p.VoiceIn = value);

    private static void SavePreload(Action<ModelPreloadSettings> change)
    {
        var p = ModelPreloadSettingsStore.Load();
        change(p);
        ModelPreloadSettingsStore.Save(p);
    }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressKnown;

    public bool GpuSupported { get; } = OperatingSystem.IsWindows();
    public bool ShowWhisper => SttProvider == SttProviderNames.WhisperLocal;
    public bool ShowSttKey => SttProvider == SttProviderNames.OpenAIWhisper;
    public bool ShowSupertonic => TtsProvider == TtsProviderNames.Supertonic;
    public bool ShowKitten2 => TtsProvider == TtsProviderNames.KittenTts2;
    public bool ShowOpenAiTts => TtsProvider == TtsProviderNames.OpenAITts;
    public bool SttRunsHere => PortableVoiceRuntime.SttProviders.Contains(SttProvider);
    public bool TtsRunsHere => PortableVoiceRuntime.TtsProviders.Contains(TtsProvider);
    public bool IsIdle => !IsBusy;

    partial void OnSttProviderChanged(string value) => RaiseVisibility();
    partial void OnTtsProviderChanged(string value)
    {
        RaiseVisibility();
        RefreshModelState();
    }

    partial void OnKitten2DeviceChanged(string value) => RefreshModelState();
    partial void OnSttWhisperModelChanged(string value) => RefreshModelState();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowWhisper));
        OnPropertyChanged(nameof(ShowSttKey));
        OnPropertyChanged(nameof(ShowSupertonic));
        OnPropertyChanged(nameof(ShowKitten2));
        OnPropertyChanged(nameof(ShowOpenAiTts));
        OnPropertyChanged(nameof(SttRunsHere));
        OnPropertyChanged(nameof(TtsRunsHere));
    }

    public VoiceSettingsViewModel() : this(VoiceSettingsStore.Load()) { }

    /// <summary>Starts from the given settings instead of the file — tests use it.</summary>
    internal VoiceSettingsViewModel(VoiceSettings stored)
    {
        GpuChoices.Add(new GpuChoice(-1, "Auto (best Vulkan device)"));
        foreach (var d in VulkanDeviceEnumerator.Enumerate())
            GpuChoices.Add(new GpuChoice(d.Index, $"#{d.Index} — {d.Name}{(d.IsDiscrete ? " · discrete" : "")}"));
        Load(stored);
    }

    private void Load(VoiceSettings s)
    {
        Keep(SttProviders, s.SttProvider);
        Keep(TtsProviders, s.TtsProvider);
        SttProvider = s.SttProvider;
        SttWhisperModel = WhisperModelStore.Normalize(s.SttWhisperModel);
        SttLanguage = SttLanguages.Contains(s.SttLanguage) ? s.SttLanguage : "auto";
        SttUseGpu = s.SttUseGpu && GpuSupported;
        SttGpu = GpuChoices.FirstOrDefault(g => g.Index == s.SttGpuDeviceIndex) ?? GpuChoices[0];
        SttOpenAIApiKey = s.SttOpenAIApiKey;
        TtsProvider = s.TtsProvider;
        OpenAiVoice = OpenAiVoices.Contains(s.TtsVoice) ? s.TtsVoice : "alloy";
        TtsOpenAIApiKey = s.TtsOpenAIApiKey;
        // With Supertonic the generic TtsVoice, when set, is the one the WPF host actually
        // speaks with (it overrides SupertonicVoice) — measured: TtsVoice M2 beside
        // SupertonicVoice F1, so the desktop spoke M2 and the watch F1. Show the voice the
        // person hears; Save writes it to both fields.
        SupertonicVoice = SupertonicVoices.Contains(s.TtsVoice) ? s.TtsVoice
            : SupertonicVoices.Contains(s.SupertonicVoice) ? s.SupertonicVoice : "F1";
        SupertonicLanguage = SupertonicLanguages.Contains(s.SupertonicLanguage) ? s.SupertonicLanguage : "ko";
        SupertonicSteps = Math.Clamp(s.SupertonicSteps, 5, 12);
        SupertonicSpeed = (decimal)Math.Clamp(s.SupertonicSpeed, 0.7f, 2.0f);
        RefreshKitten2Voices(s.Kitten2Voice);
        Kitten2Language = Kitten2Languages.Contains(s.Kitten2Language) ? s.Kitten2Language : "ko";
        Kitten2Device = Kitten2Devices.Normalize(s.Kitten2Device);
        RefreshModelState();
    }

    /// <summary>The voices the installed KittenTTS 2 bundle carries (its manifest), keeping <paramref name="wanted"/> selected.</summary>
    private void RefreshKitten2Voices(string? wanted)
    {
        Kitten2Voices.Clear();
        foreach (var v in KittenTts2ModelStore.Voices(KittenTts2ModelStore.DefaultDirectory)) Kitten2Voices.Add(v);
        Kitten2Voice = Kitten2Voices.FirstOrDefault(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase))
                       ?? Kitten2Voices.FirstOrDefault() ?? KittenTts2ModelStore.DefaultVoice;
    }

    /// <summary>A stored provider this host cannot run stays selectable, so Save does not change it behind the WPF host's back.</summary>
    private static void Keep(ObservableCollection<string> options, string stored)
    {
        if (!string.IsNullOrWhiteSpace(stored) && !options.Contains(stored)) options.Add(stored);
    }

    private void RefreshModelState()
    {
        // What is in memory right now - the next request pays no load for it.
        using (var engine = VoiceEngines.Create(new VoiceSettings
               {
                   TtsProvider = TtsProvider, Kitten2Device = Kitten2Device,
                   SupertonicModelDir = VoiceSettingsStore.Load().SupertonicModelDir,
               }))
            VoiceMemoryState = engine is null ? ""
                : !engine.Available ? $"{engine.Name}: not installed"
                : engine.IsLoaded ? $"{engine.Name}: loaded — requests start at once"
                : $"{engine.Name}: not loaded — Save (or the first request) loads it";
        WhisperState = WhisperModelStore.IsDownloaded(SttWhisperModel)
            ? $"installed · {WhisperModelStore.ModelPath(SttWhisperModel)}"
            : $"not installed ({WhisperNetStt.SizeLabel(SttWhisperModel)}) — Download or Save downloads it";
        var dir = SuperTonicModelStore.DefaultModelDirectory;
        SupertonicState = SuperTonicModelStore.IsModelPresent(dir) ? $"installed · {dir}" : "not installed (~398 MB)";
        var k2 = KittenTts2ModelStore.DefaultDirectory;
        Kitten2State = !OperatingSystem.IsWindows() ? "Windows only (the speech LM runs on the app's llama.cpp natives)"
            : KittenTts2ModelStore.IsPresent(k2) ? $"installed · {k2} · multilingual (Korean included)"
            : $"not installed ({KittenTts2ModelStore.SizeLabel}) — Download from GitHub, or Install from a folder holding the bundle";
    }

    /// <summary>The file as it is now, with this page's fields written over it.</summary>
    private VoiceSettings Collect() => ApplyTo(VoiceSettingsStore.Load());

    /// <summary>Writes this page's fields over <paramref name="s"/> and leaves every other field as it was.</summary>
    internal VoiceSettings ApplyTo(VoiceSettings s)
    {
        s.SttProvider = SttProvider;
        s.SttWhisperModel = SttWhisperModel;
        s.SttLanguage = SttLanguage;
        s.SttUseGpu = SttUseGpu;
        s.SttGpuDeviceIndex = SttGpu?.Index ?? -1;
        s.SttOpenAIApiKey = SttOpenAIApiKey.Trim();
        s.TtsProvider = TtsProvider;
        s.TtsOpenAIApiKey = TtsOpenAIApiKey.Trim();
        s.SupertonicVoice = SupertonicVoice;
        s.SupertonicLanguage = SupertonicLanguage;
        s.SupertonicSteps = (int)SupertonicSteps;
        s.SupertonicSpeed = (float)SupertonicSpeed;
        s.Kitten2Voice = Kitten2Voice;
        s.Kitten2Language = Kitten2Language;
        s.Kitten2Device = Kitten2Device;
        // The generic voice field overrides Supertonic's own when it is set, so keep the
        // two in step; for OpenAI it is the voice itself.
        if (TtsProvider == TtsProviderNames.Supertonic) s.TtsVoice = SupertonicVoice;
        else if (TtsProvider == TtsProviderNames.OpenAITts) s.TtsVoice = OpenAiVoice;
        return s;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var saved = Collect();
            VoiceSettingsStore.Save(saved);
            // Keep what was just chosen loaded (and free the voice it replaced), so the test, plugins and AI mode
            // answer without a load. In the background: the page stays responsive while KittenTTS 2 loads (~13 s).
            var name = saved.TtsProvider;
            var onDevice = VoiceEngines.OnDevice.Contains(name);
            Status = onDevice
                ? $"Saved. Loading {name} in the background so it stays ready… (the wearable host reads settings on its next start)"
                : "Saved. The wearable host reads this on its next start.";
            _ = ModelResidency.WarmVoiceAsync(saved, m => AppLogger.Log(m)).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                RefreshModelState();
                if (onDevice && Status.StartsWith("Saved. Loading", StringComparison.Ordinal))
                    Status = $"Saved. {name} is loaded and stays in memory. The wearable host reads settings on its next start.";
            }));
        }
        catch (Exception ex) { Status = "Save failed: " + ex.Message; }
    }

    [RelayCommand]
    private Task DownloadWhisperAsync() => RunAsync("Whisper", async ct =>
    {
        var stt = new WhisperNetStt(SttWhisperModel) { UseGpu = SttUseGpu, GpuDeviceIndex = SttGpu?.Index ?? -1 };
        await stt.EnsureReadyAsync(new Progress<string>(m => Status = m), ct);
    });

    [RelayCommand]
    private Task DownloadSupertonicAsync() => RunAsync("Supertonic", async ct =>
    {
        var ok = await SuperTonicModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(p =>
        {
            ProgressKnown = p.PercentComplete is not null;
            Progress = p.PercentComplete ?? 0;
            Status = $"{p.Caption} — {p.Detail}";
        }), ct);
        Status = ok ? "Supertonic model installed." : "Supertonic download did not finish.";
    });

    /// <summary>KittenTTS 2 from AgentZero's GitHub Release (model-kittentts2-v1): resumes, checks every SHA-256.</summary>
    [RelayCommand]
    private Task DownloadKitten2Async() => RunAsync("KittenTTS 2", async ct =>
    {
        var ok = await KittenTts2ModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(p =>
        {
            ProgressKnown = p.PercentComplete is not null;
            Progress = p.PercentComplete ?? 0;
            Status = $"{p.Caption} — {p.Detail}";
        }), ct);
        RefreshKitten2Voices(Kitten2Voice);
        if (ok) Status = "KittenTTS 2 downloaded.";
    });

    /// <summary>
    /// Install KittenTTS 2 from a folder holding the bundle (manifest.json + files), checking every file's SHA-256 —
    /// a synced cloud folder or a copy handed over.
    /// </summary>
    [RelayCommand]
    private Task InstallKitten2Async() => RunAsync("KittenTTS 2", async ct =>
    {
        var source = Kitten2Source.Trim().Trim('"');
        if (source.Length == 0) throw new InvalidOperationException("enter the folder that holds the bundle (manifest.json)");
        var ok = await KittenTts2ModelInstaller.InstallFromFolderAsync(source, new Progress<ModelDownloadStatus>(p =>
        {
            ProgressKnown = p.PercentComplete is not null;
            Progress = p.PercentComplete ?? 0;
            Status = $"{p.Caption} — {p.Detail}";
        }), ct);
        RefreshKitten2Voices(Kitten2Voice);
        if (ok) Status = "KittenTTS 2 installed.";
    });

    [RelayCommand]
    private Task SpeakAsync() => RunAsync("Speak", async ct =>
    {
        var s = Collect();
        var (wav, ms) = await SynthesizeAsync(s, ct);
        Status = $"{s.TtsProvider} · {ms} ms · {WavPcm.Decode(wav).DurationSeconds:0.0} s of audio — playing";
        await _player.PlayAsync(wav, ct);
        Status = $"{s.TtsProvider} · synthesised in {ms} ms · played.";
    });

    /// <summary>TTS → playback → STT on the same audio: both halves of the voice, no microphone.</summary>
    [RelayCommand]
    private Task RoundTripAsync() => RunAsync("Round trip", async ct =>
    {
        var s = Collect();
        var stt = PortableVoiceRuntime.CreateStt(s, out var why) ?? throw new InvalidOperationException("STT: " + why);
        var (wav, ttsMs) = await SynthesizeAsync(s, ct);
        var play = _player.PlayAsync(wav, ct);
        Status = "Loading STT…";
        await stt.EnsureReadyAsync(new Progress<string>(m => Status = m), ct);
        var sw = Stopwatch.StartNew();
        var language = s.SttLanguage != "auto" ? s.SttLanguage
            : s.TtsProvider == TtsProviderNames.Supertonic ? s.SupertonicLanguage
            : s.TtsProvider == TtsProviderNames.KittenTts2 ? s.Kitten2Language
            : "auto";
        var pcm = WavPcm.To16kMono(wav);
        var text = await Task.Run(() => stt.TranscribeAsync(pcm, language == "na" ? "auto" : language, ct), ct);
        Heard = string.IsNullOrWhiteSpace(text) ? "(nothing recognised)" : text;
        Status = $"TTS {ttsMs} ms · STT {sw.ElapsedMilliseconds} ms ({stt.ProviderName}).";
        await play;
    });

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        _player.Stop();
    }

    private async Task<(byte[] Wav, long Ms)> SynthesizeAsync(VoiceSettings s, CancellationToken ct)
    {
        var tts = PortableVoiceRuntime.CreateTts(s, out var why) ?? throw new InvalidOperationException("TTS: " + why);
        try
        {
            Status = tts is VoiceEngineTts { Engine.IsLoaded: false }
                ? $"Loading {tts.ProviderName} (first use; it then stays loaded)…"
                : $"Synthesising with {tts.ProviderName}…";
            var sw = Stopwatch.StartNew();
            var text = TtsTextCleaner.StripMarkdown(TestText);
            var wav = await tts.SynthesizeAsync(string.IsNullOrWhiteSpace(text) ? "Hello." : text, PortableVoiceRuntime.VoiceFor(s), ct);
            return (wav, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (tts is IAsyncDisposable d) await d.DisposeAsync();
        }
    }

    private async Task RunAsync(string what, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressKnown = false;
        Progress = 0;
        _cts = new CancellationTokenSource();
        try
        {
            // Stays on the UI thread between awaits: the providers do their heavy work on
            // the pool themselves, and Progress<T> built here posts its reports back here,
            // which is where bound properties must change.
            await work(_cts.Token);
        }
        catch (OperationCanceledException) { Status = what + " stopped."; }
        catch (Exception ex)
        {
            AppLogger.Log($"[Voice] {what} failed: {ex}");
            Status = $"{what} failed: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            RefreshModelState();
        }
    }
}
