using System.Diagnostics;
using System.Text;
using Agent.Common.Llm;
using Agent.Common.Voice;
using Agent.Common.Wearable;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace ZeroWearable.Voice;

/// <summary>
/// The watch's ear — <b>AgentZero's</b> ear.
///
/// Offline speech-to-text through whisper.cpp (Whisper.net), against the very ggml model
/// the app already installed: size, language and the Vulkan/CPU choice all come from
/// Settings → Voice, and <see cref="WhisperModelStore"/> resolves the same file the GUI's
/// <c>WhisperLocalStt</c> loads. It never downloads anything — a 466 MB fetch is not
/// something a watch question should trigger. With the model absent
/// <see cref="Available"/> is false and the device is told it has no STT up front, rather
/// than failing an utterance halfway through.
///
/// Two findings from the reference host are preserved because they cost real debugging:
/// <list type="bullet">
///   <item>whisper.cpp's default thread count turned a 4.1 s capture into 25.6 s of work.
///     Pinned to <c>ProcessorCount - 1</c> with the language fixed rather than
///     auto-detected it is 2.3 s cold, 0.24 s warm.</item>
///   <item>On a quiet capture whisper invents text (<c>[구독 / 좋아요]</c>,
///     <c>[감사합니다]</c>) which the watch would then send to the model as a question. The
///     level gate stops that before the audio is ever loaded.</item>
/// </list>
/// </summary>
public sealed class WearableStt : IDisposable
{
    private readonly VoiceSettings _voice;
    private readonly WearableSettings _settings;
    private readonly Action<string, string> _log;
    private readonly string _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private readonly GpuLoaderFallback<WhisperFactory> _fallback;
    private WhisperFactory? _factory;

    public WearableStt(VoiceSettings voice, WearableSettings settings, Action<string, string> log)
    {
        _voice = voice;
        _settings = settings;
        _log = log;
        _model = WhisperModelStore.Normalize(voice.SttWhisperModel);
        _fallback = new GpuLoaderFallback<WhisperFactory>(message => log("warn", $"whisper {message}"));
    }

    public string ModelPath => WhisperModelStore.ModelPath(_model);

    /// <summary>
    /// The watch's microphone works only on the offline provider: the cloud ones
    /// (OpenAI Whisper, Webnori Gemma) would ship a stranger's room audio off the machine,
    /// which is not a decision this host makes on the user's behalf.
    /// </summary>
    public bool Available =>
        string.Equals(_voice.SttProvider, SttProviderNames.WhisperLocal, StringComparison.OrdinalIgnoreCase) &&
        WhisperModelStore.IsDownloaded(_model);

    public string Status =>
        !string.Equals(_voice.SttProvider, SttProviderNames.WhisperLocal, StringComparison.OrdinalIgnoreCase)
            ? $"Settings → Voice → STT is '{_voice.SttProvider}'; the watch mic needs WhisperLocal"
            : Available
                ? $"whisper-{_model} ({ModelPath})"
                : $"model not installed at {ModelPath} — Settings → Voice → Download Model";

    public string ProviderName => $"whisper-{_model}";

