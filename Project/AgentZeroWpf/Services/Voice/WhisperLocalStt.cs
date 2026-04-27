using System.IO;
using Agent.Common;
using Agent.Common.Voice;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Offline STT via whisper.cpp through Whisper.net. Models live under
/// <c>%USERPROFILE%\.ollama\models\agentzero\whisper\</c> and are downloaded
/// on first use per size. Lite ships CPU runtime only — the GPU flag is
/// preserved on the settings tab for forward compatibility but currently
/// always falls back to CPU (Whisper.net.Runtime.Cuda intentionally not
/// referenced to keep the installer slim). Origin's CUDA helper is dropped
/// here for the same reason; we'll bring it back when the matching runtime
/// package is in.
/// </summary>
public sealed class WhisperLocalStt : ISpeechToText
{
    public string ProviderName => "WhisperLocal";

    private static readonly object Lock = new();
    private static WhisperFactory? _factory;
    private static string? _loadedModelPath;

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
        if (IsModelDownloaded(_modelName))
        {
            progress?.Report($"Whisper {_modelName} model ready (CPU).");
            EnsureLoaded(path);
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
        EnsureLoaded(path);
        return true;
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        var path = GetModelPath(_modelName);
        EnsureLoaded(path);

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

    private static void EnsureLoaded(string path)
    {
        lock (Lock)
        {
            if (_factory is not null && _loadedModelPath == path) return;
            if (!File.Exists(path))
                throw new InvalidOperationException($"Whisper model missing: {path}. Call EnsureReadyAsync first.");

            _factory?.Dispose();
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            _factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false });
            _loadedModelPath = path;
            AppLogger.Log($"[Voice] Whisper factory loaded | model={Path.GetFileName(path)} runtime=CPU");
        }
    }

    public static void Unload()
    {
        lock (Lock)
        {
            _factory?.Dispose();
            _factory = null;
            _loadedModelPath = null;
        }
    }
}
