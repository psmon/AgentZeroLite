using System.Runtime.InteropServices;

namespace ZeroWearable.Voice;

/// <summary>
/// The PC's own speakers, for a watch whose answer mode is "PC": the reply is spoken here
/// at the synthesizer's full rate instead of being squeezed into 16 kHz ADPCM and sent over
/// BLE. One clip at a time — a newer one stops the older, which is the same
/// newest-question-wins rule the actor already applies to requests.
///
/// <para>winmm <c>PlaySound</c> with SND_ASYNC: a synchronous call cannot be interrupted
/// from another thread (measured in the Avalonia host: a cancel at 0.3 s still ran 2.0 s),
/// while <c>PlaySound(NULL)</c> stops an asynchronous one at once. The buffer stays pinned
/// for as long as the clip can be playing.</para>
/// </summary>
public sealed class PcSpeaker
{
    private readonly object _gate = new();
    private int _generation;

    /// <summary>Plays <paramref name="wav"/> (a plain PCM WAV) and returns when it ends or is cancelled.</summary>
    public async Task PlayAsync(byte[] wav, TimeSpan length, CancellationToken ct)
    {
        int mine;
        lock (_gate) mine = ++_generation;
        var handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
        try
        {
            if (!PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                throw new InvalidOperationException("PlaySound refused the clip (no audio output device?)");
            await Task.Delay(length + TimeSpan.FromMilliseconds(150), ct);
        }
        finally
        {
            // Only silence the device if no newer clip has started meanwhile: PlaySound(NULL)
            // would cut that one off too.
            lock (_gate)
                if (ct.IsCancellationRequested && mine == _generation) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            // A newer PlaySound has replaced this buffer by now, or the clip has ended.
            handle.Free();
        }
    }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
