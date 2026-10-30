namespace Agent.Common.Voice.Streaming;

/// <summary>
/// Lets a streaming engine answer where an <see cref="ISpeechToText"/> is asked for — the Voice Test panel,
/// the batch mic path, voice notes. The whole buffer goes through one session in 50 ms frames, as the mic
/// would deliver it, and every final is joined. Same model, same text; only the "while you speak" part is lost,
/// so choosing the streaming provider never leaves a path without an ear.
/// </summary>
public sealed class StreamingSttBatchAdapter(IStreamingSpeechToText engine) : ISpeechToText
{
    private const int FrameBytes = 1600; // 50 ms of 16 kHz PCM16, a microphone frame

    public string ProviderName => engine.ProviderName;

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        => engine.EnsureReadyAsync(progress, ct);

    public Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var session = engine.StartSession(language);
            var finals = new List<string>();
            for (int offset = 0; offset < pcm16kMono.Length; offset += FrameBytes)
            {
                ct.ThrowIfCancellationRequested();
                var length = Math.Min(FrameBytes, pcm16kMono.Length - offset);
                finals.AddRange(session.Accept(pcm16kMono.AsSpan(offset, length)).Where(u => u.IsFinal).Select(u => u.Text));
            }
            if (session.Finish() is { } last) finals.Add(last.Text);
            return string.Join(" ", finals);
        }, ct);
}
