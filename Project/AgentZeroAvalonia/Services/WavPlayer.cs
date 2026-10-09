using System.Diagnostics;
using System.Runtime.InteropServices;
using Agent.Common;
using Agent.Common.Voice;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// Plays one WAV buffer and returns when it ends or is stopped. Avalonia has no audio
/// API and the WPF host's NAudio queue is Windows-only, so this is the smallest thing
/// that works on each OS: winmm <c>PlaySound</c> on Windows, <c>afplay</c> on macOS,
/// <c>paplay</c>/<c>aplay</c> on Linux. It is for the settings page's test buttons —
/// one clip at a time, no queue, no mixing.
///
/// <para>Every buffer is re-encoded as plain 16-bit mono PCM first: OpenAI streams its
/// WAV with 0xFFFFFFFF chunk sizes, which PlaySound refuses.</para>
/// </summary>
public sealed class WavPlayer
{
    private readonly object _gate = new();
    private Process? _child;

    public Task PlayAsync(byte[] wav, CancellationToken ct = default)
    {
        var decoded = WavPcm.Decode(wav);
        var clean = WavPcm.ToWav(decoded);
        return OperatingSystem.IsWindows()
            ? PlayWindowsAsync(clean, TimeSpan.FromSeconds(decoded.DurationSeconds), ct)
            : PlayExternalAsync(clean, ct);
    }

    public void Stop()
    {
        if (OperatingSystem.IsWindows()) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        lock (_gate)
        {
            try { if (_child is { HasExited: false }) _child.Kill(); } catch { /* already gone */ }
        }
    }

    // SND_ASYNC, then wait out the clip: a synchronous PlaySound cannot be interrupted
    // from another thread (measured: a cancel at 0.3 s still ran 2.0 s of a 1.8 s clip),
    // while PlaySound(NULL) stops an asynchronous one at once. The buffer stays pinned
    // until the wait ends, because winmm reads it while it plays.
    private static async Task PlayWindowsAsync(byte[] wav, TimeSpan length, CancellationToken ct)
    {
        var handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
        try
        {
            if (!PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                throw new InvalidOperationException("PlaySound refused the clip (no audio device?)");
            await Task.Delay(length + TimeSpan.FromMilliseconds(150), ct);
        }
        finally
        {
            if (ct.IsCancellationRequested) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            handle.Free();
        }
    }

    private async Task PlayExternalAsync(byte[] wav, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"agentzero-voice-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(file, wav, ct);
        try
        {
            var player = OperatingSystem.IsMacOS() ? "afplay" : FirstOnPath("paplay", "aplay") ?? "aplay";
            var psi = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(file);
            var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {player}");
            lock (_gate) _child = process;
            using var stop = ct.Register(Stop);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        finally
        {
            lock (_gate) _child = null;
            try { File.Delete(file); } catch (Exception ex) { AppLogger.Log($"[Voice] temp wav not deleted: {ex.Message}"); }
        }
    }

    private static string? FirstOnPath(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return names.FirstOrDefault(n => dirs.Any(d => File.Exists(Path.Combine(d, n))));
    }

    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
