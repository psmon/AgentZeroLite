using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Common.Platform;

namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// One file of a KittenTTS 2 bundle, as <c>manifest.json</c> lists it. A large file may be published as
/// <see cref="Parts"/> release assets of <see cref="PartSize"/> bytes (the last one shorter) — measured: single
/// uploads of 100 MB+ were cut mid-stream ("tls: bad record MAC") while 40 MB pieces went through; the downloader
/// joins them and checks the whole file's <see cref="Sha256"/>.
/// </summary>
public sealed record KittenTts2File(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("parts")] int Parts = 0,
    [property: JsonPropertyName("part_size")] long PartSize = 0);

/// <summary><c>manifest.json</c> of a bundle: what it holds and how to check it.</summary>
public sealed record KittenTts2Manifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("alpha")] float Alpha,
    [property: JsonPropertyName("sample_rate")] int SampleRate,
    [property: JsonPropertyName("voices")] IReadOnlyList<string> Voices,
    [property: JsonPropertyName("decoder")] string Decoder,
    [property: JsonPropertyName("files")] IReadOnlyList<KittenTts2File> Files,
    [property: JsonPropertyName("total_bytes")] long TotalBytes);

/// <summary>
/// Where the KittenTTS 2 bundle lives and whether it is whole. The bundle is a research build of KittenML's
/// kitten-tts-2 for AgentZero's own runtimes: the speech LM as a standard Q8_0 GGUF (run by the app's llama.cpp,
/// Vulkan), chatterbox-turbo's decoder as ONNX (CPU ONNX Runtime), the per-voice conditioning precomputed, and the
/// text-normalization grammars — under <c>%LOCALAPPDATA%\AgentZeroLite\models\kittentts2\&lt;bundle&gt;</c>, shared by
/// both GUIs and the wearable host like every other model. <c>manifest.json</c> names every file with its size and
/// SHA-256; presence checks sizes, the installer and the downloader (<see cref="KittenTts2ModelDownloader"/>, from
/// AgentZero's GitHub Release) check hashes.
/// </summary>
public static class KittenTts2ModelStore
{
    public const string BundleName = "kittentts2-agentzero-v1";
    public const string DefaultVoice = "Bruno";
    public const string ManifestFile = "manifest.json";

    /// <summary>Approximate download, for the Settings screen.</summary>
    public const string SizeLabel = "~3.3 GB";

    public static string DefaultDirectory => Path.Combine(AppPaths.SharedRoot, "models", "kittentts2", BundleName);

    public static string ResolveDir(VoiceSettings s) =>
        string.IsNullOrWhiteSpace(s.Kitten2ModelDir) ? DefaultDirectory : s.Kitten2ModelDir;

    public static KittenTts2Manifest? ReadManifest(string dir)
    {
        try
        {
            var path = Path.Combine(dir, ManifestFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<KittenTts2Manifest>(File.ReadAllText(path)) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Every file the manifest names is there with its size (hashes are the installer's job).</summary>
    public static bool IsPresent(string dir)
    {
        var m = ReadManifest(dir);
        return m is not null && m.Files.Count > 0 && m.Files.All(f =>
        {
            var p = Path.Combine(dir, f.Path);
            return File.Exists(p) && new FileInfo(p).Length == f.Size;
        });
    }

    /// <summary>The voices the bundle carries, or the default name when nothing is installed.</summary>
    public static IReadOnlyList<string> Voices(string dir) => ReadManifest(dir)?.Voices ?? [DefaultVoice];
}
