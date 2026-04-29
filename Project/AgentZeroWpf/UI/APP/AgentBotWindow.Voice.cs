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

    // Stream pipeline (P1+) — non-null when VoiceSettings.UseStreamPipeline is true.
    private IActorRef? _voiceStreamRef;
    private Action<MicFrame>? _voiceFrameForwarder;

    // Virtual voice injector — silent test mode (TTS → speaker → mic).
    private VirtualVoiceInjector? _virtualVoice;

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

    private async Task StartVoiceMicAsync()
    {
        try
        {
            var v = VoiceSettingsStore.Load();

            _voiceCapture ??= new VoiceCaptureService();
            _voicePipelineCts?.Dispose();
            _voicePipelineCts = new CancellationTokenSource();

            var threshold = VoiceRuntimeFactory.SensitivityToThreshold(100 - v.VadThreshold);
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
    /// Bring up the Akka.Streams INPUT pipeline. The actor is created lazily
    /// (idempotent — Stage caches the singleton) and the per-frame forwarder
    /// is wired so NAudio's capture thread Tells the actor for every 50 ms
    /// frame. Transcript callback fires off the actor's Receive thread —
    /// marshal back to the dispatcher before touching txtInput.
    /// </summary>
    private async Task StartStreamPipelineAsync(VoiceSettings v, float threshold)
    {
        var settingsSnapshot = v;
        var snapshotForFactory = v;

        var created = await ActorSystemManager.Stage.Ask<VoiceStreamCreated>(
            new CreateVoiceStream(
                SttFactory: () => VoiceRuntimeFactory.BuildStt(snapshotForFactory)
                    ?? throw new InvalidOperationException(
                        $"STT '{snapshotForFactory.SttProvider}' unavailable"),
                OnTranscript: OnVoiceStreamTranscript,
                TtsFactory: () => VoiceRuntimeFactory.BuildTts(snapshotForFactory)
                    ?? throw new InvalidOperationException(
                        $"TTS '{snapshotForFactory.TtsProvider}' unavailable"),
                PlaybackFactory: () => new NAudioPlaybackQueue()),
            TimeSpan.FromSeconds(5));

        _voiceStreamRef = created.VoiceRef;

        _voiceFrameForwarder = frame => _voiceStreamRef?.Tell(frame);
        _voiceCapture!.FrameAvailable += _voiceFrameForwarder;

        _voiceStreamRef.Tell(new StartListening(
            VadThreshold: threshold,
            PreRollSeconds: 1.0,
            UtteranceHangoverFrames: 40,
            MicBufferSize: 64,
            SttParallelism: Math.Max(1, settingsSnapshot.StreamSttParallelism),
            Language: settingsSnapshot.SttLanguage));
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

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_voiceMicOn) return;
            var v = VoiceSettingsStore.Load();
            txtInput.Text = transcript;
            txtInput.CaretIndex = transcript.Length;
            SendCurrentInput();
            SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
        }));
        AppLogger.Log($"[BOT-Voice] (stream) Transcript sent ({transcript.Length} chars, {durationSeconds:F2}s utterance)");
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
        _voiceCapture?.SeedBufferWithPreRoll();
    }

    private void OnVoiceUtteranceEnded()
    {
        if (_voiceCapture is null) return;
        var pcm = _voiceCapture.ConsumePcmBuffer();
        if (pcm.Length < 8000) return; // <0.25s — too short to bother

        if (_voicePipelineBusy)
        {
            // Skip — a previous transcription hasn't finished. We could queue
            // it but that'd let utterances stack while a slow STT crawls.
            // Simpler: drop, surface "busy" so the user knows to pause briefly.
            AppLogger.Log("[BOT-Voice] Skipping utterance — pipeline busy");
            SetVoiceStatus("Busy — pause briefly…", System.Windows.Media.Brushes.Goldenrod);
            return;
        }

        _ = Task.Run(() => RunVoicePipelineOnceAsync(pcm));
    }

    private async Task RunVoicePipelineOnceAsync(byte[] pcm)
    {
        if (_voicePipelineBusy) return;
        _voicePipelineBusy = true;
        var ct = _voicePipelineCts?.Token ?? CancellationToken.None;

        SetInflightCancelVisible(true);
        try
        {
            var v = VoiceSettingsStore.Load();
            var stt = VoiceRuntimeFactory.BuildStt(v);
            if (stt is null)
            {
                SetVoiceStatus($"✗ STT '{v.SttProvider}' unavailable", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            SetVoiceStatus($"Transcribing · {ShortProviderLabel(v)}…", System.Windows.Media.Brushes.SkyBlue);
            var ready = await stt.EnsureReadyAsync(
                new Progress<string>(msg => SetVoiceStatus(msg, System.Windows.Media.Brushes.SkyBlue)),
                ct);
            if (!ready || ct.IsCancellationRequested) return;

            string transcript;
            try
            {
                transcript = await stt.TranscribeAsync(pcm, v.SttLanguage, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLogger.LogError("[BOT-Voice] Transcribe failed", ex);
                SetVoiceStatus($"✗ {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            transcript = (transcript ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(transcript))
            {
                SetVoiceStatus("(empty) — try again", System.Windows.Media.Brushes.Goldenrod);
                return;
            }
            if (ct.IsCancellationRequested) return;

            // Hand the transcript off to the existing send pipeline. SendCurrentInput
            // already handles AIMODE vs Chat/Key routing, attachments, the
            // multi-line case, and the single-instance terminal write.
            Dispatcher.Invoke(() =>
            {
                txtInput.Text = transcript;
                txtInput.CaretIndex = transcript.Length;
                SendCurrentInput();
                SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
            });
            AppLogger.Log($"[BOT-Voice] Transcript sent ({transcript.Length} chars)");
        }
        finally
        {
            _voicePipelineBusy = false;
            SetInflightCancelVisible(false);
        }
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

    private void DisposeVoiceRuntime()
    {
        try { StopVoiceMic("window closed"); } catch { }
        try { _voicePipelineCts?.Dispose(); } catch { }
        try { _voiceCapture?.Dispose(); } catch { }
        try { _virtualVoice?.Dispose(); } catch { }
        _voicePipelineCts = null;
        _voiceCapture = null;
        _virtualVoice = null;
    }

    // ── Virtual voice test (silent test mode) ───────────────────────────
    //
    // The strip is hidden by default; clicking the toggle reveals a textbox.
    // Submitting (Enter or Speak button) synthesises the line via the active
    // TTS provider and plays it through the default speaker. If the mic is
    // ON, the played audio loops back through OS audio → mic capture → STT,
    // exercising the entire input path without needing a human speaker.

    private void OnVoiceTestToggle(object sender, RoutedEventArgs e)
    {
        if (pnlVoiceTestStrip is null) return;
        var willShow = pnlVoiceTestStrip.Visibility != Visibility.Visible;
        pnlVoiceTestStrip.Visibility = willShow ? Visibility.Visible : Visibility.Collapsed;
        if (willShow)
        {
            txtVoiceTestStatus.Text = "TTS → speaker → mic. Mic ON for round-trip.";
            txtVoiceTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
            try { txtVoiceTestInput?.Focus(); } catch { }
        }
    }

    private void OnVoiceTestInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.None))
        {
            e.Handled = true;
            _ = RunVirtualVoiceAsync();
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            if (pnlVoiceTestStrip is not null)
                pnlVoiceTestStrip.Visibility = Visibility.Collapsed;
        }
    }

    private void OnVoiceTestSpeak(object sender, RoutedEventArgs e) => _ = RunVirtualVoiceAsync();

    private async Task RunVirtualVoiceAsync()
    {
        if (txtVoiceTestInput is null) return;
        var text = txtVoiceTestInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            SetVoiceTestStatus("Type something first.", isError: false);
            return;
        }

        _virtualVoice ??= new VirtualVoiceInjector();
        // Wire status updates lazily — only the first call needs the hookup.
        _virtualVoice.Started -= OnVirtualVoiceStarted;
        _virtualVoice.Stopped -= OnVirtualVoiceStopped;
        _virtualVoice.Errored -= OnVirtualVoiceErrored;
        _virtualVoice.Started += OnVirtualVoiceStarted;
        _virtualVoice.Stopped += OnVirtualVoiceStopped;
        _virtualVoice.Errored += OnVirtualVoiceErrored;

        SetVoiceTestStatus("Synthesizing…", isError: false);
        try
        {
            await _virtualVoice.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            SetVoiceTestStatus($"✗ {ex.Message}", isError: true);
        }
    }

    private void OnVirtualVoiceStarted() => SetVoiceTestStatus("▶ playing", isError: false);
    private void OnVirtualVoiceStopped() => SetVoiceTestStatus("done — mic should now have it.", isError: false);
    private void OnVirtualVoiceErrored(Exception ex) => SetVoiceTestStatus($"✗ {ex.Message}", isError: true);

    private void SetVoiceTestStatus(string text, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string, bool>(SetVoiceTestStatus), DispatcherPriority.Normal, text, isError);
            return;
        }
        if (txtVoiceTestStatus is null) return;
        txtVoiceTestStatus.Text = text;
        txtVoiceTestStatus.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Media.Brush)FindResource("TextDim");
    }
}
