namespace Agent.Common.Voice;

/// <summary>
/// Path convention + presence checks for the Whisper GGML bundles, factored out of
/// <c>AgentZeroWpf.Services.Voice.WhisperLocalStt</c> so a second process can find the
/// <i>same</i> models without the Whisper.net runtime being in scope. Mirrors
/// <see cref="SuperTonicModelStore"/>.
///
/// <para>The directory is <c>%USERPROFILE%\.ollama\models\agentzero\whisper\</c> rather
/// than the <c>models\</c> root the other on-device bundles use — that is where the models
/// were installed before the convention settled, and moving them would strand every
/// existing install's 466 MB download.</para>
///
/// <para><c>minBytes</c> guards against truncated/aborted downloads: anything smaller is
/// treated as missing rather than handed to whisper.cpp, which would fail deep inside the
/// native loader.</para>
/// </summary>
public static class WhisperModelStore
{
    /// <summary>ggml file name + the smallest size a complete download can have.</summary>
    public static readonly IReadOnlyDictionary<string, (string File, long MinBytes)> Models =
        new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny"] = ("ggml-tiny.bin", 70_000_000),
            ["base"] = ("ggml-base.bin", 130_000_000),
            ["small"] = ("ggml-small.bin", 400_000_000),
            ["medium"] = ("ggml-medium.bin", 1_400_000_000),
        };

    /// <summary>Sizes the app offers for download. <c>base</c> is resolvable but not offered.</summary>
    public static readonly IReadOnlyList<string> DownloadableModels = ["tiny", "small", "medium"];

    public static string ModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ollama", "models", "agentzero", "whisper");

    /// <summary>Unknown names resolve to <c>small</c> — the size the app defaults to.</summary>
    public static string Normalize(string? modelName)
        => modelName is not null && Models.ContainsKey(modelName) ? modelName : "small";

    public static string ModelPath(string modelName)
        => Path.Combine(ModelDirectory, Models[Normalize(modelName)].File);

    /// <summary>True when the file exists and is at least <c>minBytes</c> long.</summary>
    public static bool IsDownloaded(string modelName)
    {
        var path = ModelPath(modelName);
        if (!File.Exists(path)) return false;
        return new FileInfo(path).Length >= Models[Normalize(modelName)].MinBytes;
    }
}
