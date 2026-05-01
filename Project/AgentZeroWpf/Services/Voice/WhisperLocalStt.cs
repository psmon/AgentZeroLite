using System.IO;
using Agent.Common;
using Agent.Common.Llm;
using Agent.Common.Voice;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Offline STT via whisper.cpp through Whisper.net. Models live under
/// <c>%USERPROFILE%\.ollama\models\agentzero\whisper\</c> and are downloaded
/// on first use per size. Two runtimes are bundled — CPU and Vulkan — and
/// the loader picks based on <see cref="UseGpu"/>: <c>true</c> probes
/// Vulkan → CPU, <c>false</c> stays on CPU. Vulkan is cross-vendor so a
/// single binary covers AMD/Intel/NVIDIA. CUDA runtime is not bundled
/// (~750 MB of cuBLAS DLLs would balloon the installer); revisit as an
/// on-demand download if benchmark wins justify it.
/// </summary>
public sealed class WhisperLocalStt : ISpeechToText
{
    public string ProviderName => "WhisperLocal";

    private static readonly object Lock = new();
    private static WhisperFactory? _factory;
    private static string? _loadedModelPath;
    private static bool _loadedUseGpu;
    private static int _loadedGpuIndex;
    // Once whisper.cpp's Vulkan backend SEHs during model init we never retry
    // it in this process — re-entering native init after it crashed once tends
    // to corrupt heap and FailFast the host. The fallback policy itself owns
    // the sticky bit; we hold a static instance so the flag survives the lock
    // scope.
    private static readonly GpuLoaderFallback<WhisperFactory> Fallback = new(
        log: msg => AppLogger.Log($"[Voice] Whisper {msg}"));

    // minBytes guards against truncated/aborted downloads — anything smaller is
    // treated as missing and re-fetched.
    private static readonly Dictionary<string, (GgmlType type, string file, string sizeLabel, long minBytes)> Models = new()
    {
        ["tiny"]   = (GgmlType.Tiny,   "ggml-tiny.bin",   "~75 MB",   70_000_000),
        ["small"]  = (GgmlType.Small,  "ggml-small.bin",  "~466 MB", 400_000_000),
        ["medium"] = (GgmlType.Medium, "ggml-medium.bin", "~1.5 GB", 1_400_000_000),
    };

    private readonly string _modelName;

    public WhisperLocalStt(string modelName = "small")
    {
        _modelName = Models.ContainsKey(modelName) ? modelName : "small";
    }

    public bool UseGpu { get; set; }

    /// <summary>
    /// Vulkan device index. <c>-1</c> = auto (pick best via WMI heuristic);
    /// <c>0..N</c> = explicit Vulkan physical-device index. Ignored when
    /// <see cref="UseGpu"/> is false.
    /// </summary>
    public int GpuDeviceIndex { get; set; } = -1;

    public static IReadOnlyList<string> AvailableModels => ["tiny", "small", "medium"];

