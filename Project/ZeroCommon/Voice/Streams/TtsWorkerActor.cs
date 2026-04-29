using Akka.Actor;
using Akka.Event;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// One TTS synthesize request. <see cref="Voice"/> mirrors the legacy
/// <c>VoiceSettings.TtsVoice</c> string.
/// </summary>
public sealed record SynthesizeRequest(string Text, string Voice);

/// <summary>
/// Reply for <see cref="SynthesizeRequest"/>. Empty Audio with Error set
/// signals "skip this chunk" — the OUTPUT graph filters those out so a
/// transient TTS failure doesn't break playback for the rest of the
/// response.
/// </summary>
public sealed record SynthesizeReply(byte[] Audio, string Format, string? Error = null);

/// <summary>
/// One TTS engine instance per worker. Same shape as
/// <see cref="SttWorkerActor"/> — the pool is built with a router so
/// parallelism is a runtime knob, not a fused <c>SelectAsync(p)</c>
/// constant. Two parallel TTS workers can keep playback fed while
/// rendering the next sentence in the background.
/// </summary>
public sealed class TtsWorkerActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Func<ITextToSpeech> _factory;
    private ITextToSpeech? _tts;

    public TtsWorkerActor(Func<ITextToSpeech> factory)
    {
        _factory = factory;
        ReceiveAsync<SynthesizeRequest>(HandleAsync);
    }

    private async Task HandleAsync(SynthesizeRequest req)
    {
        var sender = Sender;
        try
        {
            _tts ??= _factory()
                ?? throw new InvalidOperationException("TtsFactory returned null");
            // P3 — same bounded retry as STT. TTS chunks should not stall
            // playback over a single transient 5xx.
            var bytes = await TransientRetry.WithBackoffAsync(
                () => _tts.SynthesizeAsync(req.Text, req.Voice),
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(200),
                _log);
            sender.Tell(new SynthesizeReply(bytes, _tts.AudioFormat));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[TtsWorker] {0} text=\"{1}\"",
                ex.GetType().Name,
                req.Text.Length > 60 ? req.Text[..60] + "…" : req.Text);
            sender.Tell(new SynthesizeReply(Array.Empty<byte>(), "wav", Error: ex.Message));
        }
    }

    public static Props PoolProps(Func<ITextToSpeech> factory, int parallelism)
    {
        var props = Props.Create(() => new TtsWorkerActor(factory));
        return props.WithRouter(new Akka.Routing.SmallestMailboxPool(Math.Max(1, parallelism)));
    }
}
