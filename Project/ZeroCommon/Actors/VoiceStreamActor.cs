// ───────────────────────────────────────────────────────────
// VoiceStreamActor — owns the materialized voice INPUT graph
//
// Topology (P1 — INPUT only; P2 adds OUTPUT, P3 adds barge-in):
//
//   Source.Queue<MicFrame>(N, DropHead)
//      ▼ ViaMaterialized(KillSwitches.Single, Keep.Both)
//   VoiceSegmenterFlow (VAD + pre-roll + utterance FSM)
//      ▼ .Async()
//   SelectAsync(p, seg => sttPool.Ask<TranscribeReply>(seg))
//      ▼ Where(transcript ≠ "")
//   Sink.ActorRefWithAck(Self, init=Ready, ack=Ack, complete=Ended)
//
// Why an actor at all (vs. a static class with a materializer)?
//   1. The materializer needs an ActorSystem context — owning the graph
//      from an actor keeps lifecycle within the supervision tree.
//   2. Stop is a real lifecycle event: Self.PostStop tears down the
//      kill switch and STT pool deterministically.
//   3. Future P3 control-plane messages (BargeIn, CancelInflight,
//      EndTurn, DeviceLost) need a single Tell-only inbox per session.
//
// Path: /user/stage/voice  (created by StageActor on CreateVoiceStream).
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Agent.Common.Voice;
using Agent.Common.Voice.Streams;

namespace Agent.Common.Actors;

