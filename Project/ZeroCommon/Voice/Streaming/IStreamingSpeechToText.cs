namespace Agent.Common.Voice.Streaming;

/// <summary>
/// A recognizer that hears audio while it is still being spoken. Unlike
/// <see cref="ISpeechToText"/>, which is handed a finished utterance after the VAD has waited out
/// its silence hangover (1.2–2 s), a streaming engine is fed every ~50 ms frame and answers with
/// the text so far (<see cref="SttUpdate.IsFinal"/> = false) and, when its own endpoint rule
/// fires, the settled utterance (<c>IsFinal</c> = true).
///
/// <para>The engine holds the model; a <see cref="IStreamingSttSession"/> holds one conversation's
/// decoder state. Sessions are cheap — start one per mic-on, dispose it on mic-off.</para>
/// </summary>
public interface IStreamingSpeechToText : IDisposable
{
    /// <summary>Settings value this engine answers to (<see cref="SttProviderNames.SherpaStreaming"/>).</summary>
    string ProviderName { get; }

    /// <summary>Catalog id of the loaded model (<see cref="StreamingSttModelCatalog"/>).</summary>
    string ModelId { get; }

    /// <summary>Load the model. False when it is not installed or failed to load — progress says why.</summary>
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Open a decoding session. <paramref name="language"/> is a short tag or "auto".</summary>
    IStreamingSttSession StartSession(string language = "auto");
}

/// <summary>
/// One live decoding stream. Not thread-safe: feed it from a single consumer (the
/// <see cref="Streams.StreamingSttStage"/> does).
/// </summary>
public interface IStreamingSttSession : IDisposable
{
    /// <summary>
    /// Feed 16 kHz mono PCM16 (little-endian). Returns what changed: zero or more partials
    /// (only when the text actually changed) and at most one final per endpoint.
    /// </summary>
    IReadOnlyList<SttUpdate> Accept(ReadOnlySpan<byte> pcm16k);

    /// <summary>Flush the audio still in the decoder (mic off). Returns the last final, if any text was pending.</summary>
    SttUpdate? Finish();
}

/// <summary>
/// One recognizer answer. <see cref="Segment"/> counts endpoints from 0, so a UI can replace the
/// partial of the current segment in place and append when a new one starts.
/// <see cref="AudioSeconds"/> is the length of audio the segment covers so far.
/// </summary>
public sealed record SttUpdate(string Text, bool IsFinal, int Segment, double AudioSeconds);
