using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;
using Brush = System.Windows.Media.Brush;

namespace AgentZeroWpf.UI.APP;

/// <summary>
/// Standalone popup that hosts the developer test panels — currently just
/// the Virtual Voice injector. Built with a TabControl so additional test
/// panels (e.g. virtual keyboard, IPC ping) drop in as new tabs without
/// touching AgentBotWindow.
///
/// <para><b>Active models</b> read straight from <see cref="VoiceSettingsStore"/>
/// each turn — the Settings → Voice tab is the source of truth, so changing
/// STT model / TTS voice there is reflected here on the next Activated
/// event (or the next Speak click). Visible in the top status row.</para>
///
/// <para><b>History</b>: every completed turn (success or failure) lands in
/// the bottom <c>lstHistory</c> ListBox so the user can A/B different
/// inputs against the same model, or compare model swaps without losing
/// prior runs. Insert-at-top so the latest is always at eye level.</para>
/// </summary>
public partial class TestToolsWindow : Window
{
    private VoicePlaybackService? _replayPlayer;
    private readonly ObservableCollection<VirtualVoiceHistoryItem> _history = new();
    private const int HistoryCap = 50;
    private bool _isBusy;
    // Tracks whether *we* (the test window) muted AskBot during the current
    // turn, so we can restore the prior state on turn end without stomping
    // a manual mute the user set independently.
    private bool _weMutedAskBot;

    // Virtual-voice synth defaults — tuned 2026-04-29 after observing that
    // SAPI Heami's default rate is fast enough to confuse Whisper on phoneme
    // boundaries, and Whisper hallucinates more on absolute-silence-then-
    // speech transitions than on speech surrounded by quiet room tone.
    private const int    DefaultSapiRate     = -2;     // SAPI rate (-10..10), -2 ≈ 10–15% slower
    private const double DefaultOpenAiSpeed  = 0.85;   // OpenAI tts-1 speed (0.25..4.0)
    private const double DefaultLeadSilence  = 1.0;    // seconds of noise before the speech
    private const double DefaultTrailSilence = 1.0;    // seconds of noise after the speech
    private const double DefaultNoiseDbfs    = -45;    // RMS level for the noise (dBFS)

