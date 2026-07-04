namespace Agent.Common.Vision;

/// <summary>
/// Persisted options for the on-device vision tab (M0028). Deliberately small —
/// Florence-2 object detection has no per-box confidence to threshold, so the
/// tunables are just the model location, the active task, and the YouTube test
/// harness settings. Mirrors <see cref="Agent.Common.Music.MusicSettings"/>.
/// </summary>
public sealed class VisionSettings
{
    /// <summary>Override for the Florence-2 model cache dir. Empty = convention default.</summary>
    public string ModelDir { get; set; } = "";

    /// <summary>
    /// Florence-2 task name. Fixed to object detection ("OD") for v1 — kept as a
    /// string so a future task dropdown (caption/OCR) doesn't need a schema bump.
    /// </summary>
    public string Task { get; set; } = "OD";

    /// <summary>How often the YouTube test captures + interprets a frame.</summary>
    public int CaptureIntervalMs { get; set; } = 1500;

    /// <summary>Last YouTube URL used in the test box, restored on reopen.</summary>
    public string LastYouTubeUrl { get; set; } = "";
}
