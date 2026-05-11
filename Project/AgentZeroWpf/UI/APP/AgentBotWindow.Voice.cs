using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Akka.Actor;
using Agent.Common;
using Agent.Common.Voice;
using Agent.Common.Voice.Streams;
using AgentZeroWpf.Actors;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.UI.APP;

/// <summary>
/// AgentBot voice-input runtime.
///
/// Reuses the same Voice settings store and STT factory the Voice Test panel
/// owns — sensitivity, model, language, input device — so toggling the mic
/// here behaves identically to the panel that proved them. Routing of the
/// transcript is mode-aware:
///
///   • Chat / Key mode → fills <c>txtInput</c> and calls <see cref="SendCurrentInput"/>,
///     which forwards to the active terminal.
///   • Ai mode         → same path; <see cref="SendCurrentInput"/> already routes
///     AIMODE inputs through <c>SendThroughAiToolLoopAsync</c>.
///
/// AUTO toggle semantics:
///   ON  — mic on, dual-VAD running, every <c>UtteranceEnded</c> auto-sends.
///   OFF — capture stopped, any in-flight pipeline cancelled, waveform reset.
///
/// All NAudio events (AmplitudeChanged / UtteranceEnded) fire from the
/// capture thread; UI mutations marshal back through <see cref="Dispatcher"/>.
/// </summary>
public partial class AgentBotWindow
{
    private VoiceCaptureService? _voiceCapture;
    private CancellationTokenSource? _voicePipelineCts;
    private volatile bool _voicePipelineBusy;
    private bool _voiceMicOn;

    // Per-utterance timing checkpoints. Stage 1 of finding "where did the
    // 10 seconds go?" — without these, all we know is "transcript appeared
    // late." With them: VAD hangover vs STT prep vs STT inference vs
    // dispatch are separable line items in the log.
    private DateTime? _utteranceStartedAtUtc;
    private DateTime? _utteranceEndedAtUtc;
    private DateTime? _pipelineEnqueuedAtUtc;

    // Stream pipeline (P1+) — non-null when VoiceSettings.UseStreamPipeline is true.
    private IActorRef? _voiceStreamRef;
    private Action<MicFrame>? _voiceFrameForwarder;

    // Test tools popup (Virtual voice today; future virtual keyboard, etc.).
    private TestToolsWindow? _testToolsWindow;

    // True while we're applying VoiceSettings → UI on mic-on transitions,
    // so the Slider.ValueChanged handler doesn't re-save the same value
    // back to disk on every initialisation.
    private bool _suppressMicVolumePersist;

    private void OnVoiceMicToggle(object sender, RoutedEventArgs e)
    {
        if (_voiceMicOn)
            StopVoiceMic("user toggled OFF");
        else
            _ = StartVoiceMicAsync();
    }

    private void OnVoiceCancelInflight(object sender, RoutedEventArgs e)
    {
        try { _voicePipelineCts?.Cancel(); } catch { }
        SetVoiceStatus("Cancelled.", System.Windows.Media.Brushes.Goldenrod);
    }