    private static readonly Brush PendingBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x85, 0x85, 0x85));
    private static readonly Brush SuccessBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly Brush ErrorBrush   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));

    public TestToolsWindow()
    {
        InitializeComponent();
        lstHistory.ItemsSource = _history;
        Loaded += (_, _) =>
        {
            RefreshActiveModels();
            UpdateModeLine();
            txtVoiceInput.Focus();
        };
        Activated += (_, _) => RefreshActiveModels();
    }

    private void OnSpeakerPlaybackToggled(object sender, RoutedEventArgs e) => UpdateModeLine();

    private void OnVoiceInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = RunSpeakAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnVoiceSpeak(object sender, RoutedEventArgs e) => _ = RunSpeakAsync();

    /// <summary>
    /// Quick-phrase button handler. One click both fills the textbox
    /// (so the user can see what was sent) and kicks off the synth +
    /// STT pipeline. RunSpeakAsync clears the textbox after capture,
    /// matching the manual-Enter flow.
    /// </summary>
    private void OnQuickPhraseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not string phrase || string.IsNullOrWhiteSpace(phrase)) return;
        txtVoiceInput.Text = phrase;
        _ = RunSpeakAsync();
    }

    private async Task RunSpeakAsync()
    {
        // Re-entrancy guard. One turn at a time so the user can't fire
        // overlapping playbacks (acoustic-loop) or stack queued bypass
        // calls. Visual: SetTurnBusy disables Speak / quick-phrase / input
        // until the turn lands.
        if (_isBusy)
        {
            SetStatus("(busy — wait for the previous turn to finish)", isError: false);
            return;
        }

        var text = txtVoiceInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Type something first.", isError: false);
            return;
        }

        // Clear the textbox so the typed phrase is visibly captured and
        // the field is ready for the next line. (Buttons are disabled
        // until the turn lands, so the user can't actually type+submit
        // mid-turn — but they can type ahead while waiting.)
        txtVoiceInput.Clear();

        // Snapshot models for this turn so the history row reflects what was
        // *actually* used, not what the user might switch to mid-turn.
        var v = VoiceSettingsStore.Load();

        // Pre-create the history item with a placeholder result; the worker
        // updates it via INotifyPropertyChanged when the result lands.
        var item = new VirtualVoiceHistoryItem(text);
        _history.Insert(0, item);
        while (_history.Count > HistoryCap)
            _history.RemoveAt(_history.Count - 1);
        lstHistory.ScrollIntoView(item);

        SetTurnBusy(true);
        var shouldMute = chkAutoMuteAskBot?.IsChecked == true;
        var playSpeaker = chkPlaySpeaker?.IsChecked == true;
        TryMuteAskBot(shouldMute);
        try
        {
            await RunTurnAsync(item, v, playSpeaker);
        }
        finally
        {
            TryUnmuteAskBot();
            SetTurnBusy(false);
        }
    }

    /// <summary>
    /// Find the running AskBot window and soft-mute its mic if it isn't
    /// already muted. Records the fact so <see cref="TryUnmuteAskBot"/>
    /// only un-mutes when *we* did the muting (preserves a manual mute
    /// the user might have set independently).
    /// </summary>
    private void TryMuteAskBot(bool shouldMute)
    {
        _weMutedAskBot = false;
        if (!shouldMute) return;
        var bot = FindAskBot();
        if (bot is null) return;
        // Only act if AskBot's mic is currently live (no point muting a
        // mic that's already off or already muted).
        if (!bot.IsVoiceMicLive()) return;
        bot.SetVoiceMicMuted(true, source: "test-tools auto-mute");
        _weMutedAskBot = true;
    }

    private void TryUnmuteAskBot()
    {
        if (!_weMutedAskBot) return;
        _weMutedAskBot = false;
        var bot = FindAskBot();
        if (bot is null) return;
        bot.SetVoiceMicMuted(false, source: "test-tools auto-restore");
    }

    private static AgentBotWindow? FindAskBot() =>
        Application.Current?.Windows.OfType<AgentBotWindow>().FirstOrDefault();

    /// <summary>
    /// Toggle UI controls while a turn is in flight. Disables the input
    /// textbox, Speak button, quick-phrase buttons, and bypass checkbox so
    /// the user can't fire overlapping turns. Re-enabled when the turn lands
    /// in the history row (success or failure).
    /// </summary>
    private void SetTurnBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<bool>(SetTurnBusy), DispatcherPriority.Normal, busy);
            return;
        }
        _isBusy = busy;
        if (txtVoiceInput is not null) txtVoiceInput.IsEnabled = !busy;
        if (btnVoiceSpeak is not null) btnVoiceSpeak.IsEnabled = !busy;
        if (chkPlaySpeaker is not null) chkPlaySpeaker.IsEnabled = !busy;
        if (chkAutoMuteAskBot is not null) chkAutoMuteAskBot.IsEnabled = !busy;
        if (pnlQuickPhrases is not null) pnlQuickPhrases.IsEnabled = !busy;
    }

    /// <summary>
    /// Single test turn. Always: synth → resample → STT → inject the
    /// transcript into AskBot (mic-independent). Optionally: also play the
    /// audio through the speaker so the human can hear what was "spoken".
    ///
    /// <para>This unifies what used to be two modes (bypass / acoustic-loop).
    /// The old acoustic-loop went speaker → mic → STT, which Windows echo
    /// cancellation reliably broke; the old bypass kept the result in the
    /// popup only, which the user perceived as "AgentBot unresponsive."
    /// Both modes now drive AskBot the same way; the only knob is whether
    /// the speaker plays for human verification.</para>
    ///
    /// <para>Heavy work (SAPI Speak, NAudio resample, Whisper inference) is
    /// pushed off the UI thread via <c>Task.Run</c>. Per-stage Stopwatch
    /// timings are logged so the next slowdown can be diagnosed without
    /// re-instrumenting.</para>
    /// </summary>
    private async Task RunTurnAsync(VirtualVoiceHistoryItem item, VoiceSettings v, bool playSpeaker)
    {
        if (string.Equals(v.TtsProvider, TtsProviderNames.Off, StringComparison.OrdinalIgnoreCase))
        {
            FailItem(item, "TTS provider is Off — Settings → Voice → pick one.");
            SetStatus("✗ TTS Off", isError: true);
            return;
        }

        try
        {
            var tts = VoiceRuntimeFactory.BuildTts(v)
                ?? throw new InvalidOperationException($"TTS '{v.TtsProvider}' could not be constructed.");
            var stt = VoiceRuntimeFactory.BuildStt(v)
                ?? throw new InvalidOperationException($"STT '{v.SttProvider}' could not be constructed.");

            SetStatus($"synthesising via {v.TtsProvider}…", isError: false);
            await Task.Run(async () =>
            {
                var totalSw = Stopwatch.StartNew();
                try
                {
                    // ── Stage 1: TTS synthesis ──
                    // Apply slower-rate setting so phonemes have more headroom
                    // for the STT model to lock onto (Whisper struggles on the
                    // SAPI Heami default delivery rate).
                    ApplySlowSpeechRate(tts);

                    var sw = Stopwatch.StartNew();
                    var rawWav = await tts.SynthesizeAsync(item.Input, v.TtsVoice);
                    sw.Stop();
                    var synthMs = sw.ElapsedMilliseconds;
                    if (rawWav is null || rawWav.Length == 0)
                        throw new InvalidOperationException("TTS returned empty audio.");

                    // Pad with low-level noise so STT sees natural room tone
                    // bracketing the speech rather than absolute silence.
                    var wav = WavNoisePadder.PadWithNoise(
                        rawWav, DefaultLeadSilence, DefaultTrailSilence, DefaultNoiseDbfs);

                    AppLogger.Log($"[TestTools-Bypass] [1/3] synth+pad | {synthMs} ms · raw={rawWav.Length} → padded={wav.Length} bytes · provider={v.TtsProvider} · rate={DescribeTtsSpeed(tts)}");

                    // Stash the PADDED WAV in the history item so the row's ▶
                    // button replays exactly what STT actually saw.
                    SetItemAudio(item, wav, tts.AudioFormat);

                    SetStatus("decoding WAV → PCM 16k mono…", isError: false);

                    // ── Stage 2: WAV → PCM 16k mono (resample if needed) ──
                    sw.Restart();
                    var pcm = WavToPcm.To16kMono(wav);
                    sw.Stop();
                    var decodeMs = sw.ElapsedMilliseconds;
                    if (pcm.Length == 0)
                        throw new InvalidOperationException("WAV decode produced empty PCM.");
                    var pcmSeconds = pcm.Length / 32_000.0;
                    AppLogger.Log($"[TestTools-Bypass] [2/3] decode | {decodeMs} ms · pcm_bytes={pcm.Length} (~{pcmSeconds:F2}s audio)");

                    var debugPath = SaveDebugWav(wav, pcm);
                    if (debugPath is not null)
                        AppLogger.Log($"[TestTools-Bypass] debug WAV → {debugPath}");

                    SetStatus($"transcribing via {v.SttProvider}… (~{pcmSeconds:F1}s audio)", isError: false);

                    // ── Stage 3: STT inference ──
                    sw.Restart();
                    var ready = await stt.EnsureReadyAsync();
                    if (!ready) throw new InvalidOperationException("STT failed to ready.");
                    var transcript = (await stt.TranscribeAsync(pcm, v.SttLanguage)) ?? string.Empty;
                    sw.Stop();
                    var sttMs = sw.ElapsedMilliseconds;
                    var rtFactor = pcmSeconds > 0 ? sttMs / 1000.0 / pcmSeconds : 0;
                    AppLogger.Log($"[TestTools-Bypass] [3/3] STT | {sttMs} ms · provider={v.SttProvider} · {rtFactor:F2}x realtime · chars={transcript.Length} · text=\"{Trunc(transcript, 80)}\"");

                    totalSw.Stop();

                    // ── Stage 4: ALWAYS inject transcript into AskBot ──
                    // Used to be conditional on "acoustic loop" mode, but the
                    // user's mental model is "the test should drive AskBot."
                    // Skip only if STT returned empty (nothing useful to send).
                    var bot = FindAskBot();
                    string sentTag;
                    if (bot is not null && !string.IsNullOrWhiteSpace(transcript))
                    {
                        bot.SendVoiceTranscript(transcript);
                        sentTag = "sent to AskBot";
                    }
                    else if (bot is null)
                    {
                        sentTag = "(no AskBot window)";
                    }
                    else
                    {
                        sentTag = "(empty transcript — not sent)";
                    }

                    // ── Stage 5: optional speaker playback for verification ──
                    long playMs = 0;
                    if (playSpeaker)
                    {
                        playMs = EstimateWavDurationMs(wav);
                        SetStatus($"▶ playing through speaker (~{playMs / 1000.0:F1}s)…", isError: false);
                        _replayPlayer ??= new VoicePlaybackService();
                        _replayPlayer.Play(wav, tts.AudioFormat);
                        await Task.Delay((int)playMs + 200);
                    }

                    var tail = $"{ProviderTag(v)} · synth {synthMs}ms · decode {decodeMs}ms · STT {sttMs}ms ({rtFactor:F1}x rt)" +
                               (playSpeaker ? $" · play {playMs}ms" : " · silent") +
                               $" · total {totalSw.ElapsedMilliseconds}ms · {sentTag}";
                    AppLogger.Log($"[TestTools] DONE | {tail}");

                    if (string.IsNullOrWhiteSpace(transcript))
                        SucceedItem(item, "(empty transcript)", tail);
                    else
                        SucceedItem(item, transcript, tail);
                    SetStatus($"✓ done · {totalSw.ElapsedMilliseconds}ms · {sentTag}", isError: false);
                }
                finally
                {
                    (tts as IDisposable)?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[TestTools] turn failed", ex);
            FailItem(item, $"{ex.GetType().Name}: {ex.Message}");
            SetStatus($"✗ {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Parse a WAV byte array's total playback duration. Returns 0 on
    /// any failure — caller falls back to a best-effort fixed delay if
    /// it cares about timing precision.
    /// </summary>
    private static int EstimateWavDurationMs(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length < 44) return 0;
        try
        {
            var patched = VoicePlaybackService.PatchWavHeaderSizes(wavBytes);
            using var ms = new MemoryStream(patched);
            using var reader = new NAudio.Wave.WaveFileReader(ms);
            return (int)reader.TotalTime.TotalMilliseconds;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Apply the project-default slower speech rate via concrete-type
    /// downcast — keeps the <see cref="ITextToSpeech"/> interface unchanged
    /// while still letting the test tooling slow down delivery. WindowsTts
    /// uses SAPI's integer rate; OpenAiTts uses the API's speed multiplier.
    /// Other providers are left at their default.
    /// </summary>
    private static void ApplySlowSpeechRate(ITextToSpeech tts)
    {
        if (tts is WindowsTts win) win.Rate = DefaultSapiRate;
        else if (tts is OpenAiTts oai) oai.Speed = DefaultOpenAiSpeed;
    }

    private static string DescribeTtsSpeed(ITextToSpeech tts) => tts switch
    {
        WindowsTts w  => $"SAPI rate={w.Rate}",
        OpenAiTts  o  => $"OpenAI speed={o.Speed:F2}",
        _             => "default",
    };

    // ── Replay button handler (per-row playback) ─────────────────────────

    private void OnPlayHistoryItem(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not VirtualVoiceHistoryItem item) return;
        var (wav, format) = item.GetAudio();
        if (wav is null || wav.Length == 0) return;

        try
        {
            _replayPlayer ??= new VoicePlaybackService();
            _replayPlayer.Play(wav, format);
            SetStatus($"▶ replaying turn {item.Time} · {wav.Length} bytes · {format}", isError: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[TestTools] replay failed", ex);
            SetStatus($"✗ replay: {ex.Message}", isError: true);
        }
    }

    // Marshal SetAudio to UI thread (PropertyChanged on CanPlay must run
    // where the binding subscribers live).
    private void SetItemAudio(VirtualVoiceHistoryItem item, byte[] wav, string format)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<VirtualVoiceHistoryItem, byte[], string>(SetItemAudio),
                DispatcherPriority.Normal, item, wav, format);
            return;
        }
        item.SetAudio(wav, format);
    }

    // ── History item state transitions (marshal to UI thread) ────────────

    private void SucceedItem(VirtualVoiceHistoryItem item, string result, string tail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<VirtualVoiceHistoryItem, string, string>(SucceedItem),
                DispatcherPriority.Normal, item, result, tail);
            return;
        }
        item.MarkSuccess(result, tail);
    }

    private void FailItem(VirtualVoiceHistoryItem item, string error)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<VirtualVoiceHistoryItem, string>(FailItem),
                DispatcherPriority.Normal, item, error);
            return;
        }
        item.MarkFailure(error);
    }

    // ── Active-model display ────────────────────────────────────────────

    private void RefreshActiveModels()
    {
        if (txtActiveModels is null) return;
        var v = VoiceSettingsStore.Load();
        var stt = FormatStt(v);
        var tts = FormatTts(v);
        txtActiveModels.Text = $"STT: {stt}    ·    TTS: {tts}";
    }

    private void UpdateModeLine()
    {
        if (txtActiveMode is null) return;
        var playSpeaker = chkPlaySpeaker?.IsChecked == true;
        txtActiveMode.Text = playSpeaker
            ? "mode: synth → STT direct → AskBot · ALSO speaker playback (verification)"
            : "mode: synth → STT direct → AskBot · silent (no speaker playback)";
    }

    private static string FormatStt(VoiceSettings v) => v.SttProvider switch
    {
        SttProviderNames.WhisperLocal => $"WhisperLocal · model={v.SttWhisperModel} · lang={v.SttLanguage} · gpu={v.SttUseGpu}",
        SttProviderNames.OpenAIWhisper => $"OpenAIWhisper · lang={v.SttLanguage}",
        SttProviderNames.WebnoriGemma => $"WebnoriGemma · model={(string.IsNullOrEmpty(v.SttWebnoriModel) ? "(default)" : v.SttWebnoriModel)} · lang={v.SttLanguage}",
        SttProviderNames.LocalGemma => $"LocalGemma · model={(string.IsNullOrEmpty(v.SttLocalGemmaModelId) ? "(unset)" : v.SttLocalGemmaModelId)} · lang={v.SttLanguage}",
        _ => $"{v.SttProvider}",
    };

    private static string FormatTts(VoiceSettings v)
    {
        if (string.Equals(v.TtsProvider, TtsProviderNames.Off, StringComparison.OrdinalIgnoreCase))
            return "Off";
        var voice = string.IsNullOrEmpty(v.TtsVoice) ? "(default)" : v.TtsVoice;
        return $"{v.TtsProvider} · voice={voice}";
    }

    private static string ProviderTag(VoiceSettings v) =>
        $"STT={v.SttProvider}/{(v.SttProvider == SttProviderNames.WhisperLocal ? v.SttWhisperModel : v.SttLanguage)}, TTS={v.TtsProvider}";

    // ── Debug WAV dump (post-resample audio that STT actually saw) ──────

    private static string? SaveDebugWav(byte[] origWav, byte[] pcm16k)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return null;
            var dir = Path.Combine(local, "AgentZeroLite", "debug");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var origPath = Path.Combine(dir, $"virtualvoice-{stamp}-tts.wav");
            var pcmPath  = Path.Combine(dir, $"virtualvoice-{stamp}-stt-input-16k.wav");

            File.WriteAllBytes(origPath, origWav);
            var wrapped = WrapPcmAsWav(pcm16k, 16_000, 16, 1);
            File.WriteAllBytes(pcmPath, wrapped);
            return dir;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[TestTools-Bypass] debug WAV save failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcm.Length;
        var fileSize = 36 + dataSize;
        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(fileSize); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)channels);
        bw.Write(sampleRate); bw.Write(byteRate); bw.Write((short)blockAlign); bw.Write((short)bitsPerSample);
        bw.Write("data"u8); bw.Write(dataSize); bw.Write(pcm);
        return ms.ToArray();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void SetStatus(string text, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string, bool>(SetStatus), DispatcherPriority.Normal, text, isError);
            return;
        }
        if (txtVoiceStatus is null) return;
        txtVoiceStatus.Text = text;
        txtVoiceStatus.Foreground = isError
            ? Brushes.OrangeRed
            : (Brush)FindResource("TextDim");
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _replayPlayer?.Dispose(); } catch { }
        _replayPlayer = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// One row in the history ListBox. Mutable result/tail/brush so the
    /// row can be inserted at "pending" state and updated when the work
    /// completes — gives the user immediate feedback that their input
    /// was registered while STT runs in the background.
    /// </summary>
    public sealed class VirtualVoiceHistoryItem : INotifyPropertyChanged
    {
        public string Time { get; }
        public string Input { get; }

        private string _result = "(running…)";
        public string Result
        {
            get => _result;
            private set { _result = value; Notify(nameof(Result)); }
        }

        private string _tail = "";
        public string Tail
        {
            get => _tail;
            private set { _tail = value; Notify(nameof(Tail)); }
        }

        private Brush _resultBrush = PendingBrush;
        public Brush ResultBrush
        {
            get => _resultBrush;
            private set { _resultBrush = value; Notify(nameof(ResultBrush)); }
        }

        // Stored TTS audio for the per-row ▶ replay button. Null until
        // synthesis succeeds; CanPlay drives Button.IsEnabled binding.
        private byte[]? _wavBytes;
        private string _wavFormat = "wav";
        public bool CanPlay => _wavBytes is not null && _wavBytes.Length > 0;

        public VirtualVoiceHistoryItem(string input)
        {
            Time = DateTime.Now.ToString("HH:mm:ss");
            Input = input;
        }

        public void MarkSuccess(string result, string tail)
        {
            Result = string.IsNullOrEmpty(result) ? "(empty)" : result;
            Tail = tail;
            ResultBrush = SuccessBrush;
        }

        public void MarkFailure(string error)
        {
            Result = "✗ " + error;
            Tail = "";
            ResultBrush = ErrorBrush;
        }

        public void SetAudio(byte[] wav, string format)
        {
            _wavBytes = wav;
            _wavFormat = string.IsNullOrEmpty(format) ? "wav" : format;
            Notify(nameof(CanPlay));
        }

        public (byte[]? Wav, string Format) GetAudio() => (_wavBytes, _wavFormat);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
