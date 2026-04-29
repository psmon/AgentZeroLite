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
            if (ct.IsCancellationRequested) return;

            // Hand the transcript off to the existing send pipeline. SendCurrentInput
            // already handles AIMODE vs Chat/Key routing, attachments, the
            // multi-line case, and the single-instance terminal write.
            var dispatchSw = System.Diagnostics.Stopwatch.StartNew();
            Dispatcher.Invoke(() =>
            {
                txtInput.Text = transcript;
                txtInput.CaretIndex = transcript.Length;
                SendCurrentInput();
                SetVoiceStatus($"Listening · {ShortProviderLabel(v)}", System.Windows.Media.Brushes.LightGreen);
            });
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
