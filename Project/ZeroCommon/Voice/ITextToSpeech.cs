namespace Agent.Common.Voice;

/// <summary>
/// Text-to-Speech provider abstraction. Implementations synthesize text into
/// audio bytes. Ported from the AgentWin origin.
/// </summary>
public interface ITextToSpeech
{
    string ProviderName { get; }

    /// <summary>Audio container of <see cref="SynthesizeAsync"/>: "wav", "mp3", or "pcm16".</summary>
    string AudioFormat { get; }

    /// <summary>List available voice ids for this provider.</summary>
    Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default);

    /// <summary>Synthesize text into audio bytes using the specified voice.</summary>
    Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default);
}
