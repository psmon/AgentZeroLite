using Akka.Actor;
using Akka.Event;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// One STT request, dispatched from the segmenter <c>SelectAsync</c> stage to
/// a worker in the pool via <c>Ask</c>. Carrying the language per-request keeps
/// the worker stateless w.r.t. settings.
/// </summary>
public sealed record TranscribeRequest(PcmSegment Segment, string Language);

/// <summary>
/// Reply for <see cref="TranscribeRequest"/>. Empty Transcript with a non-null
/// Error means the worker caught an exception; the segmenter Flow will drop
/// it via the <c>Where</c> filter downstream.
/// </summary>
public sealed record TranscribeReply(string Transcript, double DurationSeconds, string? Error = null);

/// <summary>
/// One STT engine instance per worker. The pool router (<c>RoundRobinPool</c>
/// or <c>SmallestMailboxPool</c>) materialised in <see cref="Actors.VoiceStreamActor"/>
/// fans incoming <see cref="TranscribeRequest"/>s across N workers, so STT
/// parallelism is configured at runtime — not fused into the graph as a
/// fixed <c>SelectAsync(parallelism: N)</c>. That makes it controllable from
/// settings or from a runtime <c>SetSttParallelism</c> message later (P3).
///
/// The worker constructs its <see cref="ISpeechToText"/> via the supplied
/// factory — once, lazily, on first request. <c>EnsureReadyAsync</c> is
/// invoked then so the cold-start cost (Whisper.net "small" ≈ 487 MB load)
/// only happens on the first segment instead of stalling the materialisation
/// of every replica.
/// </summary>
public sealed class SttWorkerActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Func<ISpeechToText> _factory;
    private ISpeechToText? _stt;

    public SttWorkerActor(Func<ISpeechToText> factory)
    {
        _factory = factory;
        ReceiveAsync<TranscribeRequest>(HandleAsync);
    }

    private async Task HandleAsync(TranscribeRequest req)
    {
        var sender = Sender;
        try
        {
            _stt ??= _factory()
                ?? throw new InvalidOperationException("SttFactory returned null");
            await _stt.EnsureReadyAsync();
            // P3 — bounded retry for transient network errors. STT providers
            // (OpenAI, Webnori) occasionally 5xx; one retry recovers most.
            // Cancellation / argument errors fall through immediately.
            var transcript = await TransientRetry.WithBackoffAsync(
                () => _stt.TranscribeAsync(req.Segment.Pcm16k, req.Language),
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(250),
                _log);
            sender.Tell(new TranscribeReply(
                Transcript: transcript ?? string.Empty,
                DurationSeconds: req.Segment.DurationSeconds));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[SttWorker] {0} bytes={1} dur={2}s",
                ex.GetType().Name, req.Segment.Pcm16k.Length, req.Segment.DurationSeconds);
            sender.Tell(new TranscribeReply(
                Transcript: string.Empty,
                DurationSeconds: req.Segment.DurationSeconds,
                Error: ex.Message));
        }
    }

    public static Props PoolProps(Func<ISpeechToText> factory, int parallelism)
    {
        var props = Props.Create(() => new SttWorkerActor(factory));
        return props.WithRouter(new Akka.Routing.SmallestMailboxPool(Math.Max(1, parallelism)));
    }
}
