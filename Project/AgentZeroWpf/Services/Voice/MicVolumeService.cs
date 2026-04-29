using Agent.Common;
using NAudio.CoreAudioApi;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Read / write the default capture endpoint's master volume via WASAPI.
/// Used by the AskBot toolbar slider so the user can pull the system mic
/// level down (e.g. for a clean virtual-voice test session) without
/// leaving the app.
///
/// <para><b>System-wide effect.</b> This *is* the Windows recording volume
/// — other apps (Zoom, Teams, OBS) see the change too. That's the same
/// behaviour as moving the slider in Sound Settings, so users expect it.
/// </para>
///
/// <para>Best-effort: if no input device is available, all calls return
/// null / false silently. The mic toolbar disables the slider in that
/// case rather than throwing.</para>
/// </summary>
internal static class MicVolumeService
{
    /// <summary>
    /// Current master-volume scalar [0..1] of the default capture device,
    /// or null if no device is available / a COM error happens.
    /// </summary>
    public static float? GetVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device?.AudioEndpointVolume?.MasterVolumeLevelScalar;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[MicVolume] GetVolume failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Set the default capture device's master-volume scalar [0..1].
    /// Returns true on success, false if no device or any failure.
    /// </summary>
    public static bool SetVolume(float scalar)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (device?.AudioEndpointVolume is null) return false;
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[MicVolume] SetVolume({scalar:F2}) failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when a default capture endpoint exists.</summary>
    public static bool IsAvailable() => GetVolume().HasValue;
}
