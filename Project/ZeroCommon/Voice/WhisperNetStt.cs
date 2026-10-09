using Agent.Common.Llm;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Agent.Common.Voice;

/// <summary>
/// Offline STT through whisper.cpp (Whisper.net) for hosts that are not the WPF one —
/// the Avalonia host on Windows and macOS. Same model files as the WPF host's
/// <c>WhisperLocalStt</c> (<see cref="WhisperModelStore"/>), same download source, same
/// Vulkan → CPU fallback; what differs is the auto GPU pick, which asks Vulkan only,
/// because the WMI fallback the WPF copy uses is Windows-only. The WPF class is left
/// alone (conversion work does not edit that project) and keeps its own name, so a file
/// importing both namespaces never sees an ambiguous type.
///
/// <para>One factory per process, shared by every instance: loading a ggml model is the
/// expensive step and the weights are read-only once loaded.</para>
/// </summary>
public sealed class WhisperNetStt : ISpeechToText
{
    public string ProviderName => SttProviderNames.WhisperLocal;

    private static readonly object Lock = new();
    private static WhisperFactory? _factory;
    private static string? _loadedPath;
    private static bool _loadedGpu;
    private static int _loadedIndex;
    private static readonly GpuLoaderFallback<WhisperFactory> Fallback = new(
        log: msg => AppLogger.Log($"[Voice] Whisper {msg}"));

    private static readonly Dictionary<string, (GgmlType Type, string Size)> Downloads = new()
    {
        ["tiny"] = (GgmlType.Tiny, "~75 MB"),
        ["small"] = (GgmlType.Small, "~466 MB"),
        ["medium"] = (GgmlType.Medium, "~1.5 GB"),
    };

    private readonly string _model;

    public WhisperNetStt(string? modelName = "small") => _model = WhisperModelStore.Normalize(modelName);

    public bool UseGpu { get; set; }

    /// <summary>Vulkan device index; -1 picks the best one Vulkan reports.</summary>
    public int GpuDeviceIndex { get; set; } = -1;

    public static string SizeLabel(string modelName) =>
        Downloads.TryGetValue(WhisperModelStore.Normalize(modelName), out var d) ? d.Size : "";

    /// <summary>Downloads the model when it is missing, then loads it.</summary>
    public async Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var path = WhisperModelStore.ModelPath(_model);
        if (!WhisperModelStore.IsDownloaded(_model))
        {
            var (type, size) = Downloads[_model];
            Directory.CreateDirectory(WhisperModelStore.ModelDirectory);
            var partial = path + ".part";
            progress?.Report($"Downloading Whisper {_model} ({size})…");
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: ct))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 20];
                long total = 0, lastReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (total - lastReport >= 16 << 20)
                    {
                        lastReport = total;
                        progress?.Report($"Downloading Whisper {_model} ({size})… {total >> 20} MB");
                    }
                }
            }
            File.Move(partial, path, overwrite: true);
            progress?.Report($"Whisper {_model} saved: {new FileInfo(path).Length >> 20} MB");
        }
        await Task.Run(() => EnsureLoaded(path, UseGpu, GpuDeviceIndex), ct);
        progress?.Report($"Whisper {_model} ready ({(_loadedGpu ? $"GPU #{_loadedIndex}" : "CPU")}).");
        return true;
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        if (pcm16kMono.Length == 0) return "";
        EnsureLoaded(WhisperModelStore.ModelPath(_model), UseGpu, GpuDeviceIndex);

        var samples = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm16kMono[i * 2] | (pcm16kMono[i * 2 + 1] << 8)) / 32768f;

        // whisper.cpp's default thread count turned a 4 s clip into 25 s of work in the
        // wearable host; ProcessorCount - 1 brought it to ~2 s.
        await using var processor = _factory!.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .Build();

        var text = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
            if (!string.IsNullOrWhiteSpace(segment.Text)) text.Append(segment.Text);
        return text.ToString().Trim();
    }

    private static void EnsureLoaded(string path, bool useGpu, int gpuIndex)
    {
        lock (Lock)
        {
            var wantGpu = useGpu && !Fallback.StickyGpuFailed;
            var index = 0;
            if (wantGpu)
                index = gpuIndex >= 0
                    ? gpuIndex
                    : GpuIndexPicker.PickAuto(VulkanDeviceEnumerator.Enumerate, wmiFallback: () => 0).Index;

            if (_factory is not null && _loadedPath == path && _loadedGpu == wantGpu && _loadedIndex == index) return;
            if (!File.Exists(path)) throw new InvalidOperationException($"Whisper model missing: {path}");

            _factory?.Dispose();
            _factory = null;
            var result = Fallback.Load(
                loader: (gpu, i) =>
                {
                    RuntimeOptions.RuntimeLibraryOrder = gpu ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu] : [RuntimeLibrary.Cpu];
                    return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = gpu, GpuDevice = i });
                },
                requestedUseGpu: useGpu,
                requestedGpuIndex: index);
            _factory = result.Factory;
            _loadedPath = path;
            _loadedGpu = result.UsedGpu;
            _loadedIndex = result.UsedGpuIndex;
            AppLogger.Log($"[Voice] Whisper loaded | model={Path.GetFileName(path)} gpu={result.UsedGpu} device={result.UsedGpuIndex}");
        }
    }
}