public sealed class VoiceStreamActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly ActorMaterializer _materializer;
    private readonly Func<ISpeechToText> _sttFactory;
    private readonly Func<ITextToSpeech>? _ttsFactory;
    private readonly Func<IAudioPlaybackQueue>? _playbackFactory;
    private readonly Action<string, double> _onTranscript;
    private readonly Action<bool>? _onTtsPlaybackChanged;

    private ISourceQueueWithComplete<MicFrame>? _frameQueue;
    private UniqueKillSwitch? _inputKillSwitch;
    private IActorRef? _sttPool;
    private string? _language;
    private float _vadThreshold;

    private IActorRef? _ttsPool;
    private IAudioPlaybackQueue? _playback;
    private ISourceQueueWithComplete<string>? _tokenQueue;
    private UniqueKillSwitch? _outputKillSwitch;

    // P3 — barge-in detection state. _outputActive is set when SpeakResponse
    // materialises, cleared when playback drains. While active, a streak of
    // BargeInFrameThreshold loud frames triggers Self.Tell(BargeIn) which
    // tears down the OUTPUT graph atomically (LLM token feed + TTS pool +
    // playback queue).
    private bool _outputActive;
    private int _consecutiveLoudFrames;
    private const int BargeInFrameThreshold = 4;

    public VoiceStreamActor(
        Func<ISpeechToText> sttFactory,
        Action<string, double> onTranscript,
        Func<ITextToSpeech>? ttsFactory = null,
        Func<IAudioPlaybackQueue>? playbackFactory = null,
        Action<bool>? onTtsPlaybackChanged = null)
    {
        _sttFactory = sttFactory;
        _onTranscript = onTranscript;
        _ttsFactory = ttsFactory;
        _playbackFactory = playbackFactory;
        _onTtsPlaybackChanged = onTtsPlaybackChanged;
        _materializer = Context.System.Materializer();

        // ── INPUT graph ──
        Receive<StartListening>(OnStart);
        Receive<StopListening>(_ => StopInputGraph());
        Receive<MicFrame>(OnMicFrame);

        // Sink.ActorRefWithAck protocol — Self acts as the sink. The graph
        // sends VoiceStreamReady once, then alternates Ack ←→ next element.
        Receive<VoiceStreamReady>(_ =>
        {
            _log.Info("[Voice] INPUT graph connected");
            Sender.Tell(new VoiceFrameAck());
        });
        Receive<VoiceTranscriptReady>(msg =>
        {
            try { _onTranscript(msg.Transcript, msg.UtteranceDurationSeconds); }
            catch (Exception ex) { _log.Error(ex, "[Voice] OnTranscript callback threw"); }
            Sender.Tell(new VoiceFrameAck());
        });
        Receive<VoiceStreamEnded>(_ => _log.Info("[Voice] INPUT graph completed"));
        Receive<VoiceStreamFailed>(msg =>
            _log.Error(msg.Error, "[Voice] INPUT graph failed"));

        // ── OUTPUT graph ──
        Receive<SpeakResponse>(OnSpeakResponse);
        Receive<SpeakText>(msg => OnSpeakResponse(new SpeakResponse(
            TokenStream: SingleAsync(msg.Text), Voice: msg.Voice, TtsParallelism: msg.TtsParallelism)));

        // ── Control plane (P3 — partial: structural messages handled now,
        // detector + RestartFlow follow in P3 final cut) ──
        Receive<BargeIn>(_ => CancelOutputGraph("BargeIn"));
        Receive<CancelInflight>(_ => CancelOutputGraph("CancelInflight"));
        Receive<EndTurn>(_ => CancelOutputGraph("EndTurn"));
        Receive<DeviceLost>(msg =>
        {
            _log.Warning("[Voice] DeviceLost: {0}", msg.Reason);
            StopInputGraph();
            CancelOutputGraph("DeviceLost");
        });
    }

    private static async IAsyncEnumerable<string> SingleAsync(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }

    private void OnStart(StartListening cmd)
    {
        if (_frameQueue is not null)
        {
            _log.Info("[Voice] StartListening ignored — graph already running");
            return;
        }

        _language = cmd.Language;
        _vadThreshold = cmd.VadThreshold;

        _sttPool = Context.ActorOf(
            SttWorkerActor.PoolProps(_sttFactory, cmd.SttParallelism),
            "stt-pool");

        var vadCfg = new VadConfig(
            VadThreshold: cmd.VadThreshold,
            PreRollSeconds: cmd.PreRollSeconds,
            UtteranceHangoverFrames: cmd.UtteranceHangoverFrames);

        var sink = Sink.ActorRefWithAck<VoiceTranscriptReady>(
            Self,
            onInitMessage: new VoiceStreamReady(),
            ackMessage: new VoiceFrameAck(),
            onCompleteMessage: new VoiceStreamEnded(),
            onFailureMessage: ex => new VoiceStreamFailed(ex));

        var sttPool = _sttPool;
        var language = cmd.Language;
        var parallelism = Math.Max(1, cmd.SttParallelism);

        // Materialize. ViaMaterialized(KillSwitches.Single, Keep.Both) at the
        // Source.Queue boundary gives us (queue, killSwitch) as the left mat.
        // ToMaterialized(sink, Keep.Left) discards the sink's Task<NotUsed>
        // because we don't need it — actor lifecycle handles completion.
        var materialized = Source.Queue<MicFrame>(cmd.MicBufferSize, OverflowStrategy.DropHead)
            .ViaMaterialized(KillSwitches.Single<MicFrame>(), Keep.Both)
            .Via(VoiceSegmenterFlow.Create(vadCfg))
            .Async()
            .SelectAsync(parallelism, async (PcmSegment seg) =>
            {
                var reply = await sttPool.Ask<TranscribeReply>(
                    new TranscribeRequest(seg, language),
                    TimeSpan.FromSeconds(120));
                return new VoiceTranscriptReady(reply.Transcript, reply.DurationSeconds);
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Transcript))
            .ToMaterialized(sink, Keep.Left)
            .Run(_materializer);

        _frameQueue = materialized.Item1;
        _inputKillSwitch = materialized.Item2;

        _log.Info(
            "[Voice] INPUT graph materialised | poolSize={0} micBuffer={1} preRoll={2}s threshold={3} hangover={4}",
            parallelism, cmd.MicBufferSize, cmd.PreRollSeconds, cmd.VadThreshold, cmd.UtteranceHangoverFrames);
    }

    private void OnMicFrame(MicFrame frame)
    {
        // P3 — barge-in: while AI speech is playing, a sustained burst of
        // loud frames cancels the OUTPUT graph and lets the user's actual
        // utterance go through the segmenter normally.
        if (_outputActive)
        {
            if (frame.Rms >= _vadThreshold)
            {
                _consecutiveLoudFrames++;
                if (_consecutiveLoudFrames >= BargeInFrameThreshold)
                {
                    _consecutiveLoudFrames = 0;
                    Self.Tell(new BargeIn());
                }
            }
            else
            {
                _consecutiveLoudFrames = 0;
            }
        }

        var queue = _frameQueue;
        if (queue is null) return;
        // Fire-and-forget. With OverflowStrategy.DropHead the queue never
        // blocks — when full the oldest frame is dropped, preserving recency
        // (the audio thread cannot slow down, so dropping stale frames is
        // strictly better than backpressuring the soundcard).
        _ = queue.OfferAsync(frame);
    }

    private void StopInputGraph()
    {
        try { _inputKillSwitch?.Shutdown(); } catch { }
        try { _frameQueue?.Complete(); } catch { }
        _frameQueue = null;
        _inputKillSwitch = null;
        if (_sttPool is not null)
        {
            try { Context.Stop(_sttPool); } catch { }
            _sttPool = null;
        }
        _log.Info("[Voice] INPUT graph stopped");
    }

    // ── OUTPUT graph (P2) ─────────────────────────────────────

    private void OnSpeakResponse(SpeakResponse cmd)
    {
        if (_ttsFactory is null || _playbackFactory is null)
        {
            _log.Warning("[Voice] SpeakResponse rejected — TtsFactory or PlaybackFactory not provided at CreateVoiceStream time");
            return;
        }
        // If a previous response is still playing, cancel it first — the
        // new response supersedes. P3 will distinguish "user barge-in"
        // (cancel) from "still speaking, queue more" (concat).
        if (_outputKillSwitch is not null) CancelOutputGraph("superseded by new SpeakResponse");

        _ttsPool ??= Context.ActorOf(
            TtsWorkerActor.PoolProps(_ttsFactory, cmd.TtsParallelism),
            $"tts-pool-{Context.GetChildren().Count()}");
        if (_playback is null)
        {
            _playback = _playbackFactory();
            // P3 — track playback state so OnMicFrame can run barge-in detection.
            _playback.PlaybackStarted += () =>
            {
                _outputActive = true;
                _consecutiveLoudFrames = 0;
                try { _onTtsPlaybackChanged?.Invoke(true); }
                catch (Exception ex) { _log.Error(ex, "[Voice] OnTtsPlaybackChanged(true) threw"); }
            };
            _playback.PlaybackStopped += () =>
            {
                _outputActive = false;
                _consecutiveLoudFrames = 0;
                try { _onTtsPlaybackChanged?.Invoke(false); }
                catch (Exception ex) { _log.Error(ex, "[Voice] OnTtsPlaybackChanged(false) threw"); }
            };
        }

        var ttsPool = _ttsPool;
        var playback = _playback;
        var voice = cmd.Voice;
        var parallelism = Math.Max(1, cmd.TtsParallelism);

        var sink = Sink.ForEach<SynthesizeReply>(reply =>
        {
            if (!string.IsNullOrEmpty(reply.Error)) return;
            if (reply.Audio is null || reply.Audio.Length == 0) return;
            try { playback.Enqueue(reply.Audio, reply.Format); }
            catch (Exception ex) { _log.Error(ex, "[Voice] Playback enqueue threw"); }
        });

        var materialized = Source.Queue<string>(64, OverflowStrategy.Backpressure)
            .ViaMaterialized(KillSwitches.Single<string>(), Keep.Both)
            .Via(SentenceChunkerFlow.Create())
            .Select(TtsTextCleaner.StripMarkdown)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectAsync(parallelism, async (string chunk) =>
            {
                var reply = await ttsPool.Ask<SynthesizeReply>(
                    new SynthesizeRequest(chunk, voice),
                    TimeSpan.FromSeconds(60));
                return reply;
            })
            .ToMaterialized(sink, Keep.Left)
            .Run(_materializer);

        _tokenQueue = materialized.Item1;
        _outputKillSwitch = materialized.Item2;

        var queue = _tokenQueue;
        var ks = _outputKillSwitch;

        // Pump tokens from the IAsyncEnumerable into the Source.Queue. This
        // runs off the actor thread — the actor stays responsive to BargeIn /
        // CancelInflight while the LLM stream is feeding.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var token in cmd.TokenStream)
                {
                    // Cancel detection: if the kill switch was shut down,
                    // OfferAsync returns Dropped/QueueClosed.
                    if (queue is null) return;
                    var result = await queue.OfferAsync(token);
                    if (result is Akka.Streams.QueueOfferResult.QueueClosed) return;
                    if (result is Akka.Streams.QueueOfferResult.Failure f)
                    {
                        _log.Error(f.Cause, "[Voice] Token queue offer failed");
                        return;
                    }
                }
                queue?.Complete();
            }
            catch (OperationCanceledException) { try { queue?.Complete(); } catch { } }
            catch (Exception ex)
            {
                _log.Error(ex, "[Voice] Token pump failed");
                try { queue?.Fail(ex); } catch { }
            }
        });

        _log.Info("[Voice] OUTPUT graph materialised | voice={0} ttsParallelism={1}", voice, parallelism);
    }

    private void CancelOutputGraph(string reason)
    {
        if (_outputKillSwitch is null && _tokenQueue is null && !_outputActive) return;
        try { _outputKillSwitch?.Shutdown(); } catch { }
        try { _tokenQueue?.Complete(); } catch { }
        _outputKillSwitch = null;
        _tokenQueue = null;
        try { _playback?.Stop(); } catch { }
        _outputActive = false;
        _consecutiveLoudFrames = 0;
        _log.Info("[Voice] OUTPUT graph cancelled — reason={0}", reason);
    }

    protected override void PostStop()
    {
        StopInputGraph();
        CancelOutputGraph("PostStop");
        try { _playback?.Dispose(); } catch { }
        if (_ttsPool is not null) try { Context.Stop(_ttsPool); } catch { }
        _playback = null;
        _ttsPool = null;
        base.PostStop();
    }
}
