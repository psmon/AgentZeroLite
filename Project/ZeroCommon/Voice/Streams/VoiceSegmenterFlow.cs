using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// Combined VAD + segmenter as a custom <see cref="GraphStage{TShape}"/>.
/// Translates a stream of <see cref="MicFrame"/>s into <see cref="PcmSegment"/>s
/// — one per detected utterance.
///
/// Why a GraphStage and not <c>StatefulSelectMany</c>? In Akka.NET 1.5 that
/// extension is defined on <c>SourceOperations</c> only — there is no
/// <c>FlowOperations.StatefulSelectMany</c>. A GraphStage is the canonical
/// (and most efficient) way to encode an inlet → state machine → outlet
/// pattern in Akka.NET, and it gives us exact control over backpressure
/// (we Pull on every "no segment yet" frame and Push on the rare
/// "segment just completed" frame).
///
/// Folds three concerns from the legacy <c>VoiceCaptureService</c>:
///   1. Frame-level RMS check (above threshold = speaking).
///   2. Utterance-level FSM with a silence hangover (Origin default
///      40 frames ≈ 2 s) so brief mid-sentence pauses don't split.
///   3. A pre-roll ring buffer (default 1 s) seeded into the active
///      utterance buffer the moment VAD trips, so the first consonant
///      isn't clipped.
///
/// Pure logic — no WPF, no NAudio. Headlessly testable with
/// <c>Akka.Streams.TestKit</c>'s <c>TestSource.Probe</c> /
/// <c>TestSink.Probe</c>.
/// </summary>
public sealed class VoiceSegmenterStage : GraphStage<FlowShape<MicFrame, PcmSegment>>
{
    /// <summary>One frame of 16 kHz / 16-bit / mono = 32000 bytes per second.</summary>
    public const int BytesPerSecondMono16k16 = 16_000 * 2;

    public Inlet<MicFrame> In { get; } = new("VoiceSegmenter.in");
    public Outlet<PcmSegment> Out { get; } = new("VoiceSegmenter.out");
    public override FlowShape<MicFrame, PcmSegment> Shape { get; }

    private readonly VadConfig _config;

    public VoiceSegmenterStage(VadConfig config)
    {
        _config = config;
        Shape = new FlowShape<MicFrame, PcmSegment>(In, Out);
    }

    protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

    private sealed class Logic : InAndOutGraphStageLogic
    {
        private readonly VoiceSegmenterStage _stage;
        private readonly long _maxPreRollBytes;

        private bool _inUtterance;
        private int _utteranceSilenceFrames;

        private readonly Queue<byte[]> _preRoll = new();
        private long _preRollBytes;

        private List<byte>? _buffer;
        private DateTimeOffset _startedAt;

        public Logic(VoiceSegmenterStage stage) : base(stage.Shape)
        {
            _stage = stage;
            _maxPreRollBytes = (long)Math.Max(0, stage._config.PreRollSeconds * BytesPerSecondMono16k16);

            SetHandler(stage.In, this);
            SetHandler(stage.Out, this);
        }

        public override void OnPush()
        {
            var frame = Grab(_stage.In);

            // Pre-roll ring (always — even outside an utterance — so when
            // VAD trips the prior PreRollSeconds of audio is available).
            _preRoll.Enqueue(frame.Pcm16k);
            _preRollBytes += frame.Pcm16k.Length;
            while (_preRollBytes > _maxPreRollBytes && _preRoll.Count > 0)
            {
                var old = _preRoll.Dequeue();
                _preRollBytes -= old.Length;
            }

            var above = frame.Rms >= _stage._config.VadThreshold;

            if (above)
            {
                _utteranceSilenceFrames = 0;
                if (!_inUtterance)
                {
                    _inUtterance = true;
                    var capacityHint = (int)Math.Min(int.MaxValue,
                        _maxPreRollBytes + BytesPerSecondMono16k16 * 8L);
                    _buffer = new List<byte>(capacityHint);
                    foreach (var chunk in _preRoll)
                        if (!ReferenceEquals(chunk, frame.Pcm16k))
                            _buffer.AddRange(chunk);
                    _buffer.AddRange(frame.Pcm16k);
                    _startedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    _buffer!.AddRange(frame.Pcm16k);
                }
                Pull(_stage.In);
                return;
            }

            if (_inUtterance)
            {
                _buffer!.AddRange(frame.Pcm16k);
                _utteranceSilenceFrames++;
                if (_utteranceSilenceFrames >= _stage._config.UtteranceHangoverFrames)
                {
                    _inUtterance = false;
                    var pcm = _buffer.ToArray();
                    _buffer = null;
                    var duration = pcm.Length / (double)BytesPerSecondMono16k16;
                    Push(_stage.Out, new PcmSegment(pcm, duration, _startedAt));
                    return;
                }
                Pull(_stage.In);
                return;
            }

            // Below-threshold, not in utterance — drop and pull next.
            Pull(_stage.In);
        }

        public override void OnPull() => Pull(_stage.In);
    }
}

/// <summary>
/// Convenience facade so callsites read <c>VoiceSegmenterFlow.Create(cfg)</c>
/// instead of constructing the GraphStage manually.
/// </summary>
public static class VoiceSegmenterFlow
{
    public static Flow<MicFrame, PcmSegment, NotUsed> Create(VadConfig cfg)
        => Flow.FromGraph(new VoiceSegmenterStage(cfg));
}