    public static string GetModelDir()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".ollama", "models", "agentzero", "whisper");
    }

    public static string GetModelPath(string modelName)
    {
        var (_, file, _, _) = Models.GetValueOrDefault(modelName, Models["small"]);
        return Path.Combine(GetModelDir(), file);
    }

    public static bool IsModelDownloaded(string modelName)
    {
        var path = GetModelPath(modelName);
        if (!File.Exists(path)) return false;
        var (_, _, _, minBytes) = Models.GetValueOrDefault(modelName, Models["small"]);
        return new FileInfo(path).Length >= minBytes;
    }

    public async Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var path = GetModelPath(_modelName);
        var backend = UseGpu ? "GPU/Vulkan→CPU" : "CPU";
        if (IsModelDownloaded(_modelName))
        {
            progress?.Report($"Whisper {_modelName} model ready ({backend}).");
            EnsureLoaded(path, UseGpu, GpuDeviceIndex);
            return true;
        }

        if (File.Exists(path)) File.Delete(path);

        var (ggmlType, _, sizeLabel, _) = Models[_modelName];
        Directory.CreateDirectory(GetModelDir());
        progress?.Report($"Downloading Whisper {_modelName} model… ({sizeLabel})");

        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(ggmlType, cancellationToken: ct);
        await using var fileWriter = File.Create(path);
        await modelStream.CopyToAsync(fileWriter, ct);

        progress?.Report($"Whisper model saved: {new FileInfo(path).Length / (1024 * 1024)} MB");
        EnsureLoaded(path, UseGpu, GpuDeviceIndex);
        return true;
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        var path = GetModelPath(_modelName);
        EnsureLoaded(path, UseGpu, GpuDeviceIndex);

        if (_factory is null) throw new InvalidOperationException("Whisper factory unexpectedly null");
        if (pcm16kMono.Length == 0) return "";

        var samples = new float[pcm16kMono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            short sample = (short)(pcm16kMono[i * 2] | (pcm16kMono[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
        }

        await using var processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .Build();

        var sb = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
                sb.Append(segment.Text);
        }
        return sb.ToString().Trim();
    }

    private static void EnsureLoaded(string path, bool useGpu, int gpuIndex)
    {
        lock (Lock)
        {
            // Resolve auto (-1) once per load. Prefer the Vulkan probe over WMI
            // because whisper.cpp/llama.cpp's ggml-vulkan backend uses Vulkan's
            // device order, which is not guaranteed to match Win32's WMI order
            // on hybrid-graphics laptops.
            var effectiveUseGpu = useGpu && !Fallback.StickyGpuFailed;
            int resolvedIndex = 0;
            string indexSource = "cpu";
            string? indexDescription = null;
            if (effectiveUseGpu)
            {
                if (gpuIndex >= 0)
                {
                    resolvedIndex = gpuIndex;
                    indexSource = "explicit";
                }
                else
                {
                    var picked = GpuIndexPicker.PickAuto(
                        vulkanProbe: VulkanDeviceEnumerator.Enumerate,
                        wmiFallback: () => GpuEnumerator.PickBestIndex());
                    resolvedIndex = picked.Index;
                    indexSource = picked.Source;
                    indexDescription = picked.Description;
                }
            }

            if (_factory is not null
                && _loadedModelPath == path
                && _loadedUseGpu == effectiveUseGpu
                && _loadedGpuIndex == resolvedIndex) return;
            if (!File.Exists(path))
                throw new InvalidOperationException($"Whisper model missing: {path}. Call EnsureReadyAsync first.");

            _factory?.Dispose();
            _factory = null;

            var result = Fallback.Load(
                loader: (g, i) => LoadFactory(path, g, i),
                requestedUseGpu: useGpu,
                requestedGpuIndex: resolvedIndex);

            _factory = result.Factory;
            _loadedModelPath = path;
            _loadedUseGpu = result.UsedGpu;
            _loadedGpuIndex = result.UsedGpuIndex;

            var probe = string.Join("→", RuntimeOptions.RuntimeLibraryOrder);
            var deviceLabel = result.UsedGpu
                ? $"device={result.UsedGpuIndex} src={indexSource}{(indexDescription is null ? "" : $" [{indexDescription}]")}"
                : "device=cpu";
            var fallbackTag = (useGpu && !result.UsedGpu) ? " (CPU fallback)" : "";
            AppLogger.Log($"[Voice] Whisper factory loaded{fallbackTag} | model={Path.GetFileName(path)} useGpu={result.UsedGpu} {deviceLabel} probe={probe}");
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

    public static void Unload()
    {
        lock (Lock)
        {
            _factory?.Dispose();
            _factory = null;
            _loadedModelPath = null;
            _loadedUseGpu = false;
            _loadedGpuIndex = 0;
            // Don't reset Fallback's sticky bit — once whisper.cpp's native side
            // has SEHed in this process, the heap is suspect; re-entering Vulkan
            // init from a clean managed-side Unload won't undo that.
        }
    }
}
