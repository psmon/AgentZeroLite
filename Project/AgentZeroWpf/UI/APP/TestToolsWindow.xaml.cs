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
    ///
    /// <para>Heavy work (SAPI Speak, NAudio resample, Whisper inference) is
    /// pushed off the UI thread via Task.Run — both <c>SpeechSynthesizer.Speak</c>
    /// and Whisper's CPU inference are synchronous-ish and would freeze the
    /// dispatcher otherwise. Per-stage Stopwatch timings are logged so the
    /// next slowdown can be diagnosed without re-instrumenting.</para>
    /// </summary>
    private async Task RunBypassAsync(string text)
    {
        var v = VoiceSettingsStore.Load();
        if (string.Equals(v.TtsProvider, TtsProviderNames.Off, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("✗ TTS provider is Off — Settings → Voice → pick one.", isError: true);
            return;
        }

        try
        {
            // Build factories on the UI thread (cheap), then offload everything
            // synchronous to a worker. SetStatus marshals back automatically.
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
                    var wav = await tts.SynthesizeAsync(text, v.TtsVoice);
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

                    // Save the synthesised audio to disk for inspection. The user
                    // can listen to it directly to verify TTS quality before
                    // blaming STT for the recognition failure.
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
                    var timing = $"synth {synthMs}ms · decode {decodeMs}ms · STT {sttMs}ms ({rtFactor:F1}x rt) · total {totalSw.ElapsedMilliseconds}ms";
                    AppLogger.Log($"[TestTools-Bypass] DONE | {timing}");

                    if (string.IsNullOrWhiteSpace(transcript))
                        SetStatus($"✓ empty transcript · {timing}", isError: false);
                    else
                        SetStatus($"✓ \"{transcript}\"  ·  {timing}", isError: false);
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
            SetStatus($"✗ {ex.GetType().Name}: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Persist the synthesised WAV (as TTS produced it) and the resampled
    /// PCM (as STT actually saw it) into <c>%LOCALAPPDATA%\AgentZeroLite\debug\</c>.
    /// Both are written; comparing them isolates whether a recognition
    /// problem is in the TTS output or in the resample step. Returns the
    /// folder path so callers can log where to look.
    /// </summary>
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

            // Wrap the resampled PCM as a WAV so the user can play it back
            // and hear exactly what STT was fed (post-resample, pre-inference).
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
