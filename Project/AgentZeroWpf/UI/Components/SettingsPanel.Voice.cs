using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Voice tab handlers — live pipeline.
///
/// Wires the Voice Test panel end-to-end:
///   1. NAudio mic capture → 16 kHz PCM buffer + dual-VAD events
///   2. STT provider (Whisper.net local / OpenAI Whisper / Webnori-Gemma /
///      Local-Gemma placeholder) → text
///   3. LlmGateway.OpenSession() — uses whatever the user picked on the LLM
///      tab (Local or External), no separate Voice-LLM provider
///   4. TTS provider (Off / Windows SAPI / OpenAI tts-1) → WAV bytes
///   5. NAudio playback through default output device
///
/// The capture is suspended (<c>Muted</c>) while TTS plays back so the AI's
/// own voice doesn't bleed into the next transcription.
/// </summary>
public partial class SettingsPanel
{
    private bool _voiceInitializing;

    // Voice Test runtime state — instance scoped so we can stop/dispose on
    // panel unload. Created lazily on first START click.
    private VoiceCaptureService? _voiceCapture;
    private VoicePlaybackService? _voicePlayback;
    private CancellationTokenSource? _pipelineCts;
    private bool _pipelineBusy;

    private void InitializeVoiceTab()
    {
        _voiceInitializing = true;
        try
        {
            var v = VoiceSettingsStore.Load();

            SelectComboTag(cbSttProvider, v.SttProvider);
            SelectComboTag(cbSttWhisperModel, v.SttWhisperModel);
            SelectComboTag(cbSttLanguage, v.SttLanguage);
            chkSttUseGpu.IsChecked = v.SttUseGpu;
            pbSttOpenAIKey.Password = v.SttOpenAIApiKey;
            ApplySttProviderUi(v.SttProvider);

            PreloadSingleItem(cbSttWebnoriModel, v.SttWebnoriModel);
            PopulateLocalGemmaModels(v.SttLocalGemmaModelId);

            SelectComboTag(cbTtsProvider, v.TtsProvider);
            pbTtsOpenAIKey.Password = v.TtsOpenAIApiKey;
            PreloadSingleItem(cbTtsVoice, v.TtsVoice);
            ApplyTtsProviderUi(v.TtsProvider);

            slVoiceSensitivity.Value = 100 - v.VadThreshold;
            tbVoiceSensitivityValue.Text = ((int)slVoiceSensitivity.Value).ToString();
            slVoiceSensitivity.ValueChanged += (_, _) =>
            {
                tbVoiceSensitivityValue.Text = ((int)slVoiceSensitivity.Value).ToString();
                if (_voiceCapture is not null)
                    _voiceCapture.VadThreshold = (100 - (int)slVoiceSensitivity.Value) / 100f;
            };

            rbVoiceModeAuto.IsChecked = v.VoiceTestAutoMode;
            rbVoiceModeManual.IsChecked = !v.VoiceTestAutoMode;

            RefreshVoiceInputDevices(v.InputDeviceId);
            UpdateVoiceTestActiveLlmLabel();

            // Stop & dispose capture/playback when the panel unloads so we
            // never leak the mic across hide/show cycles.
            Unloaded += (_, _) => DisposeVoiceRuntime();
        }
        finally
        {
            _voiceInitializing = false;
        }
    }

