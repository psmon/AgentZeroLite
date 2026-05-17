namespace Agent.Common.Voice;

/// <summary>
/// Cross-provider snapshot of a model-download operation, surfaced through
/// <see cref="System.IProgress{T}"/>. Intentionally provider-agnostic so the
/// shared <c>ModelDownloadDialog</c> WPF component can host Supertonic,
/// Whisper, LLM GGUF, or any future model fetch without duplicating UI.
/// M0020 follow-up #7 — operator asked for resume / progress / componentized
/// download UI after cancelling a half-finished Supertonic fetch.
/// </summary>
public sealed record ModelDownloadStatus(
    /// <summary>Headline above the progress bar — e.g. "Downloading Supertonic model".</summary>
    string Caption,
    /// <summary>One-line detail under the bar — bytes/files/ETA, whatever the source can report.</summary>
    string Detail,
    /// <summary>0..100 when the source can report a real percent; null = indeterminate (spinner).</summary>
    int? PercentComplete,
    /// <summary>True when no more updates will arrive. <see cref="IsSuccess"/> tells which terminal state.</summary>
    bool IsTerminal,
    /// <summary>Only meaningful when <see cref="IsTerminal"/> is true.</summary>
    bool IsSuccess);
