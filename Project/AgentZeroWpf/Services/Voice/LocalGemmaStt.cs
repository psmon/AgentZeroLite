using Agent.Common.Voice;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// On-device Gemma audio STT — placeholder for Lite phase 2.
///
/// AgentZeroLite already loads Gemma GGUFs through the self-built llama.dll
/// runtime, but no audio-capable Gemma variant exists in the catalog yet.
/// When such a GGUF (e.g. a Gemma 4 multimodal release that accepts audio
/// tokens) lands, this class will load the model via LLamaSharp's multimodal
/// API and return the transcription. Until then, <see cref="EnsureReadyAsync"/>
/// returns false with a clear hint so the user picks a different STT provider.
/// </summary>
public sealed class LocalGemmaStt : ISpeechToText
{
    public string ProviderName => "LocalGemma";

    private readonly string _modelId;

    public LocalGemmaStt(string modelId)
    {
        _modelId = modelId;
    }

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(
            $"On-device Gemma audio STT not yet bundled (model={_modelId}). " +
            "Pick a different STT provider on the Voice tab until an audio-capable Gemma GGUF ships.");
        return Task.FromResult(false);
    }

    public Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
        => throw new NotSupportedException(
            "LocalGemma STT not implemented yet — call EnsureReadyAsync first and surface its progress message to the user.");
}
