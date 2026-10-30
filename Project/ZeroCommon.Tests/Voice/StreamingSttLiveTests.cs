// ───────────────────────────────────────────────────────────
// Live streaming STT — runs only where a streaming model is installed
// (Settings → Voice → Streaming → Download, or
// `AgentZeroWearable.exe --download-stream-stt --stream-model <id>`).
// Uses the Korean test clips the Zipformer archive ships with.
// ───────────────────────────────────────────────────────────

using Agent.Common.Audio;
using Agent.Common.Voice;
using Agent.Common.Voice.Streaming;
using Xunit.Abstractions;

namespace ZeroCommon.Tests.Voice;

public sealed class StreamingSttLiveTests(ITestOutputHelper output)
{
    private static string? TestClip(int n)
    {
        var dir = Path.Combine(StreamingSttModelCatalog.ModelDirectory(StreamingSttModelCatalog.ZipformerKorean), "test_wavs");
        var path = Path.Combine(dir, $"{n}.wav");
        return File.Exists(path) ? path : null;
    }

    public static TheoryData<string> Models() => new() { "zipformer-ko", "nemotron-3.5-560ms" };

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Korean_clip_streams_partials_then_a_spaced_final(string modelId)
    {
        var model = StreamingSttModelCatalog.Resolve(modelId);
        if (!StreamingSttModelCatalog.IsInstalled(model) || TestClip(1) is not { } clip)
        {
            output.WriteLine($"skipped — {modelId} or the Zipformer test clips are not installed");
            return;
        }

        using var engine = new SherpaStreamingStt(model);
        Assert.True(await engine.EnsureReadyAsync(new Progress<string>(output.WriteLine)));
        var pcm = WavPcm.To16kMono(await File.ReadAllBytesAsync(clip));
        var silence = new byte[16000 * 2 * 3 / 2];

        using var session = engine.StartSession("ko");
        var updates = new List<SttUpdate>();
        foreach (var chunk in pcm.Concat(silence).Chunk(3200)) updates.AddRange(session.Accept(chunk));
        if (session.Finish() is { } last) updates.Add(last);
        foreach (var u in updates) output.WriteLine($"{(u.IsFinal ? "FINAL" : "part ")} {u.AudioSeconds:F2}s {u.Text}");

        Assert.Contains(updates, u => !u.IsFinal);
        var final = Assert.Single(updates, u => u.IsFinal);
        // "지하철에서 다리를 벌리고 앉지 마라." — the words must come back apart.
        Assert.Contains("지하철에서", final.Text);
        Assert.Contains(' ', final.Text);
    }

    /// <summary>A microphone that plays a WAV in 50 ms frames, at real-time pace, then silence.</summary>
    private sealed class WavMicrophone(byte[] pcm) : IAudioCapturePlatform, IAudioFrameSource
    {
        private CancellationTokenSource? _cts;
        public string Name => "wav";
        public bool SupportsMicrophone => true;
        public bool SupportsLoopback => false;
        public bool IsCapturing => _cts is not null;
        public event Action<byte[]>? FrameAvailable;
        public event Action<Exception?>? Stopped;
        public IReadOnlyList<AudioDeviceInfo> ListMicrophones() => [new("0", "wav", true)];
        public IReadOnlyList<AudioDeviceInfo> ListLoopbackDevices() => [];
        public IAudioFrameSource OpenMicrophone(string? deviceId) => this;
        public IAudioFrameSource OpenLoopback(string? deviceId) => throw new NotSupportedException();

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                foreach (var frame in pcm.Concat(new byte[32000 * 2]).Chunk(1600))
                {
                    if (ct.IsCancellationRequested) return;
                    FrameAvailable?.Invoke(frame);
                    await Task.Delay(50);
                }
                Stopped?.Invoke(null);
            });
        }

        public void Stop() => _cts?.Cancel();
        public void Dispose() => Stop();
    }

    [Fact]
    public async Task Mic_session_through_the_voice_actor_sends_one_settled_sentence()
    {
        // The Avalonia AgentBot's path minus the UI: VoiceMicSession → /voice actor → StreamingSttFlow → callbacks.
        var model = StreamingSttModelCatalog.Default;
        if (!StreamingSttModelCatalog.IsInstalled(model) || TestClip(3) is not { } clip)
        {
            output.WriteLine($"skipped — {model.Id} or the Zipformer test clips are not installed");
            return;
        }
        var settings = new VoiceSettings { SttProvider = SttProviderNames.SherpaStreaming, SttLanguage = "ko" };
        var partials = new List<string>();
        var finals = new List<string>();
        using var system = Akka.Actor.ActorSystem.Create("mic-session-live");
        Func<ISpeechToText> batch = () => throw new InvalidOperationException("the streaming path must not build batch STT");
        Action<string, double> onFinal = (text, _) => { lock (finals) finals.Add(text); };
        Func<IStreamingSpeechToText?> streaming = () => PortableVoiceRuntime.CreateStreamingStt(settings, out _);
        Action<SttUpdate> onPartial = u => { lock (partials) partials.Add(u.Text); };
        var voice = system.ActorOf(Akka.Actor.Props.Create(() =>
            new Agent.Common.Actors.VoiceStreamActor(batch, onFinal, null, null, null, streaming, onPartial)));

        var mic = new WavMicrophone(WavPcm.To16kMono(await File.ReadAllBytesAsync(clip)));
        var started = DateTime.UtcNow;
        using (var session = VoiceMicSession.Start(voice, mic, settings, isAgentMode: true))
        {
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
            {
                lock (finals) if (finals.Count > 0) break;
                await Task.Delay(100);
            }
        }
        lock (partials) foreach (var p in partials) output.WriteLine($"part  {p}");
        lock (finals) foreach (var f in finals) output.WriteLine($"FINAL {f} after {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        lock (partials) Assert.NotEmpty(partials);
        lock (finals) Assert.Contains("주민등록증", Assert.Single(finals));
    }
}
