using Akka.Actor;
using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Agents;
using Agent.Common.Audio;
using Agent.Common.Voice;
using Agent.Common.Voice.Streaming;
using Agent.Common.Voice.Streams;
using AgentZeroAvalonia.Actors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// AgentBot voice input (🎙 in the composer). The mic feeds ZeroCommon's voice actor
/// (<c>/user/stage/voice</c>) through <see cref="VoiceMicSession"/>; with Settings → Voice → STT on
/// <see cref="SttProviderNames.SherpaStreaming"/> (Nemotron 3.5 ASR, the official streaming ear) the words appear
/// in the input box while they are spoken and the utterance is sent the moment the recognizer's endpoint fires.
/// Any other provider runs the actor's VAD + batch graph and the text arrives once, after the silence.
///
/// <para>A partial only fills the box — it is never sent: in AI mode the agent loop has side effects, so it starts
/// on settled words (the final). Voice output is not here yet; the WPF host's TTS + barge-in is the next row of
/// README-Avalonia.</para>
/// </summary>
public partial class AgentBotViewModel
{
    [ObservableProperty] private bool _micOn;
    [ObservableProperty] private string _micTip = "Voice input — click to listen";

    /// <summary>False where the shared capture layer has no microphone yet (macOS, Linux).</summary>
    public bool MicSupported { get; } = AudioCapture.Platform.SupportsMicrophone;

    private IActorRef? _voice;
    private VoiceMicSession? _micSession;

    [RelayCommand]
    public async Task ToggleMicAsync()
    {
        if (MicOn)
        {
            StopMic("off");
            return;
        }
        if (!ActorSystemManager.IsInitialized)
        {
            AddNotice("🎙 The actor system is not running yet.");
            return;
        }
        try
        {
            _voice ??= (await ActorSystemManager.Stage.Ask<VoiceStreamCreated>(new CreateVoiceStream(
                // Both factories read the settings when the mic starts, so a provider change applies at the next 🎙.
                SttFactory: () => PortableVoiceRuntime.CreateStt(VoiceSettingsStore.Load(), out var why)
                                  ?? throw new InvalidOperationException(why),
                OnTranscript: (text, _) => Post(() => OnVoiceFinal(text)),
                StreamingSttFactory: () => PortableVoiceRuntime.CreateStreamingStt(VoiceSettingsStore.Load(), out _),
                OnPartial: update => Post(() => OnVoicePartial(update.Text))),
                TimeSpan.FromSeconds(5))).VoiceRef;

            var settings = VoiceSettingsStore.Load();
            var streaming = PortableVoiceRuntime.CreateStreamingStt(settings, out var whyNot);
            _micSession = VoiceMicSession.Start(_voice, AudioCapture.Platform, settings, isAgentMode: Mode == ChatMode.Ai);
            _micSession.DeviceStopped += error => Post(() => StopMic(error is null ? "device stopped" : error.Message));
            MicOn = true;
            MicTip = streaming is not null
                ? $"Listening — {streaming.ModelId}: words appear while you speak (click to stop)"
                : $"Listening — {settings.SttProvider}: text arrives after you pause (click to stop)";
            AddNotice(streaming is not null
                ? $"🎙 Listening with {streaming.ModelId} — the text appears as you speak."
                : $"🎙 Listening with {settings.SttProvider} (after each pause). For text while you speak: Settings → Voice → {SttProviderNames.SherpaStreaming} ({whyNot}).");
            AppLogger.Log($"[BOT-Voice] Mic ON | provider={settings.SttProvider} streaming={streaming?.ModelId ?? "-"} mode={Mode}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[BOT-Voice] Mic start failed: {ex}");
            StopMic("start failed");
            AddNotice($"🎙 Could not start the microphone: {ex.Message}");
        }
    }

    /// <summary>The recognizer's text so far — shown, never sent.</summary>
    internal void OnVoicePartial(string text)
    {
        if (!MicOn) return;
        Input = text;
    }

    /// <summary>A settled utterance: what the composer would send if the person had typed it.</summary>
    internal void OnVoiceFinal(string text)
    {
        if (!MicOn || string.IsNullOrWhiteSpace(text)) return;
        Input = text;
        Send();
    }

    private void StopMic(string reason)
    {
        var wasOn = MicOn;
        MicOn = false;
        MicTip = "Voice input — click to listen";
        try { _micSession?.Dispose(); } catch { }
        _micSession = null;
        if (wasOn) AppLogger.Log($"[BOT-Voice] Mic OFF ({reason})");
    }

    /// <summary>Tests switch the mic state without a device.</summary>
    internal void SetMicOnForTest(bool on) => MicOn = on;
}
