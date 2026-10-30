using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;
using Agent.Common.Voice.Streaming;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// The streaming counterpart of <see cref="VoiceSegmenterStage"/> + the STT pool: every
/// <see cref="MicFrame"/> goes straight into an <see cref="IStreamingSttSession"/>, and what the recognizer
/// says comes out as <see cref="SttUpdate"/>s — partials while the person speaks, a final at each endpoint.
///
/// <para>There is no VAD in front of it on purpose. The recognizer needs the silence too: its endpoint rule
/// counts trailing silence frames, and a decoder that never hears the pause never closes the utterance. RMS
/// gating would also clip the first consonant, which the batch path needs a 1 s pre-roll to undo.</para>
///
/// <para>Decoding is synchronous CPU work (an encoder chunk every 160–560 ms of audio), so
/// <see cref="StreamingSttFlow.Create"/> puts the stage on the blocking-IO dispatcher behind an async
/// boundary — the frame queue upstream keeps accepting while a chunk decodes.</para>
/// </summary>
public sealed class StreamingSttStage : GraphStage<FlowShape<MicFrame, SttUpdate>>
{
    public Inlet<MicFrame> In { get; } = new("StreamingStt.in");
    public Outlet<SttUpdate> Out { get; } = new("StreamingStt.out");
    public override FlowShape<MicFrame, SttUpdate> Shape { get; }

    private readonly Func<IStreamingSttSession> _sessionFactory;

    /// <param name="sessionFactory">Called once when the stage starts; the session is disposed when it stops.</param>
    public StreamingSttStage(Func<IStreamingSttSession> sessionFactory)
    {
        _sessionFactory = sessionFactory;
        Shape = new FlowShape<MicFrame, SttUpdate>(In, Out);
    }

    protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

    private sealed class Logic : InAndOutGraphStageLogic
    {
        private readonly StreamingSttStage _stage;
        private readonly Queue<SttUpdate> _pending = new();
        private IStreamingSttSession? _session;
        private bool _upstreamDone;

        public Logic(StreamingSttStage stage) : base(stage.Shape)
        {
            _stage = stage;
            SetHandler(stage.In, this);
            SetHandler(stage.Out, this);
        }

        public override void PreStart() => _session = _stage._sessionFactory();

        public override void PostStop()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
        }

        public override void OnPush()
        {
            var frame = Grab(_stage.In);
            foreach (var update in _session!.Accept(frame.Pcm16k)) _pending.Enqueue(update);
            if (_pending.Count > 0 && IsAvailable(_stage.Out)) Push(_stage.Out, _pending.Dequeue());
            else if (!HasBeenPulled(_stage.In)) Pull(_stage.In);
        }

        public override void OnPull()
        {
            if (_pending.Count > 0)
            {
                Push(_stage.Out, _pending.Dequeue());
                if (_upstreamDone && _pending.Count == 0) CompleteStage();
                return;
            }
            if (_upstreamDone) { CompleteStage(); return; }
            if (!HasBeenPulled(_stage.In)) Pull(_stage.In);
        }

        /// <summary>Mic off: flush what the decoder still holds as one last final before completing.</summary>
        public override void OnUpstreamFinish()
        {
            _upstreamDone = true;
            if (_session?.Finish() is { } last) _pending.Enqueue(last);
            if (_pending.Count == 0) { CompleteStage(); return; }
            if (IsAvailable(_stage.Out)) OnPull();
        }
    }
}

/// <summary>Convenience facade, like <see cref="VoiceSegmenterFlow"/>.</summary>
public static class StreamingSttFlow
{
    public static Flow<MicFrame, SttUpdate, NotUsed> Create(Func<IStreamingSttSession> sessionFactory)
        => Flow.FromGraph(new StreamingSttStage(sessionFactory))
            .WithAttributes(ActorAttributes.CreateDispatcher("akka.stream.default-blocking-io-dispatcher"));
}
