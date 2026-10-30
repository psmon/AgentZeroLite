// ───────────────────────────────────────────────────────────
// StreamingSttStage + VoiceStreamActor (streaming branch) tests —
// headless, a scripted IStreamingSttSession stands in for sherpa-onnx.
//
// Scope: every frame reaches the recognizer (silence included), partials
// and finals keep their order under backpressure, mic-off flushes the
// last words, and the voice actor routes partials / finals to the right
// callback. The real recognizer is exercised by
// `AgentZeroWearable.exe --hear-stream`.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using Agent.Common.Actors;
using Agent.Common.Voice;
using Agent.Common.Voice.Streaming;
using Agent.Common.Voice.Streams;

namespace ZeroCommon.Tests.Voice.Streams;

public sealed class StreamingSttStageTests : TestKit
{
    /// <summary>Answers frame n (0-based) with whatever the script says; records what it was fed.</summary>
    private sealed class ScriptedSession(Func<int, IReadOnlyList<SttUpdate>> script, SttUpdate? onFinish = null)
        : IStreamingSttSession
    {
        public int Frames;
        public int Bytes;
        public bool Finished;
        public bool Disposed;

        public IReadOnlyList<SttUpdate> Accept(ReadOnlySpan<byte> pcm16k)
        {
            Bytes += pcm16k.Length;
            return script(Frames++);
        }

        public SttUpdate? Finish() { Finished = true; return onFinish; }
        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptedEngine(ScriptedSession session) : IStreamingSpeechToText
    {
        public string? Language;
        public string ProviderName => SttProviderNames.SherpaStreaming;
        public string ModelId => "scripted";
        public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
        public IStreamingSttSession StartSession(string language = "auto") { Language = language; return session; }
        public void Dispose() { }
    }

    private static MicFrame Frame(float rms = 0f) => new(new byte[3200], rms);
    private static SttUpdate Partial(string t, int seg = 0) => new(t, false, seg, 0.5);
    private static SttUpdate Final(string t, int seg = 0) => new(t, true, seg, 1.0);

    private (TestPublisher.Probe<MicFrame>, TestSubscriber.Probe<SttUpdate>) Run(ScriptedSession session)
        => this.SourceProbe<MicFrame>()
            .Via(StreamingSttFlow.Create(() => session))
            .ToMaterialized(this.SinkProbe<SttUpdate>(), Keep.Both)
            .Run(Sys.Materializer());

    [Fact]
    public void Silent_frames_still_reach_the_recognizer()
    {
        // The endpoint rule counts trailing silence — a VAD in front would starve it.
        var session = new ScriptedSession(_ => []);
        var (pub, sub) = Run(session);
        sub.Request(1);
        for (int i = 0; i < 10; i++) pub.SendNext(Frame(rms: 0f));
        pub.SendComplete();
        sub.ExpectComplete();
        Assert.Equal(10, session.Frames);
        Assert.Equal(10 * 3200, session.Bytes);
    }

    [Fact]
    public void Partials_then_final_arrive_in_order()
    {
        var session = new ScriptedSession(n => n switch
        {
            1 => [Partial("안녕")],
            3 => [Partial("안녕하세요")],
            5 => [Final("안녕하세요.")],
            _ => [],
        });
        var (pub, sub) = Run(session);
        sub.Request(10);
        for (int i = 0; i < 6; i++) pub.SendNext(Frame());
        Assert.Equal("안녕", sub.ExpectNext().Text);
        Assert.Equal("안녕하세요", sub.ExpectNext().Text);
        var final = sub.ExpectNext();
        Assert.True(final.IsFinal);
        Assert.Equal("안녕하세요.", final.Text);
    }

    [Fact]
    public void Several_updates_from_one_frame_survive_one_at_a_time_demand()
    {
        // An endpoint can close one segment and the same chunk already starts the next.
        var session = new ScriptedSession(n => n == 0 ? [Final("하나", 0), Partial("둘", 1)] : []);
        var (pub, sub) = Run(session);
        sub.Request(1);
        pub.SendNext(Frame());
        Assert.Equal("하나", sub.ExpectNext().Text);
        sub.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
        sub.Request(1);
        Assert.Equal("둘", sub.ExpectNext().Text);
    }

    [Fact]
    public void Mic_off_flushes_the_last_words_then_completes_and_disposes()
    {
        var session = new ScriptedSession(_ => [], onFinish: Final("마지막 말"));
        var (pub, sub) = Run(session);
        sub.Request(5);
        pub.SendNext(Frame());
        pub.SendComplete();
        var last = sub.ExpectNext();
        Assert.True(last.IsFinal);
        Assert.Equal("마지막 말", last.Text);
        sub.ExpectComplete();
        Assert.True(session.Finished);
        AwaitCondition(() => session.Disposed, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Korean_words_are_rebuilt_from_sentencepiece_markers()
    {
        // Measured (Zipformer and Nemotron alike): sherpa's text has no spaces between Hangul, its tokens
        // carry the word marker as a leading space — and an orphan " " token before 앉.
        Assert.Equal("지하철에서 다리를 벌리고 앉지 마라.",
            SherpaStreamingStt.Render("지하철에서다리를벌리고앉지마라.",
                [" 지하철", "에서", " 다리", "를", " 벌", "리고", " ", "앉", "지", " 마", "라", "."]));
        // A raw SentencePiece marker reads the same.
        Assert.Equal("부모가 저지르는", SherpaStreamingStt.Render("부모가저지르는", ["▁부모", "가", "▁저지르는"]));
        // No markers (character tokens) or tokens that do not spell the text: sherpa's text stands.
        Assert.Equal("안녕", SherpaStreamingStt.Render(" 안녕 ", ["안", "녕"]));
        Assert.Equal("안녕", SherpaStreamingStt.Render("안녕", ["▁<0xEC>", "녕"]));
    }

    [Fact]
    public void Voice_actor_routes_partials_and_finals_when_the_streaming_factory_answers()
    {
        var session = new ScriptedSession(n => n switch
        {
            0 => [Partial("오늘")],
            1 => [Final("오늘 날씨 어때?")],
            _ => [],
        });
        var engine = new ScriptedEngine(session);
        var partials = new List<string>();
        var finals = new List<string>();
        Func<ISpeechToText> batch = () => throw new InvalidOperationException("batch STT must not be built on the streaming path");
        Action<string, double> onFinal = (text, _) => { lock (finals) finals.Add(text); };
        Func<IStreamingSpeechToText?> streaming = () => engine;
        Action<SttUpdate> onPartial = u => { lock (partials) partials.Add(u.Text); };
        var actor = Sys.ActorOf(Props.Create(() => new VoiceStreamActor(batch, onFinal, null, null, null, streaming, onPartial)));

        actor.Tell(new StartListening(VadThreshold: 0.1f, Language: "ko"));
        actor.Tell(Frame());
        actor.Tell(Frame());

        AwaitCondition(() => { lock (finals) return finals.Count == 1; }, TimeSpan.FromSeconds(5));
        lock (partials) Assert.Equal(["오늘"], partials);
        lock (finals) Assert.Equal(["오늘 날씨 어때?"], finals);
        Assert.Equal("ko", engine.Language);
    }
}
