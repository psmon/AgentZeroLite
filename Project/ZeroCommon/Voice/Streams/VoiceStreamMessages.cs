// ─────────────────────────────────────────────────────────────
// VoiceStreamActor — message protocol
//
// Lives in ZeroCommon so headless tests can drive the actor without
// touching WPF / NAudio. The WPF-side capture service (NAudio
// WaveInEvent) only knows how to Tell `MicFrame` — everything
// downstream (VAD, segmentation, STT, transcript dispatch) is
// orchestrated by VoiceStreamActor's materialized Akka.Streams graph.
//
// Naming follows the existing Messages.cs conventions.
// ─────────────────────────────────────────────────────────────

using Akka.Actor;

namespace Agent.Common.Voice.Streams;

// ── Frame-level (capture → actor → Source.Queue) ─────────────

/// <summary>
/// One ~50 ms PCM frame emitted by the capture device. Pcm16k is 16-bit
/// little-endian signed mono at 16 kHz — the format every shipped STT
/// provider already expects, so the bytes flow through the segmenter
/// untouched.
/// </summary>
public sealed record MicFrame(byte[] Pcm16k, float Rms);

/// <summary>
/// Result of the segmenter Flow — one complete utterance from the moment
/// VAD declares "speaking" through the trailing silence hangover. Includes
/// the pre-roll bytes seeded at utterance-start so consonant attack isn't
/// clipped.
/// </summary>
public sealed record PcmSegment(byte[] Pcm16k, double DurationSeconds, DateTimeOffset StartedAt);

// ── Lifecycle (UI → VoiceStreamActor) ────────────────────────

/// <summary>
/// Materialize the INPUT graph and start accepting <see cref="MicFrame"/>s.
/// The caller (UI side, holding the device handle) is responsible for the
/// actual NAudio capture; once this message is acknowledged the actor is
/// ready to receive frames.
/// </summary>
public sealed record StartListening(
    float VadThreshold,
    double PreRollSeconds = 1.0,
    int UtteranceHangoverFrames = 40,
    int MicBufferSize = 64,
    int SttParallelism = 1,
    string Language = "auto");

/// <summary>Tear down the materialized graph and stop the STT pool.</summary>
public sealed record StopListening;

// ── Output (graph → AgentBotActor / UI) ──────────────────────

/// <summary>
/// One transcript ready for downstream routing. Emitted when the segmenter
/// Flow + STT pool produce a non-empty result. Goes through the actor's
/// own Sink.ActorRefWithAck protocol so the actor can apply backpressure
/// (e.g. drop the next segment while the bot is busy).
/// </summary>
public sealed record VoiceTranscriptReady(string Transcript, double UtteranceDurationSeconds);

// ── Sink.ActorRefWithAck protocol ────────────────────────────

/// <summary>onInit message for Sink.ActorRefWithAck — graph is connected.</summary>
public sealed record VoiceStreamReady;

/// <summary>ack message for Sink.ActorRefWithAck — actor signals "ready for next element".</summary>
public sealed record VoiceFrameAck;

/// <summary>onComplete message for Sink.ActorRefWithAck — graph closed normally.</summary>
public sealed record VoiceStreamEnded;

/// <summary>onFailure factory for Sink.ActorRefWithAck — graph aborted.</summary>
public sealed record VoiceStreamFailed(Exception Error);

// ── Stage gateway (UI → StageActor → VoiceStreamActor) ───────

/// <summary>
/// Ask StageActor to instantiate a singleton VoiceStreamActor under
/// <c>/user/stage/voice</c>. Reply: <see cref="VoiceStreamCreated"/>.
///
/// <para>SttFactory / TtsFactory are invoked once per worker in the
/// respective pool. PlaybackFactory is invoked once per
/// <see cref="SpeakResponse"/>. Callers can capture any non-WPF state
/// (settings snapshot, API keys) — the lambdas run on the worker
/// actor's dispatcher.</para>
///
/// <para>OnTranscript is invoked from the actor's Receive thread for every
/// successful transcript; implementations that touch the WPF UI must
/// marshal back themselves.</para>
///
/// <para>TtsFactory and PlaybackFactory may be null for INPUT-only sessions
/// (P1 wiring path). The actor refuses <see cref="SpeakResponse"/> when
/// either is missing.</para>
/// </summary>
public sealed record CreateVoiceStream(
    Func<ISpeechToText> SttFactory,
    Action<string, double> OnTranscript,
    Func<ITextToSpeech>? TtsFactory = null,
    Func<IAudioPlaybackQueue>? PlaybackFactory = null,
    Action<bool>? OnTtsPlaybackChanged = null);

/// <summary>Reply for <see cref="CreateVoiceStream"/>.</summary>
public sealed record VoiceStreamCreated(IActorRef VoiceRef);

// ── Future (P2/P3 placeholders, declared now to avoid churn) ─

/// <summary>
/// Materialize the OUTPUT graph: stream tokens → sentence chunks →
/// (markdown strip) → TTS pool → playback queue. The actor owns the
/// kill switch so a follow-up <see cref="BargeIn"/> /
/// <see cref="CancelInflight"/> tears down both the LLM token feed and
/// any queued TTS chunks atomically.
///
/// <see cref="TtsParallelism"/> sets the worker pool size; 2 is the sweet
/// spot — one chunk plays while the next is being synthesized.
/// </summary>
public sealed record SpeakResponse(
    IAsyncEnumerable<string> TokenStream,
    string Voice,
    int TtsParallelism = 2);

/// <summary>Single-shot speak helper that wraps a string into a 1-element token stream.</summary>
public sealed record SpeakText(string Text, string Voice, int TtsParallelism = 2);

/// <summary>P3 — user barge-in detected; cancel the current OUTPUT graph and reset to listen state.</summary>
public sealed record BargeIn;

/// <summary>P3 — explicit user cancel of the in-flight pipeline (button-driven).</summary>
public sealed record CancelInflight;

/// <summary>P3 — turn boundary; release any per-turn resources without tearing down the actor.</summary>
public sealed record EndTurn;

/// <summary>P3 — capture device disappeared; UI should resurface device picker.</summary>
public sealed record DeviceLost(string Reason);