    private void DisposeVoiceRuntime()
    {
        try { _pipelineCts?.Cancel(); } catch { }
        try { _voiceCapture?.Dispose(); } catch { }
        try { _voicePlayback?.Dispose(); } catch { }
        _pipelineCts = null;
        _voiceCapture = null;
        _voicePlayback = null;
        _pipelineBusy = false;
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static void SelectComboTag(System.Windows.Controls.ComboBox box, string tag)
    {
        foreach (var obj in box.Items)
        {
            if (obj is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                box.SelectedItem = ci;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string ReadComboTag(System.Windows.Controls.ComboBox box, string fallback)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void PreloadSingleItem(System.Windows.Controls.ComboBox box, string value)
    {
        box.Items.Clear();
        if (!string.IsNullOrEmpty(value))
        {
            box.Items.Add(value);
            box.SelectedIndex = 0;
        }
    }

    private void PopulateLocalGemmaModels(string selectedId)
    {
        cbSttLocalGemmaModel.Items.Clear();
        foreach (var entry in LlmModelCatalog.All)
            cbSttLocalGemmaModel.Items.Add(new ComboBoxItem { Content = entry.DisplayName, Tag = entry.Id });

        if (!string.IsNullOrEmpty(selectedId))
        {
            foreach (var obj in cbSttLocalGemmaModel.Items)
                if (obj is ComboBoxItem ci && (ci.Tag as string) == selectedId)
                {
                    cbSttLocalGemmaModel.SelectedItem = ci;
                    return;
                }
        }
        if (cbSttLocalGemmaModel.Items.Count > 0) cbSttLocalGemmaModel.SelectedIndex = 0;
    }

    private void RefreshVoiceInputDevices(string selectedDeviceId)
    {
        cbVoiceInputDevice.Items.Clear();
        try
        {
            var devices = VoiceCaptureService.ListDevices();
            if (devices.Count == 0)
            {
                cbVoiceInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = "(no input devices detected)",
                    Tag = ""
                });
                cbVoiceInputDevice.SelectedIndex = 0;
                return;
            }

            foreach (var dev in devices)
                cbVoiceInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = $"[{dev.DeviceNumber}] {dev.Name}",
                    Tag = dev.DeviceNumber.ToString()
                });

            // Try to honour the saved selection, else default to first device.
            if (!string.IsNullOrEmpty(selectedDeviceId))
            {
                foreach (var obj in cbVoiceInputDevice.Items)
                    if (obj is ComboBoxItem ci && (ci.Tag as string) == selectedDeviceId)
                    {
                        cbVoiceInputDevice.SelectedItem = ci;
                        return;
                    }
            }
            cbVoiceInputDevice.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            cbVoiceInputDevice.Items.Add(new ComboBoxItem
            {
                Content = $"(device enumeration failed: {ex.Message})",
                Tag = ""
            });
            cbVoiceInputDevice.SelectedIndex = 0;
            AppLogger.LogError("[Voice] Device enumeration failed", ex);
        }
    }

    private void UpdateVoiceTestActiveLlmLabel()
    {
        try
        {
            var s = LlmSettingsStore.Load();
            string label = s.ActiveBackend == LlmActiveBackend.Local
                ? $"LLM: Local · {LlmModelCatalog.FindById(s.ModelId).DisplayName}"
                : $"LLM: External · {s.External.Provider} · {(string.IsNullOrEmpty(s.ResolveExternalModel()) ? "(no model)" : s.ResolveExternalModel())}";
            tbVoiceTestActiveLlm.Text = label;
        }
        catch
        {
            tbVoiceTestActiveLlm.Text = "LLM: —";
        }
    }

    private void ApplySttProviderUi(string provider)
    {
        spSttWhisperLocal.Visibility = provider == SttProviderNames.WhisperLocal ? Visibility.Visible : Visibility.Collapsed;
        grdSttOpenAIKey.Visibility = provider == SttProviderNames.OpenAIWhisper ? Visibility.Visible : Visibility.Collapsed;
        grdSttWebnoriModel.Visibility = provider == SttProviderNames.WebnoriGemma ? Visibility.Visible : Visibility.Collapsed;
        grdSttLocalGemmaModel.Visibility = provider == SttProviderNames.LocalGemma ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTtsProviderUi(string provider)
    {
        bool needsVoice = provider != TtsProviderNames.Off;
        grdTtsVoice.Visibility = needsVoice ? Visibility.Visible : Visibility.Collapsed;
        grdTtsOpenAIKey.Visibility = provider == TtsProviderNames.OpenAITts ? Visibility.Visible : Visibility.Collapsed;
    }

    private VoiceSettings ReadVoiceFromUi()
    {
        var v = VoiceSettingsStore.Load();
        v.SttProvider = ReadComboTag(cbSttProvider, SttProviderNames.WhisperLocal);
        v.SttWhisperModel = ReadComboTag(cbSttWhisperModel, "small");
        v.SttLanguage = ReadComboTag(cbSttLanguage, "auto");
        v.SttUseGpu = chkSttUseGpu.IsChecked == true;
        v.SttOpenAIApiKey = pbSttOpenAIKey.Password ?? "";
        v.SttWebnoriModel = (cbSttWebnoriModel.SelectedItem as string)
                            ?? cbSttWebnoriModel.Text
                            ?? "";
        v.SttLocalGemmaModelId = (cbSttLocalGemmaModel.SelectedItem as ComboBoxItem)?.Tag as string ?? v.SttLocalGemmaModelId;

        v.TtsProvider = ReadComboTag(cbTtsProvider, TtsProviderNames.Off);
        v.TtsOpenAIApiKey = pbTtsOpenAIKey.Password ?? "";
        v.TtsVoice = (cbTtsVoice.SelectedItem as string) ?? cbTtsVoice.Text ?? v.TtsVoice;

        v.InputDeviceId = (cbVoiceInputDevice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        v.VadThreshold = 100 - (int)slVoiceSensitivity.Value;
        v.VoiceTestAutoMode = rbVoiceModeAuto.IsChecked == true;
        return v;
    }

    private static int ParseDeviceNumber(string id) => int.TryParse(id, out var n) ? n : 0;

    /// <summary>Build the active <see cref="ISpeechToText"/> from the saved settings.</summary>
    private static ISpeechToText? CreateSttProvider(VoiceSettings v)
    {
        return v.SttProvider switch
        {
            SttProviderNames.WhisperLocal => new WhisperLocalStt(v.SttWhisperModel) { UseGpu = v.SttUseGpu },
            SttProviderNames.OpenAIWhisper => new OpenAiWhisperStt(v.SttOpenAIApiKey),
            SttProviderNames.WebnoriGemma => new WebnoriGemmaStt(v.SttWebnoriModel),
            SttProviderNames.LocalGemma => new LocalGemmaStt(v.SttLocalGemmaModelId),
            _ => null,
        };
    }

    /// <summary>Build the active <see cref="ITextToSpeech"/>; returns null when TTS is Off.</summary>
    private static ITextToSpeech? CreateTtsProvider(VoiceSettings v)
    {
        return v.TtsProvider switch
        {
            TtsProviderNames.WindowsTts => new WindowsTts(),
            TtsProviderNames.OpenAITts => new OpenAiTts(v.TtsOpenAIApiKey),
            _ => null,
        };
    }

    // ── event handlers ────────────────────────────────────────────────

    private void OnSttProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_voiceInitializing) return;
        var prov = ReadComboTag(cbSttProvider, SttProviderNames.WhisperLocal);
        ApplySttProviderUi(prov);
        tbSttSaveStatus.Text = "Unsaved — click Save STT.";
        tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
    }

