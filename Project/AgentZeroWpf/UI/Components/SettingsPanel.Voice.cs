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
                    _voiceCapture.VadThreshold = SensitivityToThreshold(slVoiceSensitivity.Value);
            };

            rbVoiceModeAuto.IsChecked = v.VoiceTestAutoMode;
            rbVoiceModeManual.IsChecked = !v.VoiceTestAutoMode;

            tbVoiceLlmMaxTokens.Text = v.LlmMaxTokens.ToString();

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
        if (int.TryParse(tbVoiceLlmMaxTokens.Text, out var maxTok) && maxTok > 0)
            v.LlmMaxTokens = maxTok;
        return v;
    }

    /// <summary>
    /// Build the chat session for the Voice Test pipeline. For External LLM
    /// providers we override <c>MaxTokens</c> with <see cref="VoiceSettings.LlmMaxTokens"/>
    /// so TTS playback stays short. Local backend has no per-call override
    /// path (<see cref="LlmService.OpenSession"/> is parameterless and reads
    /// MaxTokens from load-time options) — we fall back to the standard
    /// gateway and log the asymmetry once per call so the user knows.
    /// </summary>
    private static ILocalChatSession OpenVoiceLlmSession(VoiceSettings v)
    {
        var s = LlmSettingsStore.Load();
        if (s.ActiveBackend == LlmActiveBackend.External)
        {
            var provider = s.CreateExternalProvider()
                ?? throw new InvalidOperationException($"Unknown external provider '{s.External.Provider}'.");
            var model = s.ResolveExternalModel();
            if (string.IsNullOrEmpty(model))
            {
                (provider as IDisposable)?.Dispose();
                throw new InvalidOperationException(
                    $"No model selected for {s.External.Provider}. Open Settings → LLM → External and pick one.");
            }
            AppLogger.Log($"[Voice-LLM] External session | provider={s.External.Provider} model={model} maxTok={v.LlmMaxTokens} (voice override)");
            return new ExternalChatSession(provider, model, v.LlmMaxTokens, s.Temperature);
        }

        AppLogger.Log($"[Voice-LLM] Local session | LlmMaxTokens override ignored (LlmService loads MaxTokens at model-load); LLM-tab cap applies.");
        return LlmGateway.OpenSession();
    }

    private static int ParseDeviceNumber(string id) => VoiceRuntimeFactory.ParseDeviceNumber(id);

    /// <summary>Origin sensitivity-curve parity. See <see cref="VoiceRuntimeFactory.SensitivityToThreshold"/>.</summary>
    private static float SensitivityToThreshold(double sensitivityPercent)
        => VoiceRuntimeFactory.SensitivityToThreshold(sensitivityPercent);

    /// <summary>
    /// Marshalled status update for the Voice Test row. Safe to call from any
    /// thread — pipeline runs on a thread-pool thread per origin parity, so all
    /// UI mutations must hop the dispatcher.
    /// </summary>
    private void SetVoiceTestStatus(string text, System.Windows.Media.Brush brush)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tbVoiceTestStatus.Text = text;
            tbVoiceTestStatus.Foreground = brush;
        }));
    }

    /// <summary>
    /// Aggressive stop — capture, in-flight pipeline (via cts), and any
    /// running TTS playback all halted. Defensively unmutes the mic so a
    /// crash mid-playback can't leave it permanently silenced. Idempotent;
    /// safe to call multiple times.
    /// </summary>
    private void StopAllVoiceActivity(string reason)
    {
        AppLogger.Log($"[Voice] Stop all activity ({reason})");
        try { _voiceCapture?.Stop(); } catch { }
        try { _pipelineCts?.Cancel(); } catch { }
        try { _voicePlayback?.Stop(); } catch { }
        if (_voiceCapture is not null) _voiceCapture.Muted = false;
        btnVoiceTestStart.Content = "START";
        btnVoiceInterrupt.IsEnabled = false;
        SetVoiceTestStatus("Stopped.", System.Windows.Media.Brushes.SkyBlue);
    }

    /// <summary>
    /// Soft stop — silences the AI's current speech and cancels the in-flight
    /// pipeline, but leaves capture running so the user can keep talking.
    /// Replaces <see cref="_pipelineCts"/> with a fresh source so the next
    /// utterance triggers a new pipeline cleanly. Mute auto-clears via the
    /// PlaybackStopped → unmute callback chain.
    /// </summary>
    private void InterruptVoicePipeline(string reason)
    {
        AppLogger.Log($"[Voice] Interrupt ({reason})");
        try { _voicePlayback?.Stop(); } catch { }
        try { _pipelineCts?.Cancel(); } catch { }
        _pipelineCts = new CancellationTokenSource();
        if (_voiceCapture is not null) _voiceCapture.Muted = false;
        SetVoiceTestStatus("Interrupted — keep speaking.", System.Windows.Media.Brushes.Goldenrod);
    }

    /// <summary>
    /// Cancellation gate between pipeline phases. Returns true if the pipeline
    /// should bail out — caller must `return` immediately after. Logs the phase
    /// and surfaces it to the user. Always restores the mic Muted flag.
    /// </summary>
    private bool ShouldBailOut(CancellationToken ct, string phase)
    {
        if (!ct.IsCancellationRequested) return false;
        AppLogger.Log($"[Voice] Pipeline cancelled at phase={phase}");
        if (_voiceCapture is not null) _voiceCapture.Muted = false;
        SetVoiceTestStatus($"Cancelled at {phase}.", System.Windows.Media.Brushes.Goldenrod);
        return true;
    }

    /// <summary>Build the active <see cref="ISpeechToText"/>; delegates to <see cref="VoiceRuntimeFactory.BuildStt"/>.</summary>
    private static ISpeechToText? CreateSttProvider(VoiceSettings v) => VoiceRuntimeFactory.BuildStt(v);

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

    private async void OnSttRefreshWebnoriModels(object sender, RoutedEventArgs e)
    {
        // Pull the live /v1/models list — same path the LLM-External tab uses.
        // Audio-capable gating is server-side; we don't pre-filter so users see
        // every available model. If the call fails (offline / quota), fall back
        // to the local catalog so the picker is never empty.
        var current = (cbSttWebnoriModel.SelectedItem as string)
                      ?? cbSttWebnoriModel.Text
                      ?? "";

        btnSttRefreshWebnoriModels.IsEnabled = false;
        tbSttStatus.Text = "Listing models…";
        tbSttStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;

        var provider = LlmProviderFactory.CreateWebnori();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var models = await provider.ListModelsAsync(cts.Token);

            cbSttWebnoriModel.Items.Clear();
            foreach (var m in models)
                cbSttWebnoriModel.Items.Add(m.Id);

            if (cbSttWebnoriModel.Items.Count == 0)
            {
                foreach (var id in WebnoriDefaults.KnownModels)
                    cbSttWebnoriModel.Items.Add(id);
                tbSttStatus.Text = $"⚠ Server returned 0 models — catalog fallback ({cbSttWebnoriModel.Items.Count}).";
                tbSttStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
            }
            else
            {
                tbSttStatus.Text = $"✓ {models.Count} model(s) listed (live).";
                tbSttStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            }

            RestoreOrPickFirst(cbSttWebnoriModel, current);
        }
        catch (Exception ex)
        {
            cbSttWebnoriModel.Items.Clear();
            foreach (var id in WebnoriDefaults.KnownModels)
                cbSttWebnoriModel.Items.Add(id);
            RestoreOrPickFirst(cbSttWebnoriModel, current);
            tbSttStatus.Text = $"✗ Live refresh failed: {ex.Message} — catalog fallback.";
            tbSttStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice-STT] Webnori ListModels failed", ex);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            btnSttRefreshWebnoriModels.IsEnabled = true;
        }
    }

    private static void RestoreOrPickFirst(System.Windows.Controls.ComboBox box, string previous)
    {
        if (!string.IsNullOrEmpty(previous))
        {
            foreach (var item in box.Items)
                if (item is string s && s == previous)
                {
                    box.SelectedItem = item;
                    return;
                }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
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

            // Origin parity (SettingsPanel.xaml.cs:785). Eagerly warm up the
            // chosen STT provider so the first transcribe doesn't stall the UI
            // on cold-start factory init (Whisper "small" is 487 MB on CPU).
            // Fire-and-forget — failures are surfaced into tbSttSaveStatus but
            // don't roll back the saved settings.
            _ = WarmUpSttProviderAsync(v);
        }
        catch (Exception ex)
        {
            tbSttSaveStatus.Text = $"✗ {ex.Message}";
            tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Voice-STT] Save failed", ex);
        }
    }

    private async Task WarmUpSttProviderAsync(VoiceSettings v)
    {
        var stt = CreateSttProvider(v);
        if (stt is null) return;

        var progress = new Progress<string>(msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            tbSttSaveStatus.Text = msg;
            tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;
        })));

        try
        {
            // Whisper.net's factory init is synchronous CPU+IO-bound work
            // (mmap + native init). Even though EnsureReadyAsync is `async`,
            // it doesn't yield internally, so the call must be off-loaded to
            // a thread-pool thread or the UI freezes for several seconds.
            var ready = await Task.Run(async () => await stt.EnsureReadyAsync(progress));
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                tbSttSaveStatus.Text = ready
                    ? "✓ Saved + model ready."
                    : "✓ Saved (provider not ready — see status).";
                tbSttSaveStatus.Foreground = ready
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.Goldenrod;
            }));
            AppLogger.Log($"[Voice-STT] Warm-up done | provider={stt.ProviderName} ready={ready}");
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                tbSttSaveStatus.Text = $"✓ Saved (warm-up failed: {ex.Message})";
                tbSttSaveStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
            }));
            AppLogger.LogError("[Voice-STT] Warm-up failed", ex);
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
        SetVoiceTestStatus(
            $"Device list refreshed ({cbVoiceInputDevice.Items.Count} item(s)).",
            System.Windows.Media.Brushes.SkyBlue);
    }

    // Voice Test — START/STOP toggle drives the full pipeline.
    private void OnVoiceTestStartStop(object sender, RoutedEventArgs e)
    {
        UpdateVoiceTestActiveLlmLabel();

        // Treat the button label as the source of truth for state. This makes
        // STOP work even after capture already auto-stopped — e.g. when the
        // pipeline is mid-playback and the user wants to abort the AI's reply.
        var isStopping = (btnVoiceTestStart.Content as string) == "STOP";
        if (isStopping)
        {
            // Manual mode: drain any buffered PCM and run the pipeline once
            // BEFORE tearing capture down. Auto mode never reaches this path
            // with pending audio because UtteranceEnded already kicked the
            // pipeline. If capture is already stopped (pipeline-only stop),
            // there's nothing to drain.
            var manual = rbVoiceModeManual.IsChecked == true;
            byte[]? pendingPcm = null;
            if (manual && _voiceCapture is { IsCapturing: true })
                pendingPcm = _voiceCapture.ConsumePcmBuffer();

            StopAllVoiceActivity("user pressed STOP");

            if (manual && pendingPcm is { Length: > 0 } && !_pipelineBusy)
            {
                var pcmRef = pendingPcm;
                // Manual STOP needs a fresh cts because StopAllVoiceActivity
                // just cancelled the previous one — otherwise the pipeline
                // would bail at the very first ct check.
                _pipelineCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    AppLogger.Log($"[Voice] Task.Run transcribe start (manual) | thread=#{Environment.CurrentManagedThreadId} pcm={pcmRef.Length}");
                    try { await RunPipelineOnceAsync(pcmRef); }
                    catch (Exception ex) { AppLogger.LogError("[Voice] Manual pipeline crashed", ex); }
                    AppLogger.Log("[Voice] Task.Run transcribe done (manual)");
                });
            }
            return;
        }

        // START path.
        var v = ReadVoiceFromUi();
        VoiceSettingsStore.Save(v); // round-trip current UI state so Save STT/TTS isn't a prerequisite

        _voiceCapture ??= new VoiceCaptureService();
        _voicePlayback ??= new VoicePlaybackService();
        _pipelineCts = new CancellationTokenSource();

        _voiceCapture.VadThreshold = SensitivityToThreshold(slVoiceSensitivity.Value);
        _voiceCapture.BufferPcm = true;
        AppLogger.Log($"[Voice] VAD threshold = {_voiceCapture.VadThreshold:F4} (sensitivity {(int)slVoiceSensitivity.Value}/100)");

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
            btnVoiceInterrupt.IsEnabled = true;
            SetVoiceTestStatus(
                rbVoiceModeAuto.IsChecked == true
                    ? "Listening (Auto VAD) — speak; press STOP to end, INTERRUPT to cut off the AI."
                    : "Recording (Manual) — press STOP to transcribe.",
                System.Windows.Media.Brushes.LightGreen);
        }
        catch (Exception ex)
        {
            btnVoiceTestStart.Content = "START";
            btnVoiceInterrupt.IsEnabled = false;
            SetVoiceTestStatus($"✗ Capture start failed: {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
            AppLogger.LogError("[Voice] Capture start failed", ex);
        }
    }

    private void OnVoiceInterrupt(object sender, RoutedEventArgs e)
    {
        InterruptVoicePipeline("user pressed INTERRUPT");
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

    private void OnUtteranceEndedAsync()
    {
        if (_voiceCapture is null) return;
        var pcm = _voiceCapture.ConsumePcmBuffer();
        if (pcm.Length < 8000) return; // <0.25s — too short to bother

        // Origin parity (SettingsPanel.xaml.cs:1268). Run the whole STT → LLM
        // → TTS pipeline on a thread-pool thread so the UI never freezes
        // during the synchronous Whisper factory init or the LLM call. UI
        // updates inside RunPipelineOnceAsync hop the dispatcher via
        // SetVoiceTestStatus / AppendTranscript.
        _ = Task.Run(async () =>
        {
            AppLogger.Log($"[Voice] Task.Run transcribe start (auto) | thread=#{Environment.CurrentManagedThreadId} pcm={pcm.Length}");
            try { await RunPipelineOnceAsync(pcm); }
            catch (Exception ex) { AppLogger.LogError("[Voice] Auto pipeline crashed", ex); }
            AppLogger.Log("[Voice] Task.Run transcribe done (auto)");
        });
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
            SetVoiceTestStatus($"STT ({stt.ProviderName})…", System.Windows.Media.Brushes.SkyBlue);

            var ready = await stt.EnsureReadyAsync(
                new Progress<string>(msg => SetVoiceTestStatus(msg, System.Windows.Media.Brushes.SkyBlue)),
                ct);
            if (!ready)
            {
                AppendTranscript("[STT] Provider reported not-ready (see status above).");
                return;
            }
            if (ShouldBailOut(ct, "after STT-ready")) return;

            string transcript;
            try
            {
                transcript = await stt.TranscribeAsync(pcm, v.SttLanguage, ct);
            }
            catch (OperationCanceledException) { _ = ShouldBailOut(ct, "STT cancel"); return; }
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
            if (ShouldBailOut(ct, "after STT")) return;

            // ─── LLM (whatever's selected on the LLM tab) ──────────
            if (!LlmGateway.IsActiveAvailable())
            {
                AppendTranscript("[LLM] Active backend not ready — open Settings → LLM and load/configure first.");
                return;
            }

            SetVoiceTestStatus($"LLM (max {v.LlmMaxTokens} tok)…", System.Windows.Media.Brushes.SkyBlue);
            string answer;
            try
            {
                await using var session = OpenVoiceLlmSession(v);
                answer = await session.SendAsync(transcript, ct);
            }
            catch (OperationCanceledException) { _ = ShouldBailOut(ct, "LLM cancel"); return; }
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
            if (ShouldBailOut(ct, "after LLM")) return;

            // ─── TTS ───────────────────────────────────────────────
            var tts = CreateTtsProvider(v);
            if (tts is null)
            {
                SetVoiceTestStatus("Done (TTS off).", System.Windows.Media.Brushes.LightGreen);
                return;
            }

            SetVoiceTestStatus($"TTS ({tts.ProviderName})…", System.Windows.Media.Brushes.SkyBlue);
            byte[] audio;
            try
            {
                var clean = TtsTextCleaner.StripMarkdown(answer);
                audio = await tts.SynthesizeAsync(clean, v.TtsVoice, ct);
            }
            catch (OperationCanceledException) { _ = ShouldBailOut(ct, "TTS cancel"); return; }
            catch (Exception ex)
            {
                AppendTranscript($"[TTS-error] {ex.Message}");
                AppLogger.LogError("[Voice-TTS] Synthesize failed", ex);
                return;
            }
            if (audio.Length == 0)
            {
                SetVoiceTestStatus("TTS returned empty audio.", System.Windows.Media.Brushes.Goldenrod);
                return;
            }
            if (ShouldBailOut(ct, "before playback")) return;

            // Mute the mic during playback so the AI's own voice isn't fed
            // back into the next utterance. The cleanup runs in `finally`
            // and on PlaybackStopped, so a STOP press or a crash mid-playback
            // never leaves the mic permanently muted.
            bool muteSet = false;
            try
            {
                if (_voiceCapture is not null) { _voiceCapture.Muted = true; muteSet = true; }
                _voicePlayback ??= new VoicePlaybackService();
                Action? unmute = null;
                unmute = () =>
                {
                    if (_voiceCapture is not null) _voiceCapture.Muted = false;
                    if (unmute is not null) _voicePlayback.PlaybackStopped -= unmute;
                };
                _voicePlayback.PlaybackStopped += unmute;
                _voicePlayback.Play(audio, tts.AudioFormat);
                SetVoiceTestStatus("Playing back AI response…", System.Windows.Media.Brushes.LightGreen);
            }
            catch (Exception ex)
            {
                if (muteSet && _voiceCapture is not null) _voiceCapture.Muted = false;
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
