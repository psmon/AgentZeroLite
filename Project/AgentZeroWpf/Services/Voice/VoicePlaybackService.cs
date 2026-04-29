using System.Buffers.Binary;
using System.IO;
using Agent.Common;
using NAudio.Wave;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Plays back TTS bytes through the default output device. Self-describing
/// formats (WAV, MP3) decode via NAudio's readers; raw PCM16 is wrapped at
/// OpenAI's documented 24 kHz mono default. Includes the origin's WAV-header
/// patch for OpenAI gpt-4o-audio-preview, which sometimes returns RIFF chunk
/// sizes of <c>0xFFFFFFFF</c> (streaming sentinel) that NAudio refuses.
///
/// Safe to call from any thread; only one playback at a time — a second call
/// disposes the previous output.
/// </summary>
public sealed class VoicePlaybackService : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _source;
    private readonly object _lock = new();

    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;

    public void Play(byte[] audioBytes, string format)
    {
        Stop();
        lock (_lock)
        {
            var headerHex = HexHead(audioBytes, 16);
            AppLogger.Log($"[Voice] Playback attempt | declared format={format} bytes={audioBytes.Length} head_hex={headerHex}");

            var effectiveFormat = DetectFormat(audioBytes, format);
            if (effectiveFormat != format)
                AppLogger.Log($"[Voice] Format override {format} → {effectiveFormat} (header re-detection)");

            try
            {
                var src = CreateSource(audioBytes, effectiveFormat);
                var output = new WaveOutEvent();
                output.Init(src);
                output.PlaybackStopped += (_, _) =>
                {
                    PlaybackStopped?.Invoke();
                    try { output.Dispose(); } catch { }
                    try { src.Dispose(); } catch { }
                };
                output.Play();
                _output = output;
                _source = src;
                PlaybackStarted?.Invoke();
                AppLogger.Log($"[Voice] Playback start OK | format={effectiveFormat} bytes={audioBytes.Length}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Voice] Playback failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _source?.Dispose(); } catch { }
            _output = null;
            _source = null;
        }
    }

    public void Dispose() => Stop();

    private static string HexHead(byte[] b, int n)
    {
        var take = Math.Min(n, b.Length);
        return string.Join(" ", Enumerable.Range(0, take).Select(i => b[i].ToString("X2")));
    }

    internal static string DetectFormat(byte[] b, string declared)
    {
        if (b.Length >= 4 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return "wav";
        if (b.Length >= 3 && b[0] == 0x49 && b[1] == 0x44 && b[2] == 0x33) return "mp3";
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return "mp3";
        if (string.Equals(declared, "wav", StringComparison.OrdinalIgnoreCase)) return "pcm16";
        return declared;
    }

    internal static WaveStream CreateSource(byte[] bytes, string format)
    {
        return format?.ToLowerInvariant() switch
        {
            "mp3" => new Mp3FileReader(new MemoryStream(bytes)),
            "pcm16" => new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(24_000, 16, 1)),
            _ => new WaveFileReader(new MemoryStream(PatchWavHeaderSizes(bytes))),
        };
    }

    /// <summary>
    /// Replace 0xFFFFFFFF (unknown-length streaming sentinels) in the RIFF and
    /// data chunk size fields with actual byte counts. Returns the original
    /// buffer untouched if it isn't a RIFF header.
    /// </summary>
    internal static byte[] PatchWavHeaderSizes(byte[] bytes)
    {
        if (bytes.Length < 44) return bytes;
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return bytes;

        var patched = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(4, 4), (uint)(patched.Length - 8));

        for (var i = 12; i <= patched.Length - 8; i++)
        {
            if (patched[i] == 'd' && patched[i + 1] == 'a' && patched[i + 2] == 't' && patched[i + 3] == 'a')
            {
                var dataStart = i + 8;
                var dataSize = (uint)(patched.Length - dataStart);
                BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(i + 4, 4), dataSize);
                break;
            }
        }
        return patched;
    }
}
