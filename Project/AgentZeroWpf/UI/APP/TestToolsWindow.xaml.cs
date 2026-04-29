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
/// Lifecycle: owned by AgentBotWindow. <c>AgentBotWindow.Voice</c> creates
/// a fresh instance per toggle click and disposes the
/// <see cref="VirtualVoiceInjector"/> in <see cref="OnClosed"/>. There's no
/// caching — opening twice closes the previous; minor cost for the cleaner
/// state model.
/// </summary>
public partial class TestToolsWindow : Window
{
    private VirtualVoiceInjector? _injector;

    public TestToolsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => txtVoiceInput.Focus();
    }

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

        var bypass = chkBypassAcousticLoop?.IsChecked == true;
        if (bypass)
        {
            await RunBypassAsync(text);
        }
        else
        {
            await RunAcousticLoopAsync(text);
        }
    }

    /// <summary>
    /// Bypass mode (default). Synthesise → decode/resample to PCM 16k mono →
    /// hand straight to <see cref="ISpeechToText.TranscribeAsync"/>. Pure
    /// TTS↔STT round-trip, no speaker, no microphone, no acoustic loop —
    /// resilient to OS-level echo cancellation and headphones-vs-room-mic
    /// configuration drift. Result is shown inline in this window only;
    /// AskBot is intentionally NOT notified (this is a validation tool,
    /// not an input path).
    /// </summary>
    private async Task RunBypassAsync(string text)
    {
        var v = VoiceSettingsStore.Load();
        if (string.Equals(v.TtsProvider, TtsProviderNames.Off, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("✗ TTS provider is Off — Settings → Voice → pick one.", isError: true);
            return;
        }

        ITextToSpeech? tts = null;
        ISpeechToText? stt = null;
        try
        {
            tts = VoiceRuntimeFactory.BuildTts(v)
                ?? throw new InvalidOperationException($"TTS '{v.TtsProvider}' could not be constructed.");
            stt = VoiceRuntimeFactory.BuildStt(v)
                ?? throw new InvalidOperationException($"STT '{v.SttProvider}' could not be constructed.");

            SetStatus($"synthesising via {v.TtsProvider}…", isError: false);
            var wav = await tts.SynthesizeAsync(text, v.TtsVoice);
            if (wav is null || wav.Length == 0)
                throw new InvalidOperationException("TTS returned empty audio.");
            AppLogger.Log($"[TestTools-Bypass] synth OK | bytes={wav.Length} provider={v.TtsProvider}");

            SetStatus("decoding WAV → PCM 16k mono…", isError: false);
            var pcm = WavToPcm.To16kMono(wav);
            if (pcm.Length == 0)
                throw new InvalidOperationException("WAV decode produced empty PCM.");
            AppLogger.Log($"[TestTools-Bypass] decoded | pcm_bytes={pcm.Length} (~{pcm.Length / 32_000.0:F2}s)");

            SetStatus($"transcribing via {v.SttProvider}…", isError: false);
            var ready = await stt.EnsureReadyAsync();
            if (!ready) throw new InvalidOperationException("STT failed to ready.");
            var transcript = (await stt.TranscribeAsync(pcm, v.SttLanguage)) ?? string.Empty;
            AppLogger.Log($"[TestTools-Bypass] STT result | chars={transcript.Length} text=\"{Trunc(transcript, 80)}\"");

            if (string.IsNullOrWhiteSpace(transcript))
                SetStatus("✓ done · transcript empty (silent input or VAD-trimmed)", isError: false);
            else
                SetStatus($"✓ {v.SttProvider}: \"{transcript}\"", isError: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[TestTools-Bypass] failed", ex);
            SetStatus($"✗ {ex.GetType().Name}: {ex.Message}", isError: true);
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Acoustic-loop mode. Plays the synthesised audio through the default
    /// speaker; AskBot's mic re-captures it through the OS audio loopback.
    /// Useful for validating the full input pipeline end-to-end (VAD,
    /// segmenter, STT, AskBot routing) but only works when (a) Windows
    /// echo-cancellation / noise-suppression on the mic input is OFF and
    /// (b) speaker output and mic are physically coupled.
    /// </summary>
    private async Task RunAcousticLoopAsync(string text)
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
            await _injector.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[TestTools] SpeakAsync threw: {ex.GetType().Name}: {ex.Message}");
        }
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
}