    /// <summary>Warm-up so the first utterance does not pay the model load.</summary>
    public void Preload()
    {
        if (!Available) return;
        _ = Task.Run(() =>
        {
            try
            {
                EnsureLoaded();
            }
            catch (Exception ex)
            {
                _log("warn", $"preload failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 16 kHz mono PCM16 in, text out. Whisper.net only exposes segments as an
    /// IAsyncEnumerable, so this is async; callers still run it off the actor thread
    /// because the work itself is CPU-bound.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string? language = null,
        CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"stt unavailable: {Status}");
        if (pcm16kMono.Length < 3200) return "";   // under 0.1 s: nothing was said

        var maxBytes = Math.Max(1, _settings.MaxCaptureSeconds) * 16000 * 2;
        if (pcm16kMono.Length > maxBytes)
        {
            _log("warn", $"capture of {pcm16kMono.Length / 32000.0:F1} s truncated to " +
                         $"{_settings.MaxCaptureSeconds} s");
            pcm16kMono = pcm16kMono[..maxBytes];
        }

        var (peakDb, rmsDb) = Levels(pcm16kMono);
        if (peakDb < _settings.SilencePeakDb || rmsDb < _settings.SilenceRmsDb)
        {
            _log("info", $"capture is silence (peak {peakDb:F1} dBFS, rms {rmsDb:F1} dBFS), not transcribing");
            return "";
        }

        var factory = EnsureLoaded();
        var samples = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm16kMono[i * 2] | (pcm16kMono[i * 2 + 1] << 8)) / 32768f;

        var lang = string.IsNullOrWhiteSpace(language) ? _voice.SttLanguage : language;
        if (string.IsNullOrWhiteSpace(lang)) lang = "auto";

        await _gate.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var threads = Math.Max(1, Environment.ProcessorCount - 1);
            await using var processor = factory.CreateBuilder()
                .WithLanguage(lang)
                .WithThreads(threads)
                .Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                if (!string.IsNullOrWhiteSpace(segment.Text)) text.Append(segment.Text);

            var result = text.ToString().Trim();
            _log("info", $"transcribed {samples.Length / 16000.0:F1} s in {sw.ElapsedMilliseconds} ms " +
                         $"({threads} threads, lang {lang}): {(result.Length == 0 ? "(silence)" : result)}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Peak and RMS in dBFS: enough to tell "nobody spoke" from "something was said".</summary>
    public static (double peakDb, double rmsDb) Levels(byte[] pcm16)
    {
        if (pcm16.Length < 2) return (-120, -120);

        var peak = 0;
        double sum = 0;
        var count = pcm16.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            var abs = Math.Abs((int)sample);
            if (abs > peak) peak = abs;
            sum += (double)sample * sample;
        }
        var rms = Math.Sqrt(sum / count);
        return (Db(peak), Db(rms));

        static double Db(double value) => value < 1 ? -120 : 20 * Math.Log10(value / 32768.0);
    }

    private WhisperFactory EnsureLoaded()
    {
        if (_factory is not null) return _factory;
        lock (_loadLock)
        {
            if (_factory is not null) return _factory;

            var path = ModelPath;
            var sw = Stopwatch.StartNew();
            _log("info", $"loading {Path.GetFileName(path)}");

            // Same GPU policy as the GUI: Vulkan is cross-vendor, and one SEH inside
            // whisper.cpp's Vulkan init sets the sticky bit so this process never
            // re-enters it. Index -1 = auto, resolved by probing Vulkan itself (the
            // ggml-vulkan backend uses Vulkan's device order, which is not Win32's).
            var useGpu = _voice.SttUseGpu;
            var index = 0;
            if (useGpu)
            {
                index = _voice.SttGpuDeviceIndex >= 0
                    ? _voice.SttGpuDeviceIndex
                    // No WMI here (System.Management is a WPF-side dependency); with no
                    // Vulkan device the probe returns 0, which is also the right answer.
                    : GpuIndexPicker.PickAuto(VulkanDeviceEnumerator.Enumerate, () => 0).Index;
            }

            var result = _fallback.Load(
                loader: (gpu, i) => LoadFactory(path, gpu, i),
                requestedUseGpu: useGpu,
                requestedGpuIndex: index);

            _factory = result.Factory;
            var device = result.UsedGpu ? $"vulkan:{result.UsedGpuIndex}" : "cpu";
            var note = useGpu && !result.UsedGpu ? " (CPU fallback)" : "";
            _log("info", $"whisper ready in {sw.ElapsedMilliseconds} ms on {device}{note}");
            return _factory;
        }
    }

    private static WhisperFactory LoadFactory(string path, bool useGpu, int gpuIndex)
    {
        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];
        return WhisperFactory.FromPath(path, new WhisperFactoryOptions
        {
            UseGpu = useGpu,
            GpuDevice = gpuIndex,
        });
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}