    private void OnVoiceMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_voiceCapture is null) return;
        var newState = !_voiceCapture.Muted;
        SetVoiceMicMuted(newState, source: "user");
        // Persist user-driven toggles. Auto-mute from TestToolsWindow is
        // restored on its own and shouldn't permanently flip the user's
        // saved preference.
        try
        {
            var v = VoiceSettingsStore.Load();
            v.MicMuted = newState;
            VoiceSettingsStore.Save(v);
        }
        catch (Exception ex) { AppLogger.LogError("[BOT-Voice] Persist MicMuted failed", ex); }
    }

    /// <summary>
    /// Public toggle for soft-mute. Capture continues (level meter stays
    /// alive) but the VAD / segmenter / STT pipeline ignores incoming
    /// frames. Used both by the toolbar mute button and by
    /// <see cref="UI.APP.TestToolsWindow"/> auto-mute during virtual
    /// voice testing — the bypass path needs AskBot's mic OFF logically
    /// while leaving it physically capturing for fast resume.
    ///
    /// Note: this method does NOT persist the new state. The user-driven
    /// toolbar toggle persists via <see cref="OnVoiceMuteToggle"/>; the
    /// auto-mute path deliberately doesn't, so a transient test-tool
    /// mute/unmute cycle doesn't overwrite the user's saved preference.
    /// </summary>
    public void SetVoiceMicMuted(bool muted, string source = "external")
    {
        if (_voiceCapture is null) return;
        if (_voiceCapture.Muted == muted) return;
        _voiceCapture.Muted = muted;
        ApplyVoiceMuteUi(muted);
        AppLogger.Log($"[BOT-Voice] Mic {(muted ? "MUTED" : "UNMUTED")} ({source})");
    }

    private void OnVoiceMicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sldVoiceMicVolume is null) return;
        var pct = (int)Math.Round(e.NewValue);
        if (txtVoiceMicVolumeLabel is not null)
            txtVoiceMicVolumeLabel.Text = $"{pct}%";

        // Apply to system. Even during silent UI initialisation, applying
        // is fine — slider value matches what we just read from settings.
        MicVolumeService.SetVolume(pct / 100.0f);

        if (_suppressMicVolumePersist) return;

        try
        {
            var v = VoiceSettingsStore.Load();
            v.MicVolumePercent = pct;
            VoiceSettingsStore.Save(v);
        }
        catch (Exception ex) { AppLogger.LogError("[BOT-Voice] Persist MicVolumePercent failed", ex); }
    }

    /// <summary>True when the mic is on AND not muted — i.e. live to STT.</summary>
    public bool IsVoiceMicLive() => _voiceMicOn && _voiceCapture is not null && !_voiceCapture.Muted;

    /// <summary>
    /// Inject a transcript into AgentBot's terminal pipeline as if it had
    /// arrived from the live mic STT path. Used by
    /// <see cref="UI.APP.TestToolsWindow"/>'s acoustic-loop test mode so
    /// the synthesised voice can drive AgentBot deterministically without
    /// depending on the speaker → microphone round-trip (which Windows
    /// echo cancellation + noise suppression unreliably zero out).
    ///
    /// <para>Behavioural parity with the live mic path: SendCurrentInput
    /// already routes through AIMODE / Chat / Key dispatchers exactly as
    /// it would for a microphone-captured transcript, so AgentBot can't
    /// tell the source apart.</para>
    /// </summary>
    public void SendVoiceTranscript(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(SendVoiceTranscript), DispatcherPriority.Normal, transcript);
            return;
        }
        try
        {
            txtInput.Text = transcript;
            txtInput.CaretIndex = transcript.Length;
            SendCurrentInput();
            AppLogger.Log($"[BOT-Voice] Transcript injected from test-tools ({transcript.Length} chars)");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[BOT-Voice] SendVoiceTranscript failed", ex);
        }
    }

    private void ApplyVoiceMuteUi(bool muted)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<bool>(ApplyVoiceMuteUi), DispatcherPriority.Normal, muted);
            return;
        }
        if (btnVoiceMute is null) return;
        btnVoiceMute.Foreground = muted
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Media.Brush)FindResource("TextDim");
        btnVoiceMute.ToolTip = muted
            ? "Mic is MUTED (frames ignored). Click to unmute."
            : "Mic is LIVE. Click to mute (frames captured but ignored by VAD/STT).";
    }

    private async Task StartVoiceMicAsync()
    {
        try
        {
            var v = VoiceSettingsStore.Load();

            _voiceCapture ??= new VoiceCaptureService();
            _voicePipelineCts?.Dispose();
            _voicePipelineCts = new CancellationTokenSource();

            // M0015 / 후속 진행 #1: pass current ChatMode as the agent-mode
            // hint so the saved profile "Auto" lands on Loose during AiMode
            // (long-form agent interaction, often off-mic) and on Strict
            // for ChatMode / Key. Explicit Strict / Loose tokens still win.
            var isAgentMode = _chatMode == ChatMode.Ai;
            var threshold = VoiceRuntimeFactory.SensitivityToThreshold(
                100 - v.VadThreshold, v, isAgentMode);
            _voiceCapture.VadThreshold = threshold;

            // Always wire the level meter — needed by both pipelines.
            _voiceCapture.AmplitudeChanged -= OnVoiceAmplitudeChanged;
            _voiceCapture.AmplitudeChanged += OnVoiceAmplitudeChanged;

            // Detach legacy hooks defensively (they may be re-attached below).
            _voiceCapture.UtteranceStarted -= OnVoiceUtteranceStarted;
            _voiceCapture.UtteranceEnded -= OnVoiceUtteranceEnded;
            if (_voiceFrameForwarder is not null)
                _voiceCapture.FrameAvailable -= _voiceFrameForwarder;
            _voiceFrameForwarder = null;

            if (v.UseStreamPipeline)
            {
                // Stream path: segmenter Flow handles VAD+buffering, so the
                // legacy PCM buffer + utterance events are unused.
                _voiceCapture.BufferPcm = false;
                await StartStreamPipelineAsync(v, threshold);
            }
            else
            {
                _voiceCapture.BufferPcm = true;
                _voiceCapture.UtteranceStarted += OnVoiceUtteranceStarted;
                _voiceCapture.UtteranceEnded += OnVoiceUtteranceEnded;
            }

            var deviceNumber = VoiceRuntimeFactory.ParseDeviceNumber(v.InputDeviceId);
            _voiceCapture.Start(deviceNumber);

            // Apply persisted mute state. New VoiceCaptureService starts
            // unmuted; honour the user's saved preference here.
            if (v.MicMuted) _voiceCapture.Muted = true;

            _voiceMicOn = true;
            ApplyVoiceMicUi(on: true);
            var modeTag = v.UseStreamPipeline ? "stream" : "batch";
            SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
            AppLogger.Log($"[BOT-Voice] Mic ON | provider={v.SttProvider} sens={v.VadThreshold} device={deviceNumber} mode={modeTag}");

            // Warm the STT factory off-thread so the first utterance doesn't
            // pay the cold-start tax (Whisper.net "small" is ~487 MB on CPU).
            // Stream path warms via the SttWorkerActor on first segment instead.
            if (!v.UseStreamPipeline)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stt = VoiceRuntimeFactory.BuildStt(v);
                        if (stt is null) return;
                        await stt.EnsureReadyAsync(
                            new Progress<string>(msg => SetVoiceStatus(msg, System.Windows.Media.Brushes.SkyBlue)),
                            _voicePipelineCts?.Token ?? CancellationToken.None);
                        SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
                    }
                    catch (Exception ex) { AppLogger.LogError("[BOT-Voice] STT warm-up failed", ex); }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[BOT-Voice] Mic start failed", ex);
            SetVoiceStatus($"✗ {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
            StopVoiceMic("start failed");
        }
    }

    /// <summary>
    /// Bring up the Akka.Streams INPUT pipeline. Reuses
    /// <see cref="EnsureVoiceStreamRefAsync"/> so the actor is created once
    /// and shared across input (mic → STT) and output (AI bubble → TTS)
    /// concerns. Transcript callback fires off the actor's Receive thread —
    /// marshal back to the dispatcher before touching txtInput.
    /// </summary>
    private async Task StartStreamPipelineAsync(VoiceSettings v, float threshold)
    {
        await EnsureVoiceStreamRefAsync();
        if (_voiceStreamRef is null) return;

        _voiceFrameForwarder = frame => _voiceStreamRef?.Tell(frame);
        _voiceCapture!.FrameAvailable += _voiceFrameForwarder;

        // Drive PreRoll / Hangover from the active sensitivity profile so
        // Loose lecture-tuning takes effect end-to-end (M0015). Mode-aware
        // overload honours the "Auto" token: AiMode → Loose, others →
        // Strict. Threshold is already profile-aware via the caller
        // computing it with the same isAgentMode flag.
        var vadCfg = VoiceRuntimeFactory.BuildVadConfig(v, isAgentMode: _chatMode == ChatMode.Ai);
        _voiceStreamRef.Tell(new StartListening(
            VadThreshold: threshold,
            PreRollSeconds: vadCfg.PreRollSeconds,
            UtteranceHangoverFrames: vadCfg.UtteranceHangoverFrames,
            MicBufferSize: 64,
            SttParallelism: Math.Max(1, v.StreamSttParallelism),
            Language: v.SttLanguage));
    }

    /// <summary>
    /// Idempotent lazy creation of <see cref="_voiceStreamRef"/>. Used both by
    /// the streaming INPUT path (mic on) and by the TTS-only OUTPUT path
    /// (AI mode bubble → speak). Factories load voice settings *at call time*
    /// so the user's currently-saved TTS provider/voice applies on each new
    /// worker spawn — no need to recreate the actor when settings change
    /// (the existing pool will however keep using the provider it loaded
    /// originally, which is acceptable for MVP).
    ///
    /// <para>OnTtsPlaybackChanged drives the mic auto-mute envelope: when the
    /// AI's voice is playing, the mic must not capture it back as a new
    /// utterance — otherwise the bot transcribes its own speech, sends it
    /// as a new turn, and the AI responds without tools because it's just
    /// "answering" its own previous answer. Auto-mute breaks the loop.</para>
    /// </summary>
    private async Task EnsureVoiceStreamRefAsync()
    {
        if (_voiceStreamRef is not null) return;
        try
        {
            var created = await ActorSystemManager.Stage.Ask<VoiceStreamCreated>(
                new CreateVoiceStream(
                    SttFactory: () => VoiceRuntimeFactory.BuildStt(VoiceSettingsStore.Load())
                        ?? throw new InvalidOperationException("STT provider unavailable"),
                    OnTranscript: OnVoiceStreamTranscript,
                    TtsFactory: () => VoiceRuntimeFactory.BuildTts(VoiceSettingsStore.Load())
                        ?? throw new InvalidOperationException("TTS provider unavailable"),
                    PlaybackFactory: () => new NAudioPlaybackQueue(),
                    OnTtsPlaybackChanged: OnTtsPlaybackChanged),
                TimeSpan.FromSeconds(5));
            _voiceStreamRef = created.VoiceRef;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[BOT-Voice] EnsureVoiceStreamRefAsync failed", ex);
        }
    }

    // True iff *we* applied the current mute as part of an active TTS burst.
    // Self-tracked instead of derived from a snapshot of the user's prior
    // mute state — earlier we used `_userMutedBeforeTts` and a spurious
    // double-start event (NAudio queue draining clip-by-clip) would re-snapshot
    // the already-muted state as `True` and latch the mic permanently muted
    // because the auto-unmute path checked that snapshot. With this flag the
    // unmute decision depends only on whether *we* were the muter.
    private bool _autoMutedByTts;

    /// <summary>
    /// Fires from the voice actor when the TTS playback queue transitions
    /// idle ↔ busy. We auto-mute the mic for the duration so the bot's own
    /// voice doesn't bleed back into Whisper as a new utterance — that
    /// feedback loop both echoes the bot and silently triggers tool-less
    /// AI turns that look like "function calls were skipped".
    /// </summary>
    private void OnTtsPlaybackChanged(bool isPlaying)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<bool>(OnTtsPlaybackChanged), isPlaying);
            return;
        }
        if (_voiceCapture is null) return;

        if (isPlaying)
        {
            // Mute only if not already muted. If the user manually muted
            // before TTS started, we leave their state alone (and importantly
            // do NOT set _autoMutedByTts) so the natural drain at end won't
            // unmute against their will.
            if (!_voiceCapture.Muted)
            {
                SetVoiceMicMuted(true, source: "tts-auto");
                _autoMutedByTts = true;
            }
            AppLogger.Log($"[BOT-Voice] TTS started — autoMuted={_autoMutedByTts} micMuted={_voiceCapture.Muted}");
        }
        else
        {
            // Unmute only if WE muted — never override a user-driven mute.
            if (_autoMutedByTts)
            {
                SetVoiceMicMuted(false, source: "tts-auto");
                _autoMutedByTts = false;
            }
            AppLogger.Log($"[BOT-Voice] TTS stopped — autoMutedNow={_autoMutedByTts} micMuted={_voiceCapture.Muted}");
        }
    }

    /// <summary>
    /// Called from VoiceStreamActor's Receive thread when the segmenter +
    /// STT pool produce a non-empty transcript. Same downstream behaviour as
    /// the legacy <see cref="OnVoiceUtteranceEnded"/> path: fill txtInput
    /// then SendCurrentInput so AIMODE / Chat routing stays unchanged.
    /// </summary>
    private void OnVoiceStreamTranscript(string transcript, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        if (_voicePipelineCts?.IsCancellationRequested == true) return;

        // Drop known Whisper hallucination outros ("감사합니다" etc.) emitted
        // on near-silence input — see WhisperHallucinationFilter. The stream
        // pipeline has no peak/VAR gate yet, so this is the only line of
        // defence against forwarding "thanks for watching" as a user prompt.
        if (WhisperHallucinationFilter.IsLikelyHallucination(transcript))
        {
            AppLogger.Log($"[BOT-Voice] (stream) dropped — Whisper hallucination pattern: \"{transcript}\" ({durationSeconds:F2}s)");
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => DispatchVoiceTranscriptOnUi(transcript, "stream")));
        AppLogger.Log($"[BOT-Voice] (stream) Transcript sent ({transcript.Length} chars, {durationSeconds:F2}s utterance)");
    }

    /// <summary>
    /// Shared dispatch logic used by both voice pipelines once a transcript
    /// has cleared the hallucination filter. Runs on the UI thread.
    ///
    /// <para>Three intents are recognised before falling back to
    /// <c>SendCurrentInput()</c>:</para>
    /// <list type="bullet">
    ///   <item><b>StopSpeaking</b> — say "그만" to interrupt any TTS that is
    ///     currently playing. Sends <c>BargeIn</c> to the voice actor; the
    ///     OUTPUT graph (kill switch + token queue + playback) tears down
    ///     atomically. No LLM dispatch.</item>
    ///   <item><b>SummarizeTerminal</b> — say "터미널 작업 요약해" to ask the
    ///     AI to summarise the active terminal's screen text. We snapshot
    ///     <c>GetConsoleText()</c> once at request time and embed it inline
    ///     in the prompt — that way the LLM gets a static, complete window
    ///     of output instead of racing the still-streaming PTY buffer
    ///     (which would risk re-summarising overlapping chunks across
    ///     repeated requests). Only fires in AI mode; in other modes the
    ///     phrase falls through as regular speech.</item>
    ///   <item><b>PassThrough</b> — fill <c>txtInput</c> and call
    ///     <see cref="SendCurrentInput"/> as before.</item>
    /// </list>
    /// </summary>
    private void DispatchVoiceTranscriptOnUi(string transcript, string sourceTag)
    {
        if (!_voiceMicOn) return;
        var intent = VoiceCommandInterceptor.Classify(transcript);

        switch (intent)
        {
            case VoiceCommandIntent.StopSpeaking:
                AppLogger.Log($"[BOT-Voice] ({sourceTag}) stop command — sending BargeIn");
                _voiceStreamRef?.Tell(new BargeIn());
                SetVoiceStatus("(stopped speaking)", System.Windows.Media.Brushes.Goldenrod);
                return;

            case VoiceCommandIntent.DelegateOn:
                AppLogger.Log($"[BOT-Voice] ({sourceTag}) delegate ON — \"{transcript}\"");
                EnterDelegationMode();
                return;

            case VoiceCommandIntent.DelegateOff:
                AppLogger.Log($"[BOT-Voice] ({sourceTag}) delegate OFF — \"{transcript}\"");
                ExitDelegationMode();
                return;

            case VoiceCommandIntent.SummarizeTerminal when _chatMode == ChatMode.Ai:
                var session = _getActiveSession?.Invoke();
                var terminalText = session?.GetConsoleText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(terminalText))
                {
                    AppLogger.Log($"[BOT-Voice] ({sourceTag}) summarize — no active terminal text");
                    AddSystemMessage("활성 터미널 출력이 비어 있어 요약할 내용이 없습니다.");
                    return;
                }
                AppLogger.Log($"[BOT-Voice] ({sourceTag}) summarize-terminal | terminalChars={terminalText.Length}");
                txtInput.Clear();
                _ = SendThroughAiToolLoopAsync(
                    BuildTerminalSummaryPrompt(transcript, terminalText),
                    transcript);
                return;

            case VoiceCommandIntent.PassThrough:
            case VoiceCommandIntent.SummarizeTerminal: // fell through (not AI mode)
            default:
                var v = VoiceSettingsStore.Load();
                txtInput.Text = transcript;
                txtInput.CaretIndex = transcript.Length;
                SendCurrentInput();
                SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
                return;
        }
    }

    /// <summary>
    /// Build the LLM prompt for a "summarize terminal" voice command. The
    /// snapshot is fenced in a code block so the model parses it as a verbatim
    /// observation instead of treating individual lines as separate
    /// instructions. Verbose by design — easier for a small local LLM to
    /// follow than a terse one-liner.
    /// </summary>
    private static string BuildTerminalSummaryPrompt(string userPhrase, string terminalText)
    {
        return
            $"{userPhrase}\n\n" +
            "아래는 사용자가 보고 있는 활성 터미널의 현재 화면 출력입니다. " +
            "이 내용을 한국어로 간결하게 요약해 주세요. " +
            "어떤 작업이 실행되었고 어떤 결과 / 에러가 있었는지 핵심만 짚어주세요.\n\n" +
            "```\n" + terminalText + "\n```";
    }

    private void StopVoiceMic(string reason)
    {
        try { _voicePipelineCts?.Cancel(); } catch { }
        try { _voiceCapture?.Stop(); } catch { }
        if (_voiceCapture is not null)
        {
            _voiceCapture.AmplitudeChanged -= OnVoiceAmplitudeChanged;
            _voiceCapture.UtteranceStarted -= OnVoiceUtteranceStarted;
            _voiceCapture.UtteranceEnded -= OnVoiceUtteranceEnded;
            if (_voiceFrameForwarder is not null)
                _voiceCapture.FrameAvailable -= _voiceFrameForwarder;
        }
        _voiceFrameForwarder = null;

        // Stream pipeline: tell the actor to tear down its graph + STT pool.
        // The actor itself stays alive (Stage caches the singleton) — next
        // StartListening will re-materialize.
        try { _voiceStreamRef?.Tell(new StopListening()); } catch { }
        // Also drain any in-flight TTS playback. StopListening only kills the
        // INPUT graph; without the BargeIn the OUTPUT graph keeps playing the
        // current AI bubble's audio even after the user toggled the mic off,
        // which contradicts "음성모드 진입 시에만 음성 출력". Mute this round.
        try { _voiceStreamRef?.Tell(new BargeIn()); } catch { }

        _voiceMicOn = false;
        ApplyVoiceMicUi(on: false);
        AppLogger.Log($"[BOT-Voice] Mic OFF ({reason})");
    }

    private void ApplyVoiceMicUi(bool on)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<bool>(ApplyVoiceMicUi), DispatcherPriority.Normal, on);
            return;
        }

        if (btnVoiceMic is not null)
        {
            // Segoe MDL2 Assets glyphs — picked via raw codepoint so the
            // file stays ASCII-clean. E720 = mic, E74F = mute.
            btnVoiceMic.Content = on ? "" : "";
            btnVoiceMic.Foreground = on
                ? (System.Windows.Media.Brush)FindResource("CyanBrush")
                : (System.Windows.Media.Brush)FindResource("TextDim");
            btnVoiceMic.ToolTip = on
                ? "Voice AUTO is ON — click to stop listening"
                : "Voice AUTO is OFF — click to start listening";
        }

        if (pnlVoiceWaveStrip is not null)
            pnlVoiceWaveStrip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (voiceWave is not null)
            voiceWave.IsActive = on;

        if (btnVoiceMute is not null)
        {
            btnVoiceMute.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Reset visual to unmuted on every mic-on transition; capture is
            // recreated unmuted so the UI must follow.
            if (on) ApplyVoiceMuteUi(_voiceCapture?.Muted ?? false);
        }

        // System mic volume slider — show only when mic ON, populate from
        // settings on transition, and apply to the system endpoint.
        if (sldVoiceMicVolume is not null && txtVoiceMicVolumeLabel is not null)
        {
            sldVoiceMicVolume.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            txtVoiceMicVolumeLabel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                _suppressMicVolumePersist = true;
                try
                {
                    var v = VoiceSettingsStore.Load();
                    int pct;
                    if (v.MicVolumePercent < 0)
                    {
                        // First-launch / never-set: read current system level
                        // so the slider shows what Windows is at instead of
                        // forcing it to a default.
                        var current = MicVolumeService.GetVolume();
                        pct = current is { } c ? (int)Math.Round(c * 100) : 100;
                    }
                    else
                    {
                        pct = Math.Clamp(v.MicVolumePercent, 0, 100);
                        MicVolumeService.SetVolume(pct / 100f);
                    }
                    sldVoiceMicVolume.Value = pct;
                    sldVoiceMicVolume.IsEnabled = MicVolumeService.IsAvailable();
                    txtVoiceMicVolumeLabel.Text = $"{pct}%";
                    if (!sldVoiceMicVolume.IsEnabled)
                        txtVoiceMicVolumeLabel.Text = "n/a";
                }
                finally { _suppressMicVolumePersist = false; }
            }
        }
    }

    private void SetVoiceStatus(string text, System.Windows.Media.Brush? brush = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string, System.Windows.Media.Brush?>(SetVoiceStatus), DispatcherPriority.Normal, text, brush);
            return;
        }
        if (txtVoiceStatus is null) return;
        txtVoiceStatus.Text = text;
        if (brush is not null) txtVoiceStatus.Foreground = brush;
    }

    private static string ShortProviderLabel(VoiceSettings v) => v.SttProvider switch
    {
        SttProviderNames.WhisperLocal => $"Whisper {v.SttWhisperModel}",
        SttProviderNames.OpenAIWhisper => "OpenAI Whisper",
        SttProviderNames.WebnoriGemma => "Webnori Gemma",
        SttProviderNames.LocalGemma => "Local Gemma",
        _ => v.SttProvider,
    };

    private void OnVoiceAmplitudeChanged(float rms)
    {
        // Capture thread — VoiceWaveformIndicator marshals internally.
        voiceWave?.Push(rms);
    }

    private void OnVoiceUtteranceStarted()
    {
        _utteranceStartedAtUtc = DateTime.UtcNow;
        _voiceCapture?.SeedBufferWithPreRoll();
        AppLogger.Log("[BOT-Voice-pipe] [t0] utterance-start");
    }

    private void OnVoiceUtteranceEnded()
    {
        if (_voiceCapture is null) return;
        _utteranceEndedAtUtc = DateTime.UtcNow;
        var pcm = _voiceCapture.ConsumePcmBuffer();
        var pcmSec = pcm.Length / 32_000.0; // 16-bit PCM 16 kHz mono = 32 kB/s
        var sinceStartMs = _utteranceStartedAtUtc is { } t0
            ? (int)(_utteranceEndedAtUtc!.Value - t0).TotalMilliseconds
            : -1;
        AppLogger.Log($"[BOT-Voice-pipe] [t1] utterance-end | t1-t0={sinceStartMs}ms · pcm={pcm.Length} bytes (~{pcmSec:F2}s audio · includes ~2s VAD hangover + 1s pre-roll)");

        if (pcm.Length < 8000)
        {
            AppLogger.Log("[BOT-Voice-pipe] dropped — utterance < 0.25s");
            return;
        }

        if (_voicePipelineBusy)
        {
            // Skip — a previous transcription hasn't finished. We could queue
            // it but that'd let utterances stack while a slow STT crawls.
            // Simpler: drop, surface "busy" so the user knows to pause briefly.
            AppLogger.Log("[BOT-Voice-pipe] dropped — pipeline busy (previous turn still in flight)");
            SetVoiceStatus("Busy — pause briefly…", System.Windows.Media.Brushes.Goldenrod);
            return;
        }

        _pipelineEnqueuedAtUtc = DateTime.UtcNow;
        _ = Task.Run(() => RunVoicePipelineOnceAsync(pcm));
    }

    private async Task RunVoicePipelineOnceAsync(byte[] pcm)
    {
        if (_voicePipelineBusy) return;
        _voicePipelineBusy = true;
        var ct = _voicePipelineCts?.Token ?? CancellationToken.None;

        // Per-stage Stopwatch instrumentation. The legacy path before today
        // logged only "Transcript sent (N chars)" — useless for diagnosing
        // "why does this take 10 seconds?". Now: enqueue→prep→transcribe→
        // dispatch are each measured. Combined with the t0/t1 log lines
        // from OnVoiceUtteranceStarted/Ended, the full breakdown of perceived
        // latency is in the log without re-instrumenting per debug session.
        var pipelineStartUtc = DateTime.UtcNow;
        var enqueueLagMs = _pipelineEnqueuedAtUtc is { } enq
            ? (int)(pipelineStartUtc - enq).TotalMilliseconds
            : -1;
        var pcmSec = pcm.Length / 32_000.0;

        SetInflightCancelVisible(true);
        try
        {
            var v = VoiceSettingsStore.Load();
            var stt = VoiceRuntimeFactory.BuildStt(v);
            if (stt is null)
            {
                SetVoiceStatus($"✗ STT '{v.SttProvider}' unavailable", System.Windows.Media.Brushes.OrangeRed);
                AppLogger.Log($"[BOT-Voice-pipe] dropped — STT '{v.SttProvider}' unavailable");
                return;
            }

            // ── Voice-activity gate ──
            //
            // Whisper hallucinates YouTube-creator outros on near-silence
            // input ("시청해주셔서 감사합니다"). VAD set the trigger
            // (sensitivity is user-controlled, often max'd for quiet voice),
            // so the captured PCM may contain a brief speech burst inside
            // a much longer envelope of pre-roll + hangover silence.
            //
            // Naïve RMS-over-whole-clip is the wrong measure here — silence
            // padding pulls the average way down even when the speech itself
            // is clear. Real-world data: user's audible "안녕하세요" came
            // back as peak=-27 dBFS / rms=-56 dBFS — peak honestly reflects
            // the speech burst, but rms tracks the surrounding silence.
            //
            // Two-tier gate:
            //   (1) Peak — must reach at least -38 dBFS somewhere in the clip
            //       (any actual audible moment).
            //   (2) Voice activity ratio — fraction of 50 ms frames whose
            //       peak exceeds -45 dBFS. Distinguishes "0.5 s of sustained
            //       speech surrounded by silence" (≈ 17 % ratio) from a
            //       single typing click in 3 s of silence (≈ 1.7 %).
            var (peakAmp, rmsAmp) = MeasurePcm16Level(pcm);
            var peakDb = peakAmp > 0 ? 20.0 * Math.Log10(peakAmp) : double.NegativeInfinity;
            var rmsDb  = rmsAmp  > 0 ? 20.0 * Math.Log10(rmsAmp)  : double.NegativeInfinity;
            var var50ms = MeasurePcm16VoiceActivityRatio(pcm, frameLoudThresholdDbfs: -45.0);
            AppLogger.Log($"[BOT-Voice-pipe] [t2] pipeline-start | enqueue-lag={enqueueLagMs}ms · provider={v.SttProvider} · lang={v.SttLanguage} · pcm=~{pcmSec:F2}s · peak={peakDb:F1}dBFS · rms={rmsDb:F1}dBFS · VAR={var50ms:P1}");

            const double MinSpeechPeakDbfs       = -38.0;   // any audible moment
            const double MinVoiceActivityRatio   = 0.10;    // ≥10% of 50 ms frames active

            if (peakDb < MinSpeechPeakDbfs)
            {
                AppLogger.Log($"[BOT-Voice-pipe] dropped — peak gate (peak={peakDb:F1}dBFS < {MinSpeechPeakDbfs}dBFS · no audible speech burst in clip)");
                SetVoiceStatus("(too quiet — try speaking closer to the mic)", System.Windows.Media.Brushes.Goldenrod);
                return;
            }
            if (var50ms < MinVoiceActivityRatio)
            {
                AppLogger.Log($"[BOT-Voice-pipe] dropped — VAR gate (active-frame ratio={var50ms:P1} < {MinVoiceActivityRatio:P0} · likely brief click/tap, not sustained speech)");
                SetVoiceStatus("(brief click only — speak a longer phrase)", System.Windows.Media.Brushes.Goldenrod);
                return;
            }

            SetVoiceStatus($"Transcribing · {ShortProviderLabel(v)}…", System.Windows.Media.Brushes.SkyBlue);

            var prepSw = System.Diagnostics.Stopwatch.StartNew();
            var ready = await stt.EnsureReadyAsync(
                new Progress<string>(msg => SetVoiceStatus(msg, System.Windows.Media.Brushes.SkyBlue)),
                ct);
            prepSw.Stop();
            AppLogger.Log($"[BOT-Voice-pipe] [stage] STT prep | {prepSw.ElapsedMilliseconds}ms · ready={ready}");

            if (!ready || ct.IsCancellationRequested)
            {
                AppLogger.Log("[BOT-Voice-pipe] dropped — STT not ready or cancelled");
                return;
            }

            string transcript;
            var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                transcript = await stt.TranscribeAsync(pcm, v.SttLanguage, ct);
                transcribeSw.Stop();
            }
            catch (OperationCanceledException)
            {
                transcribeSw.Stop();
                AppLogger.Log($"[BOT-Voice-pipe] cancelled at transcribe ({transcribeSw.ElapsedMilliseconds}ms in)");
                return;
            }
            catch (Exception ex)
            {
                transcribeSw.Stop();
                AppLogger.LogError($"[BOT-Voice-pipe] transcribe failed at {transcribeSw.ElapsedMilliseconds}ms", ex);
                SetVoiceStatus($"✗ {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            var rtFactor = pcmSec > 0 ? transcribeSw.ElapsedMilliseconds / 1000.0 / pcmSec : 0;
            AppLogger.Log($"[BOT-Voice-pipe] [stage] STT transcribe | {transcribeSw.ElapsedMilliseconds}ms · {rtFactor:F2}x realtime · chars={(transcript ?? "").Length}");

            transcript = (transcript ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(transcript))
            {
                SetVoiceStatus("(empty) — try again", System.Windows.Media.Brushes.Goldenrod);
                AppLogger.Log("[BOT-Voice-pipe] dropped — empty transcript");
                return;
            }
            // Drop known Whisper hallucination outros ("감사합니다",
            // "Thank you for watching" etc.) — see WhisperHallucinationFilter.
            // Peak/VAR gates above keep most quiet clips out, but Whisper can
            // still hallucinate on borderline-energy input that passed both.
            if (WhisperHallucinationFilter.IsLikelyHallucination(transcript))
            {
                SetVoiceStatus("(noise — try again)", System.Windows.Media.Brushes.Goldenrod);
                AppLogger.Log($"[BOT-Voice-pipe] dropped — Whisper hallucination pattern: \"{transcript}\"");
                return;
            }
            if (ct.IsCancellationRequested) return;

            // Hand the transcript off via the shared dispatch helper, which
            // applies VoiceCommandInterceptor (stop / summarize-terminal)
            // before falling back to txtInput + SendCurrentInput.
            var dispatchSw = System.Diagnostics.Stopwatch.StartNew();
            Dispatcher.Invoke(() => DispatchVoiceTranscriptOnUi(transcript, "batch"));
            dispatchSw.Stop();

            // ── Final summary line: every measurement on one row ──
            // Reading order: end-to-end perceived latency from when the
            // user finished speaking to when the terminal saw the input.
            var t1 = _utteranceEndedAtUtc;
            var totalSinceUtteranceEndMs = t1 is { } t
                ? (int)(DateTime.UtcNow - t).TotalMilliseconds
                : -1;
            AppLogger.Log(
                $"[BOT-Voice-pipe] [t3] DONE | " +
                $"end-to-end {totalSinceUtteranceEndMs}ms (utt-end → terminal) · " +
                $"enqueue {enqueueLagMs}ms · " +
                $"prep {prepSw.ElapsedMilliseconds}ms · " +
                $"transcribe {transcribeSw.ElapsedMilliseconds}ms ({rtFactor:F1}x rt) · " +
                $"dispatch {dispatchSw.ElapsedMilliseconds}ms · " +
                $"chars={transcript.Length} · provider={v.SttProvider}");
        }
        finally
        {
            _voicePipelineBusy = false;
            SetInflightCancelVisible(false);
        }
    }

    /// <summary>
    /// Walk a 16-bit little-endian PCM byte array and return (peak, rms) in
    /// normalised [0, 1] amplitude. Used by the voice-activity gate to decide
    /// whether the captured utterance has enough speech energy to bother
    /// transcribing.
    /// </summary>
    private static (double peak, double rms) MeasurePcm16Level(byte[] pcm)
    {
        if (pcm.Length < 2) return (0, 0);
        long sumSq = 0;
        int peak = 0;
        var sampleCount = pcm.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            int abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
            sumSq += (long)s * s;
        }
        var rms = Math.Sqrt(sumSq / (double)sampleCount) / 32768.0;
        return (peak / 32768.0, rms);
    }

    /// <summary>
    /// Fraction of 50 ms frames in the buffer whose peak exceeds
    /// <paramref name="frameLoudThresholdDbfs"/>. A simple voice-activity
    /// proxy that distinguishes "sustained speech" (many active frames in
    /// a row) from "single click in silence" (one active frame, rest dead).
    /// 16 kHz / 16-bit / mono = 1600 samples = 3200 bytes per 50 ms frame.
    /// </summary>
    private static double MeasurePcm16VoiceActivityRatio(byte[] pcm, double frameLoudThresholdDbfs)
    {
        const int FrameBytes = 3200;
        if (pcm.Length < FrameBytes) return 0.0;
        var amplitudeThreshold = Math.Pow(10.0, frameLoudThresholdDbfs / 20.0) * 32768.0;
        var totalFrames = pcm.Length / FrameBytes;
        if (totalFrames == 0) return 0.0;

        int loudFrames = 0;
        for (int f = 0; f < totalFrames; f++)
        {
            int framePeak = 0;
            int start = f * FrameBytes;
            for (int i = start; i < start + FrameBytes; i += 2)
            {
                short s = (short)(pcm[i] | (pcm[i + 1] << 8));
                int abs = s < 0 ? -s : s;
                if (abs > framePeak) framePeak = abs;
            }
            if (framePeak >= amplitudeThreshold) loudFrames++;
        }
        return loudFrames / (double)totalFrames;
    }

    private void SetInflightCancelVisible(bool visible)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<bool>(SetInflightCancelVisible), DispatcherPriority.Normal, visible);
            return;
        }
        if (btnVoiceCancelInflight is not null)
            btnVoiceCancelInflight.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Delegation mode (M0017) ───────────────────────────────────────
    //
    // Voice utterances "에이전트큐" / "에이전트큐 중단" toggle a state on
    // AgentBotActor. (The original v1 trigger word "위임" was dropped after
    // Whisper STT kept fumbling it — see VoiceCommandInterceptor "trigger
    // v2" note.) While ON, every AI-mode StartAgentLoop is rewritten
    // server-side to force Mode 2 (terminal relay) targeting a "Claude"
    // tab. Output is already spoken via the existing TTS path on AddBotMessage
    // — visually-impaired users hear the final summary without any extra
    // wiring here.
    //
    // Preflight: scan the current terminal list for a tab whose title
    // contains "Claude". If none, surface a voice-friendly message and
    // refuse to enter delegation mode so the user isn't left with a silent
    // black-hole agent loop. Full preflight (playwright availability, CLI
    // auth) is intentionally NOT implemented here — see M0017 completion
    // log for rationale.

    private void EnterDelegationMode()
    {
        // Auto-promote to AI mode when feasible. Voice users can't reach
        // the badge anyway, so requiring them to tab-cycle manually would
        // strand the command in CHT/KEY mode.
        if (_chatMode != ChatMode.Ai)
        {
            if (IsAiModeAvailable())
            {
                _chatMode = ChatMode.Ai;
                s_lastChatMode = _chatMode;
                UpdateChatModeBadge();
                AppLogger.Log("[BOT-Voice] Auto-promoted to AI mode for delegation");
            }
            else
            {
                var blocked = "AI 모드 모델이 준비되지 않아 에이전트큐 모드를 활성화할 수 없습니다. 설정에서 모델을 먼저 로드해 주세요.";
                AddSystemMessage($"🛡 {blocked}");
                AddBotMessage(blocked, "AgentCue");
                AppLogger.Log("[BOT-Voice] Delegation refused — AI mode not available");
                return;
            }
        }

        var claude = FindClaudeTerminal();
        if (claude is null)
        {
            var msg = "Claude 터미널이 활성화되어 있지 않습니다. Claude 탭을 먼저 열어 주세요.";
            AddSystemMessage($"🛡 {msg}");
            AddBotMessage(msg, "AgentCue");
            AppLogger.Log("[BOT-Voice] Delegation refused — no Claude terminal");
            return;
        }

        // M0017 후속 #9 — UI-side flag mirrors the actor flag. SendCurrentInput
        // consults this to bypass the LLM agent loop on the send path.
        _delegationMode = true;

        // M0017 후속 #3 — route through BotTell so we recover via
        // ActorSelection if MainWindow's deferred Stage.Ask<BotCreated>
        // wiring hasn't landed yet. The previous fallback used
        // `ActorSelection(path).Anchor` which silently sent to the root
        // guardian instead of the bot actor.
        BotTell(new Agent.Common.Actors.SetDelegationMode(true));
        // Fresh KV cache so the new directive applies cleanly — leaving the
        // prior Mode 1 history primed would tempt the model to keep its old
        // tone even though the wrap now demands Mode 2.
        RequestAiSessionReset("entered delegation mode");

        // M0017 후속 #1 — suppress the ~1.5 KB first-contact handshake header
        // for the Claude tab. The handshake is opt-in for Mode 2 ("introduce
        // yourself to Claude"); in delegation mode the user has explicitly
        // authorised relay, so the handshake primer just buries the actual
        // question and the receiving Claude tries to execute the "run
        // AgentZeroLite.exe -cli help" Step 1 instruction instead of answering.
        // Must be sent AFTER RequestAiSessionReset, because ResetAgentLoopMemory
        // clears _introducedTerminals — otherwise our mark would be wiped.
        BotTell(new Agent.Common.Actors.MarkTerminalIntroduced(
            claude.Value.GroupIndex, claude.Value.TabIndex));

        // M0017 후속 #4 — send a SHORT delegation handshake directly to the
        // Claude session. The full first-contact handshake (suppressed in
        // 후속 #1) was the only place Claude learned about the bot-chat CLI
        // callback channel. Without that teaching Claude just types replies
        // into its own terminal and AgentBot has to poll via read_terminal
        // — slow + brittle. This message is ~280 chars, taught in Korean,
        // focuses ONLY on the reverse channel, and uses the exact tab title
        // as the peer-name string so IPC routing keys match.
        //
        // We also pre-arm the actor's per-peer routing state (active +
        // handshake-sent) so when Claude calls back with `bot-chat "DONE(ready)"
        // --from <TabTitle>` the IPC delivery is recognised as an active
        // peer signal instead of being silently dropped.
        TrySendDelegationHandshake(claude.Value);

        var ack = $"에이전트큐 모드를 시작합니다. 이후 음성 요청은 {claude.Value.Label} 으로 전달됩니다.";
        AddSystemMessage($"🛡 {ack}");
        AddBotMessage(ack, "AgentCue");
        AppLogger.Log($"[BOT-Voice] Delegation ON → target={claude.Value.Label} (pre-marked introduced [{claude.Value.GroupIndex}:{claude.Value.TabIndex}])");
    }

    private void ExitDelegationMode()
    {
        // M0017 후속 #9 — clear UI-side flag in lockstep with the actor.
        _delegationMode = false;

        // M0017 후속 #3 — same BotTell fallback as EnterDelegationMode.
        BotTell(new Agent.Common.Actors.SetDelegationMode(false));

        // M0017 후속 #4 — clear the peer's active-conversation flag so any
        // late Claude CLI callbacks after we've left delegation don't
        // accidentally trigger a continuation agent loop. The same Claude
        // tab can still be talked to in normal Mode 2 later — it'll go
        // through the standard handshake on its next first contact.
        var claude = FindClaudeTerminal();
        if (claude is { } c)
        {
            BotTell(new Agent.Common.Actors.ClearConversationActive(c.TabTitle));
            AppLogger.Log($"[BOT-Voice] Delegation peer \"{c.TabTitle}\" cleared from active conversations");
        }

        RequestAiSessionReset("exited delegation mode");

        var ack = "에이전트큐 모드를 중단합니다. 일반 AI 모드로 돌아갑니다.";
        AddSystemMessage($"🛡 {ack}");
        AddBotMessage(ack, "AgentCue");
        AppLogger.Log("[BOT-Voice] Delegation OFF");
    }

    /// <summary>
    /// Result of <see cref="FindClaudeTerminal"/> — the (group, tab) indices
    /// the delegation wrap will route to, the exact tab title (used as the
    /// peer-name string for IPC routing in <see cref="MarkConversationActive"/>
    /// / <see cref="MarkHandshakeSent"/> / <c>--from</c> values), and a
    /// human-friendly label for the spoken acknowledgement.
    /// </summary>
    private readonly record struct ClaudeTerminalRef(
        int GroupIndex, int TabIndex, string TabTitle, string Label);

    /// <summary>
    /// Scan the active workspace's terminal tabs for one whose title contains
    /// "Claude" (case-insensitive). Returns the (g, t) indices, the exact
    /// title (the IPC peer-name contract), and a human label like
    /// "Main / Claude Code", or null when no such tab exists.
    ///
    /// We use Title contains (not exact match) so labels like "Claude Code",
    /// "Claude — main", "[Claude]" all qualify. TabTitle is preserved as the
    /// exact string because <see cref="WorkspaceTerminalToolHost.ResolvePeerName"/>
    /// uses the same title verbatim when prepending handshake intros and the
    /// bot-chat CLI's <c>--from</c> argument must match for the
    /// <see cref="TerminalSentToBot"/> routing to find an active conversation.
    /// </summary>
    private ClaudeTerminalRef? FindClaudeTerminal()
    {
        try
        {
            var groups = _getGroups?.Invoke();
            if (groups is null) return null;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (g.Tabs is null) continue;
                for (int ti = 0; ti < g.Tabs.Count; ti++)
                {
                    var title = g.Tabs[ti].Title ?? string.Empty;
                    if (title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                        return new ClaudeTerminalRef(gi, ti, title, $"{g.DisplayName} / {title}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[BOT-Voice] FindClaudeTerminal failed", ex);
        }
        return null;
    }

    /// <summary>
    /// M0017 후속 #9 — write a user utterance directly to the Claude terminal
    /// session, bypassing the on-device LLM agent loop. Used by
    /// <see cref="SendCurrentInput"/> when <c>_delegationMode</c> is on.
    /// Returns true on success, false when the Claude tab can't be located
    /// or its session isn't initialised (caller surfaces a hint to the user
    /// via <see cref="AddBotMessage"/>; we also log the reason).
    ///
    /// Side effects:
    ///   • Adds a user bubble + a `→ AgentTest / Claude` system breadcrumb
    ///     so the chat UI mirrors what was sent.
    ///   • Re-marks Claude as introduced + active in the actor so the
    ///     forthcoming DONE callback routes correctly even if a prior reset
    ///     wiped the per-peer state.
    /// </summary>
    private bool TryRelayDirectlyToClaude(string text, string displayText)
    {
        var target = FindClaudeTerminal();
        if (target is null)
        {
            var msg = "Claude 터미널을 찾을 수 없습니다. Claude 탭을 먼저 열어 주세요.";
            AddSystemMessage($"🛡 {msg}");
            AddBotMessage(msg, "AgentCue");
            AppLogger.Log("[AIMODE] delegation direct-relay refused — no Claude tab");
            return false;
        }

        // Match the existing `TrySendDelegationHandshake` style — let the
        // compiler resolve the Session type via the CliGroupInfo →
        // ConsoleTabInfo chain so we don't need an extra `using`.
        AgentZeroWpf.Services.ConPtyTerminalSession? session = null;
        try
        {
            var groups = _getGroups?.Invoke();
            if (groups is not null
                && target.Value.GroupIndex >= 0 && target.Value.GroupIndex < groups.Count)
            {
                var g = groups[target.Value.GroupIndex];
                if (g.Tabs is not null
                    && target.Value.TabIndex >= 0 && target.Value.TabIndex < g.Tabs.Count)
                {
                    session = g.Tabs[target.Value.TabIndex].Session;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[AIMODE] delegation direct-relay — resolving Claude session threw", ex);
        }

        if (session is null)
        {
            var msg = "Claude 터미널 세션이 아직 준비되지 않았습니다. 잠시 후 다시 시도해 주세요.";
            AddSystemMessage($"🛡 {msg}");
            AddBotMessage(msg, "AgentCue");
            AppLogger.Log($"[AIMODE] delegation direct-relay refused — Claude session null [{target.Value.GroupIndex}:{target.Value.TabIndex}]");
            return false;
        }

        // UI mirror: user bubble first, then a one-line breadcrumb that this
        // went to Claude (not the LLM). Helps sighted operators correlate
        // voice prompt → Claude answer when the response eventually comes
        // back via DONE TTS.
        AddUserMessage(displayText, "Voice");
        AddSystemMessage($"→ {target.Value.Label} (delegation direct)");

        // Re-arm the per-peer state on every send. EnterDelegationMode does
        // this once but the user may have toggled embed/floating or some
        // other action that cleared the actor's introduction set; cheap to
        // idempotent-Tell.
        BotTell(new Agent.Common.Actors.MarkTerminalIntroduced(
            target.Value.GroupIndex, target.Value.TabIndex));
        BotTell(new Agent.Common.Actors.MarkConversationActive(target.Value.TabTitle));

        try
        {
            session.WriteAndSubmit(text);
            AppLogger.Log($"[AIMODE] delegation direct-relay → \"{target.Value.TabTitle}\" len={text.Length}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[AIMODE] delegation direct-relay — WriteAndSubmit threw", ex);
            AddSystemMessage("❌ Claude 터미널 전송 실패 — 자세한 내용은 로그를 확인해 주세요.");
            return false;
        }
    }

    /// <summary>
    /// Write the delegation handshake message directly to Claude's terminal
    /// session (bypassing the LLM tool-call path) and pre-arm the actor's
    /// per-peer routing state so Claude's CLI callbacks are recognised.
    /// Returns true if the handshake was written, false if the session was
    /// unavailable (tab not yet initialised). Failure is non-fatal —
    /// delegation still works via read_terminal polling.
    /// </summary>
    private bool TrySendDelegationHandshake(ClaudeTerminalRef target)
    {
        try
        {
            var groups = _getGroups?.Invoke();
            if (groups is null || target.GroupIndex < 0 || target.GroupIndex >= groups.Count)
            {
                AppLogger.Log($"[BOT-Voice] Delegation handshake SKIPPED — group index {target.GroupIndex} out of range");
                return false;
            }
            var g = groups[target.GroupIndex];
            if (g.Tabs is null || target.TabIndex < 0 || target.TabIndex >= g.Tabs.Count)
            {
                AppLogger.Log($"[BOT-Voice] Delegation handshake SKIPPED — tab index [{target.GroupIndex}:{target.TabIndex}] out of range");
                return false;
            }
            var session = g.Tabs[target.TabIndex].Session;
            if (session is null)
            {
                AppLogger.Log($"[BOT-Voice] Delegation handshake SKIPPED — Claude tab [{target.GroupIndex}:{target.TabIndex}] session is null (tab not initialised)");
                return false;
            }

            var msg = BuildDelegationHandshakeMessage(target.TabTitle);
            session.WriteAndSubmit(msg);

            // Pre-arm the per-peer state so Claude's `--from <TabTitle>` callbacks
            // land in the agent loop's continuation path instead of being
            // dropped as "INACTIVE peer signal".
            BotTell(new Agent.Common.Actors.MarkConversationActive(target.TabTitle));
            BotTell(new Agent.Common.Actors.MarkHandshakeSent(target.TabTitle));

            AppLogger.Log($"[BOT-Voice] Delegation handshake SENT to \"{target.TabTitle}\" ({msg.Length} chars, peer marked active)");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[BOT-Voice] Delegation handshake write failed", ex);
            return false;
        }
    }

    /// <summary>
    /// M0017 후속 #4 — short focused setup message sent directly to Claude's
    /// terminal session when delegation mode is entered. Teaches ONLY the
    /// reverse channel (bot-chat CLI callback), not the full first-contact
    /// handshake. Verbose handshake intro was suppressed in 후속 #1; this
    /// brings back the critical "reply via CLI" instruction without the
    /// 1.5 KB AgentBot introduction noise that buried the user's question.
    /// </summary>
    private static string BuildDelegationHandshakeMessage(string peerName)
    {
        // Korean text — Claude routinely operates in Korean for this user,
        // and short Korean keeps the message under ~280 chars total which
        // easily fits in a single terminal screen. Triple-quoted strings
        // keep the literal terminal-line spacing intact.
        return
            "[AgentBot 에이전트큐 모드 — 응답 채널 설정]\n" +
            "지금부터 사용자가 음성으로 묻는 질문이 너에게 전달돼. 답변은 너의 터미널에서 다음 명령을 실행해서 보내:\n" +
            "\n" +
            $"    AgentZeroLite.exe -cli bot-chat \"DONE(여기에 답변 내용)\" --from {peerName}\n" +
            "\n" +
            "이 줄을 실행하면 답변이 즉시 음성으로 사용자에게 전달돼. 화면에만 적으면 사용자에게 안 들려.\n" +
            "준비됐으면 알려줘:\n" +
            $"    AgentZeroLite.exe -cli bot-chat \"DONE(ready)\" --from {peerName}";
    }

    private void DisposeVoiceRuntime()
    {
        try { StopVoiceMic("window closed"); } catch { }
        try { _voicePipelineCts?.Dispose(); } catch { }
        try { _voiceCapture?.Dispose(); } catch { }
        try { _testToolsWindow?.Close(); } catch { }
        _voicePipelineCts = null;
        _voiceCapture = null;
        _testToolsWindow = null;
    }

    // ── Test tools popup (Virtual voice; future: virtual keyboard, …) ───
    //
    // Opens TestToolsWindow as a modeless owned popup. If already open,
    // brings it to front instead of stacking duplicates.

    private void OnVoiceTestToggle(object sender, RoutedEventArgs e)
    {
        if (_testToolsWindow is { } existing)
        {
            try
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            catch
            {
                // Window was closed but reference not nulled — fall through to recreate.
                _testToolsWindow = null;
            }
        }

        // Owner must be a window that has actually been Shown (has an HWND).
        // When AskBot is in embedded mode (DetachContent → MainWindow's dock),
        // `this` AgentBotWindow is Hide()'n — its handle was never created, so
        // assigning Owner=this throws "Owner cannot be set to a Window that
        // has not been previously shown". Pick whichever top-level window is
        // actually visible.
        var owner = ResolveVisibleOwner();
        var window = new TestToolsWindow();
        if (owner is not null) window.Owner = owner;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_testToolsWindow, window))
                _testToolsWindow = null;
        };
        _testToolsWindow = window;
        window.Show();
    }

    private Window? ResolveVisibleOwner()
    {
        if (IsVisible) return this;
        var mw = Application.Current?.MainWindow;
        if (mw is not null && mw.IsVisible) return mw;
        return Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsLoaded);
    }
}
