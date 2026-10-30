using Akka.Actor;
using Agent.Common.Audio;
using Agent.Common.Voice.Streams;

namespace Agent.Common.Voice.Streaming;

/// <summary>
/// One mic-on: the platform microphone (<see cref="AudioCapture.Platform"/>) feeding the voice actor
/// (<c>/user/stage/voice</c>, <see cref="Actors.VoiceStreamActor"/>). Every 50 ms frame is told to the actor as a
/// <see cref="MicFrame"/>; the actor decides at <see cref="StartListening"/> whether the streaming recognizer or the
/// VAD + batch STT graph hears it. Dispose is mic-off: the device stops and the graph is torn down (a streaming
/// graph flushes its last words first).
///
/// <para>UI-free so any host can drive it — the Avalonia AgentBot does; WPF keeps its own NAudio
/// <c>VoiceCaptureService</c>, which predates the shared capture layer.</para>
/// </summary>
public sealed class VoiceMicSession : IDisposable
{
    private readonly IActorRef _voice;
    private readonly IAudioFrameSource _mic;
    private bool _disposed;

    private VoiceMicSession(IActorRef voice, IAudioFrameSource mic)
    {
        _voice = voice;
        _mic = mic;
    }

    /// <summary>
    /// Frames are dropped while true — for the time the assistant's own voice is playing, so it is not heard as a
    /// new request. The device keeps running, so unmuting is instant.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>Raised on the capture thread when the device stops on its own (unplugged, access revoked).</summary>
    public event Action<Exception?>? DeviceStopped;

    /// <param name="isAgentMode">Picks the VAD profile for the batch fallback ("Auto" → Loose in agent mode).</param>
    public static VoiceMicSession Start(IActorRef voice, IAudioCapturePlatform platform, VoiceSettings settings, bool isAgentMode)
    {
        if (!platform.SupportsMicrophone)
            throw new PlatformNotSupportedException($"No microphone capture on this host ({platform.Name}).");

        var vad = VoiceVadSettings.BuildVadConfig(settings, isAgentMode);
        var mic = platform.OpenMicrophone(string.IsNullOrWhiteSpace(settings.InputDeviceId) ? null : settings.InputDeviceId);
        var session = new VoiceMicSession(voice, mic);
        mic.FrameAvailable += session.OnFrame;
        mic.Stopped += error => session.DeviceStopped?.Invoke(error);

        // StartListening before the first frame: the actor suspends its mailbox while a streaming model loads,
        // and the frames queue behind it.
        voice.Tell(new StartListening(
            VadThreshold: vad.VadThreshold,
            PreRollSeconds: vad.PreRollSeconds,
            UtteranceHangoverFrames: vad.UtteranceHangoverFrames,
            Language: settings.SttLanguage));
        mic.Start();
        return session;
    }

    private void OnFrame(byte[] pcm)
    {
        if (Muted || _disposed) return;
        _voice.Tell(new MicFrame(pcm, VoiceActivitySegmenter.Rms(pcm, pcm.Length)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mic.FrameAvailable -= OnFrame;
        try { _mic.Stop(); } catch { }
        try { _mic.Dispose(); } catch { }
        _voice.Tell(new StopListening());
    }
}