    private void OnSttRefreshWebnoriModels(object sender, RoutedEventArgs e)
    {
        // Surface the catalog's known Webnori models. Webnori's /v1/models
        // endpoint is the canonical source but the audio-capable filter is
        // server-side, so listing the catalog gives the user a stable picker
        // even when the model server is offline.
        cbSttWebnoriModel.Items.Clear();
        foreach (var id in WebnoriDefaults.KnownModels)
            cbSttWebnoriModel.Items.Add(id);
        if (cbSttWebnoriModel.Items.Count > 0) cbSttWebnoriModel.SelectedIndex = 0;
        tbSttStatus.Text = $"✓ {cbSttWebnoriModel.Items.Count} model(s) listed (catalog).";
        tbSttStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
    }

    private void OnSttSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var v = ReadVoiceFromUi();
            VoiceSettingsStore.Save(v);
            tbSttSaveStatus.Text = "✓ Saved.";
            tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppLogger.Log($"[Voice-STT] Saved provider={v.SttProvider} model={v.SttWhisperModel} lang={v.SttLanguage} gpu={v.SttUseGpu}");
        }
        catch (Exception ex)
        {
            tbSttSaveStatus.Text = $"✗ {ex.Message}";
            tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice-STT] Save failed", ex);
        }
    }

    private void OnTtsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_voiceInitializing) return;
        var prov = ReadComboTag(cbTtsProvider, TtsProviderNames.Off);
        ApplyTtsProviderUi(prov);
        tbTtsSaveStatus.Text = "Unsaved — click Save TTS.";
        tbTtsSaveStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
    }

    private async void OnTtsRefreshVoices(object sender, RoutedEventArgs e)
    {
        var prov = ReadComboTag(cbTtsProvider, TtsProviderNames.Off);
        cbTtsVoice.Items.Clear();
        try
        {
            switch (prov)
            {
                case TtsProviderNames.OpenAITts:
                    foreach (var v in OpenAiTts.Voices)
                        cbTtsVoice.Items.Add(v);
                    tbTtsStatus.Text = "✓ OpenAI voices listed.";
                    tbTtsStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    break;
                case TtsProviderNames.WindowsTts:
                    var tts = new WindowsTts();
                    var voices = await tts.GetAvailableVoicesAsync();
                    foreach (var voice in voices)
                        cbTtsVoice.Items.Add(voice);
                    tbTtsStatus.Text = voices.Count == 0
                        ? "No Windows voices installed (open Settings → Time & language → Speech)."
                        : $"✓ {voices.Count} Windows voice(s) listed.";
                    tbTtsStatus.Foreground = voices.Count == 0
                        ? System.Windows.Media.Brushes.Goldenrod
                        : System.Windows.Media.Brushes.LightGreen;
                    break;
                default:
                    tbTtsStatus.Text = "Pick a non-Off provider first.";
                    tbTtsStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    break;
            }
            if (cbTtsVoice.Items.Count > 0 && cbTtsVoice.SelectedIndex < 0)
                cbTtsVoice.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            tbTtsStatus.Text = $"✗ {ex.Message}";
            tbTtsStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice-TTS] Refresh voices failed", ex);
        }
    }

    private void OnTtsSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var v = ReadVoiceFromUi();
            VoiceSettingsStore.Save(v);
            tbTtsSaveStatus.Text = "✓ Saved.";
            tbTtsSaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppLogger.Log($"[Voice-TTS] Saved provider={v.TtsProvider} voice={v.TtsVoice}");
        }
        catch (Exception ex)
        {
            tbTtsSaveStatus.Text = $"✗ {ex.Message}";
            tbTtsSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice-TTS] Save failed", ex);
        }
    }

    private void OnVoiceRefreshDevices(object sender, RoutedEventArgs e)
    {
        RefreshVoiceInputDevices(VoiceSettingsStore.Load().InputDeviceId);
        tbVoiceTestStatus.Text = $"Device list refreshed ({cbVoiceInputDevice.Items.Count} item(s)).";
        tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;
    }

    // Voice Test — START/STOP toggle drives the full pipeline.
    private async void OnVoiceTestStartStop(object sender, RoutedEventArgs e)
    {
        UpdateVoiceTestActiveLlmLabel();

        if (_voiceCapture is { IsCapturing: true })
        {
            // STOP path. Manual mode: drain any buffered PCM and run the pipeline
            // once before tearing down. Auto mode: just stop — pipeline already
            // ran on UtteranceEnded.
            var manual = rbVoiceModeManual.IsChecked == true;
            byte[]? pendingPcm = null;
            if (manual && _voiceCapture is not null)
                pendingPcm = _voiceCapture.ConsumePcmBuffer();

            try { _voiceCapture?.Stop(); } catch { }
            _pipelineCts?.Cancel();
            btnVoiceTestStart.Content = "START";
            tbVoiceTestStatus.Text = "Stopped.";
            tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;

            if (manual && pendingPcm is { Length: > 0 } && !_pipelineBusy)
                await RunPipelineOnceAsync(pendingPcm);
            return;
        }

        // START path.
        var v = ReadVoiceFromUi();
        VoiceSettingsStore.Save(v); // round-trip current UI state so Save STT/TTS isn't a prerequisite

        _voiceCapture ??= new VoiceCaptureService();
        _voicePlayback ??= new VoicePlaybackService();
        _pipelineCts = new CancellationTokenSource();

        _voiceCapture.VadThreshold = v.VadThreshold / 100f;
        _voiceCapture.BufferPcm = true;

        // Marshal events back to the UI thread — NAudio fires them from its
        // own capture thread.
        _voiceCapture.AmplitudeChanged -= OnAmplitudeChanged;
        _voiceCapture.UtteranceStarted -= OnUtteranceStarted;
        _voiceCapture.UtteranceEnded -= OnUtteranceEndedAsync;
        _voiceCapture.AmplitudeChanged += OnAmplitudeChanged;
        _voiceCapture.UtteranceStarted += OnUtteranceStarted;
        if (rbVoiceModeAuto.IsChecked == true)
            _voiceCapture.UtteranceEnded += OnUtteranceEndedAsync;

        try
        {
            var deviceNumber = ParseDeviceNumber(v.InputDeviceId);
            _voiceCapture.Start(deviceNumber);
            btnVoiceTestStart.Content = "STOP";
            tbVoiceTestStatus.Text = rbVoiceModeAuto.IsChecked == true
                ? "Listening (Auto VAD) — speak; press STOP to end."
                : "Recording (Manual) — press STOP to transcribe.";
            tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            btnVoiceTestStart.Content = "START";
            tbVoiceTestStatus.Text = $"✗ Capture start failed: {ex.Message}";
            tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice] Capture start failed", ex);
        }
    }

    private void OnAmplitudeChanged(float rms)
    {
        // Smooth-ish mapping — RMS rarely tops 0.3 in normal speech, so scale by 3x.
        var pct = Math.Min(100, rms * 300);
        Dispatcher.BeginInvoke(new Action(() => pbVoiceLevel.Value = pct), DispatcherPriority.Render);
    }

    private void OnUtteranceStarted()
    {
        // Seed the segment buffer with pre-roll so the first syllable isn't clipped.
        _voiceCapture?.SeedBufferWithPreRoll();
    }

    private async void OnUtteranceEndedAsync()
    {
        if (_voiceCapture is null) return;
        var pcm = _voiceCapture.ConsumePcmBuffer();
        if (pcm.Length < 8000) return; // <0.25s — too short to bother
        await Dispatcher.InvokeAsync(() => RunPipelineOnceAsync(pcm));
    }

    private async Task RunPipelineOnceAsync(byte[] pcm)
    {
        if (_pipelineBusy) return;
        _pipelineBusy = true;
        var ct = _pipelineCts?.Token ?? CancellationToken.None;

        try
        {
            var v = VoiceSettingsStore.Load();

            // ─── STT ────────────────────────────────────────────────
            var stt = CreateSttProvider(v);
            if (stt is null)
            {
                AppendTranscript($"[STT] Provider '{v.SttProvider}' not recognised.");
                return;
            }
            tbVoiceTestStatus.Text = $"STT ({stt.ProviderName})…";
            tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;

            var ready = await stt.EnsureReadyAsync(
                new Progress<string>(msg => Dispatcher.BeginInvoke(new Action(() =>
                {
                    tbVoiceTestStatus.Text = msg;
                    tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;
                }))), ct);
            if (!ready)
            {
                AppendTranscript("[STT] Provider reported not-ready (see status above).");
                return;
            }

            string transcript;
            try
            {
                transcript = await stt.TranscribeAsync(pcm, v.SttLanguage, ct);
            }
            catch (Exception ex)
            {
                AppendTranscript($"[STT-error] {ex.Message}");
                AppLogger.LogError("[Voice-STT] Transcribe failed", ex);
                return;
            }
            if (string.IsNullOrWhiteSpace(transcript))
            {
                AppendTranscript("[STT] (empty transcript)");
                return;
            }
            AppendTranscript($"You: {transcript}");

            // ─── LLM (whatever's selected on the LLM tab) ──────────
            if (!LlmGateway.IsActiveAvailable())
            {
                AppendTranscript("[LLM] Active backend not ready — open Settings → LLM and load/configure first.");
                return;
            }

            tbVoiceTestStatus.Text = "LLM…";
            string answer;
            try
            {
                await using var session = LlmGateway.OpenSession();
                answer = await session.SendAsync(transcript, ct);
            }
            catch (Exception ex)
            {
                AppendTranscript($"[LLM-error] {ex.Message}");
                AppLogger.LogError("[Voice-LLM] SendAsync failed", ex);
                return;
            }
            if (string.IsNullOrWhiteSpace(answer))
            {
                AppendTranscript("[LLM] (empty response)");
                return;
            }
            AppendTranscript($"AI: {answer}");

            // ─── TTS ───────────────────────────────────────────────
            var tts = CreateTtsProvider(v);
            if (tts is null)
            {
                tbVoiceTestStatus.Text = "Done (TTS off).";
                tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                return;
            }

            tbVoiceTestStatus.Text = $"TTS ({tts.ProviderName})…";
            byte[] audio;
            try
            {
                var clean = TtsTextCleaner.StripMarkdown(answer);
                audio = await tts.SynthesizeAsync(clean, v.TtsVoice, ct);
            }
            catch (Exception ex)
            {
                AppendTranscript($"[TTS-error] {ex.Message}");
                AppLogger.LogError("[Voice-TTS] Synthesize failed", ex);
                return;
            }
            if (audio.Length == 0)
            {
                tbVoiceTestStatus.Text = "TTS returned empty audio.";
                tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                return;
            }

            // Mute the mic during playback so the AI's own voice isn't fed
            // back into the next utterance. Unmute when playback completes.
            if (_voiceCapture is not null) _voiceCapture.Muted = true;
            try
            {
                _voicePlayback ??= new VoicePlaybackService();
                Action? unmute = null;
                unmute = () =>
                {
                    if (_voiceCapture is not null) _voiceCapture.Muted = false;
                    if (unmute is not null) _voicePlayback.PlaybackStopped -= unmute;
                };
                _voicePlayback.PlaybackStopped += unmute;
                _voicePlayback.Play(audio, tts.AudioFormat);
                tbVoiceTestStatus.Text = "Playing back AI response…";
                tbVoiceTestStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                if (_voiceCapture is not null) _voiceCapture.Muted = false;
                AppendTranscript($"[Playback-error] {ex.Message}");
                AppLogger.LogError("[Voice-Playback] Play failed", ex);
            }
        }
        finally
        {
            _pipelineBusy = false;
        }
    }

    private void AppendTranscript(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tbVoiceTranscript.AppendText(line + Environment.NewLine);
            tbVoiceTranscript.ScrollToEnd();
        }), DispatcherPriority.Background);
    }
}
