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
    private VirtualVoiceInjector? _injector;
    private readonly ObservableCollection<VirtualVoiceHistoryItem> _history = new();

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

    private void OnBypassToggled(object sender, RoutedEventArgs e) => UpdateModeLine();

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

    private async Task RunSpeakAsync()
    {
        var text = txtVoiceInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Type something first.", isError: false);
            return;
        }

        // Clear the textbox so the user can type the next test line while STT
        // is still running. The submitted text lives on in the history list.
        txtVoiceInput.Clear();

        // Snapshot models for this turn so the history row reflects what was
        // *actually* used, not what the user might switch to mid-turn.
        var v = VoiceSettingsStore.Load();
        var bypass = chkBypassAcousticLoop?.IsChecked == true;

        // Pre-create the history item with a placeholder result; the worker
        // updates it via INotifyPropertyChanged when the result lands.
        var item = new VirtualVoiceHistoryItem(text);
        _history.Insert(0, item);
        lstHistory.ScrollIntoView(item);

        if (bypass)
            await RunBypassAsync(item, v);
        else
            await RunAcousticLoopAsync(item, v);
    }

    /// <summary>
    /// Bypass mode (default). Synthesise → decode/resample to PCM 16k mono →
    /// hand straight to <see cref="ISpeechToText.TranscribeAsync"/>. Pure
    /// TTS↔STT round-trip, no speaker, no microphone, no acoustic loop.
    ///
    /// <para>Heavy work (SAPI Speak, NAudio resample, Whisper inference) is
    /// pushed off the UI thread via <c>Task.Run</c> — both
    /// <c>SpeechSynthesizer.Speak</c> and Whisper's CPU inference are
    /// synchronous-ish and would freeze the dispatcher otherwise. Per-stage
    /// Stopwatch timings are logged so the next slowdown can be diagnosed
    /// without re-instrumenting.</para>
    /// </summary>
    private async Task RunBypassAsync(VirtualVoiceHistoryItem item, VoiceSettings v)
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
                    var sw = Stopwatch.StartNew();
                    var wav = await tts.SynthesizeAsync(item.Input, v.TtsVoice);
                    sw.Stop();
                    var synthMs = sw.ElapsedMilliseconds;
                    if (wav is null || wav.Length == 0)
                        throw new InvalidOperationException("TTS returned empty audio.");
                    AppLogger.Log($"[TestTools-Bypass] [1/3] synth | {synthMs} ms · {wav.Length} bytes · provider={v.TtsProvider}");

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
                    var tail = $"bypass · {ProviderTag(v)} · synth {synthMs}ms · decode {decodeMs}ms · STT {sttMs}ms ({rtFactor:F1}x rt) · total {totalSw.ElapsedMilliseconds}ms";
                    AppLogger.Log($"[TestTools-Bypass] DONE | {tail}");

                    if (string.IsNullOrWhiteSpace(transcript))
                        SucceedItem(item, "(empty transcript)", tail);
                    else
                        SucceedItem(item, transcript, tail);
                    SetStatus($"✓ done · {totalSw.ElapsedMilliseconds}ms", isError: false);
                }
                finally
                {
                    (tts as IDisposable)?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[TestTools-Bypass] failed", ex);
            FailItem(item, $"{ex.GetType().Name}: {ex.Message}");
            SetStatus($"✗ {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Acoustic-loop mode. Plays the synthesised audio through the default
    /// speaker; AskBot's mic re-captures it. Only works when Windows mic
    /// enhancements are off and the speaker output reaches the mic.
    /// </summary>
    private async Task RunAcousticLoopAsync(VirtualVoiceHistoryItem item, VoiceSettings v)
    {
        if (_injector is null)
        {
            _injector = new VirtualVoiceInjector();
            _injector.Started += () => SetStatus("▶ playing through speaker…", isError: false);
            _injector.Stopped += () => SetStatus("done — if mic is ON, AskBot should have heard it.", isError: false);
            _injector.Errored += ex => SetStatus($"✗ {ex.Message}", isError: true);
        }

        SetStatus("synthesising…", isError: false);
        try
        {
            await _injector.SpeakAsync(item.Input);
            // We don't get the STT result back through this path (it goes to
            // AskBot via the mic), so just record that playback fired.
            SucceedItem(item, "(played through speaker — see AskBot for STT)",
                $"acoustic · {ProviderTag(v)} · result delivered to AskBot, not shown here");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[TestTools] SpeakAsync threw: {ex.GetType().Name}: {ex.Message}");
            FailItem(item, $"{ex.GetType().Name}: {ex.Message}");
        }
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
        var bypass = chkBypassAcousticLoop?.IsChecked == true;
        txtActiveMode.Text = bypass
            ? "mode: bypass — TTS → resample → STT (no audio I/O)"
            : "mode: acoustic loop — TTS → speaker → AskBot mic → STT (requires OS audio loopback)";
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
        try { _injector?.Dispose(); } catch { }
        _injector = null;
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
