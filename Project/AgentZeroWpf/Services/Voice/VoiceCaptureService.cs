using System.IO;
using Agent.Common;
using Agent.Common.Voice.Streams;
using NAudio.Wave;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Real-time microphone capture + energy-based voice activity detection (VAD).
/// Ported from the AgentWin origin. Two event streams fire from the capture
/// thread:
///   - <see cref="AmplitudeChanged"/>     RMS(0~1) every ~50ms for level meter UI
///   - <see cref="SpeakingStateChanged"/> debounced bool (UI mic indicator)
///
/// Uses NAudio's <see cref="WaveInEvent"/> at 16 kHz mono 16-bit — the standard
/// input shape every STT provider in <c>AgentZeroLite</c> expects, so the same
/// PCM buffer is forwarded to Whisper / OpenAI / Webnori-Gemma without further
/// resampling.
///
/// The dual-VAD design (frame-level + utterance-level) is what lets the user
/// pause briefly mid-sentence ("I'm a dev… eloper") without splitting the
/// utterance — the utterance-level state machine waits for ~2 s of continuous
/// silence before declaring the segment complete.
/// </summary>
public sealed class VoiceCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private bool _isCapturing;
    private bool _isSpeaking;
    private int _silenceFrames;

    private bool _inUtterance;
    private int _utteranceSilenceFrames;

    /// <summary>Amplitude threshold (0~1). Default 0.08 (8%).</summary>
    public float VadThreshold { get; set; } = 0.08f;

    /// <summary>UI debounce — short (≈300 ms) so the speaking indicator feels responsive.</summary>
    public int SilenceHangoverFrames { get; set; } = 6;

    /// <summary>Utterance boundary — 40 frames ≈ 2 s at 50 ms per buffer.</summary>
    public int UtteranceHangoverFrames { get; set; } = 40;

    public event Action<float>? AmplitudeChanged;
    public event Action<bool>? SpeakingStateChanged;
    public event Action? UtteranceStarted;
    public event Action? UtteranceEnded;

    /// <summary>
    /// Per-frame PCM + RMS, fired alongside <see cref="AmplitudeChanged"/>.
    /// The new Akka.Streams pipeline (P1+) forwards this directly to
    /// <c>VoiceStreamActor</c> without going through the legacy VAD/buffer
    /// state — those concerns now live inside the segmenter Flow.
    /// Subscribers must not block the capture thread.
    /// </summary>
    public event Action<MicFrame>? FrameAvailable;

    /// <summary>When true, the service accumulates raw PCM into an internal buffer
    /// the caller drains via <see cref="ConsumePcmBuffer"/>. Always-on amplitude
    /// reporting (level meter) is unaffected by this flag.</summary>
    public bool BufferPcm { get; set; }

    /// <summary>
    /// Suspend VAD state machine + PCM buffering while TTS is playing back, so
    /// the speaker output isn't fed back into the next transcription. The
    /// NAudio capture itself keeps running so the level meter still moves.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>How much pre-trigger audio to retain (default 1 s) — prevents
    /// clipping the first consonant when VAD fires.</summary>
    public double PreRollSeconds { get; set; } = 1.0;

    private readonly List<byte> _pcmBuffer = new();
    private readonly Queue<byte[]> _preRollRing = new();
    private long _preRollBytes;

    public bool IsCapturing => _isCapturing;

    public byte[] ConsumePcmBuffer()
    {
        lock (_pcmBuffer)
        {
            var copy = _pcmBuffer.ToArray();
            _pcmBuffer.Clear();
            return copy;
        }
    }

    /// <summary>
    /// Copy the pre-roll ring (the last <see cref="PreRollSeconds"/> of audio)
    /// into the segment buffer. Call this on UtteranceStarted so the segment
    /// includes the ~1 s preceding the VAD trigger.
    /// </summary>
    public void SeedBufferWithPreRoll()
    {
        if (!BufferPcm) return;
        lock (_pcmBuffer)
        {
            foreach (var chunk in _preRollRing)
                _pcmBuffer.AddRange(chunk);
        }
    }

    public static void WritePcmToWav(byte[] pcm, string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        writer.Write(pcm, 0, pcm.Length);
    }

    public static IReadOnlyList<WaveInDevice> ListDevices()
    {
        var list = new List<WaveInDevice>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            list.Add(new WaveInDevice(i, caps.ProductName));
        }
        return list;
    }

    public void Start(int deviceNumber = 0)
    {
        if (_isCapturing) return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16_000, bits: 16, channels: 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) =>
        {
            _isCapturing = false;
            if (e.Exception is not null)
                AppLogger.Log($"[Voice] Recording stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            else
                AppLogger.Log("[Voice] Recording stopped (clean)");
        };

        _isCapturing = true;
        _isSpeaking = false;
        _silenceFrames = 0;
        _inUtterance = false;
        _utteranceSilenceFrames = 0;
        _waveIn.StartRecording();
        AppLogger.Log($"[Voice] Capture start | device={deviceNumber} sr=16000 bits=16 ch=1 preRoll={PreRollSeconds}s uttHangover={UtteranceHangoverFrames} frames");
    }

    public void Stop()
    {
        if (_waveIn is null) return;
        try { _waveIn.StopRecording(); } catch { /* already stopped */ }
        _waveIn.Dispose();
        _waveIn = null;
        _isCapturing = false;
        if (_isSpeaking)
        {
            _isSpeaking = false;
            SpeakingStateChanged?.Invoke(false);
        }
        AppLogger.Log("[Voice] Capture stop");
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        // Skip pre-roll ring + main buffer while muted so the AI's own TTS
        // playback never bleeds into the next transcription segment.
        if (!Muted)
        {
            lock (_preRollRing)
            {
                _preRollRing.Enqueue(chunk);
                _preRollBytes += chunk.Length;
                var maxBytes = (long)(PreRollSeconds * 16_000 * 2);
                while (_preRollBytes > maxBytes && _preRollRing.Count > 0)
                {
                    var old = _preRollRing.Dequeue();
                    _preRollBytes -= old.Length;
                }
            }

            if (BufferPcm)
            {
                lock (_pcmBuffer)
                {
                    _pcmBuffer.AddRange(chunk);
                }
            }
        }

        double sumSquares = 0;
        for (var i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            var norm = sample / 32768.0;
            sumSquares += norm * norm;
        }
        var rms = (float)Math.Sqrt(sumSquares / sampleCount);

        AmplitudeChanged?.Invoke(rms);

        // Stream pipeline subscriber. We deliberately suppress FrameAvailable
        // when Muted so the segmenter Flow sees no input at all — matches the
        // legacy batch path's buffer/VAD freeze. Level meter (AmplitudeChanged
        // above) still updates so the user has visual feedback that capture
        // is active but ignored.
        if (FrameAvailable is not null && !Muted)
        {
            try { FrameAvailable.Invoke(new MicFrame(chunk, rms)); }
            catch (Exception ex) { AppLogger.Log($"[Voice] FrameAvailable handler threw: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Muted path: keep the level meter alive but freeze the VAD state machine
        // so releasing the mute doesn't immediately fire UtteranceStarted on stale state.
        if (Muted)
        {
            if (_isSpeaking)
            {
                _isSpeaking = false;
                SpeakingStateChanged?.Invoke(false);
            }
            _silenceFrames = 0;
            _utteranceSilenceFrames = 0;
            _inUtterance = false;
            return;
        }

        var above = rms >= VadThreshold;

        if (above)
        {
            _silenceFrames = 0;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingStateChanged?.Invoke(true);
            }
        }
        else if (_isSpeaking)
        {
            _silenceFrames++;
            if (_silenceFrames >= SilenceHangoverFrames)
            {
                _isSpeaking = false;
                SpeakingStateChanged?.Invoke(false);
            }
        }

        if (above)
        {
            _utteranceSilenceFrames = 0;
            if (!_inUtterance)
            {
                _inUtterance = true;
                try { UtteranceStarted?.Invoke(); }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Voice] UtteranceStarted handler threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        else if (_inUtterance)
        {
            _utteranceSilenceFrames++;
            if (_utteranceSilenceFrames >= UtteranceHangoverFrames)
            {
                _inUtterance = false;
                try { UtteranceEnded?.Invoke(); }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Voice] UtteranceEnded handler threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}

public sealed record WaveInDevice(int DeviceNumber, string Name)
{
    public override string ToString() => $"[{DeviceNumber}] {Name}";
}
