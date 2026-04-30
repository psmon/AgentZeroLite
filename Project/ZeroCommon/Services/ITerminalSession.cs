using System.Threading;
using System.Threading.Channels;

namespace Agent.Common.Services;

/// <summary>
/// Represents a terminal output frame received from the PTY process.
/// </summary>
public readonly record struct TerminalOutputFrame(string Text, DateTimeOffset Timestamp);

/// <summary>
/// Abstraction over a terminal session (ConPTY or other).
/// Separates the "session/stream" concern from the "UI renderer" concern.
/// Bot, approval watcher, skill sync depend on this — not on the UI control.
/// </summary>
public interface ITerminalSession
{
    string SessionId { get; }

    /// <summary>Short GUID (8 hex chars) uniquely identifying this session instance,
    /// regardless of the human-readable SessionId label. Used for internal debug logs
    /// to disambiguate tabs that share the same label (e.g., two "group/Claude" tabs).</summary>
    string InternalId { get; }

    /// <summary>Whether the underlying PTY process is running.</summary>
    bool IsRunning { get; }

    /// <summary>Write text to the terminal stdin.</summary>
    void Write(ReadOnlySpan<char> text);

    /// <summary>Write text then send Enter (\r) after a brief delay so TUIs that
    /// distinguish pasted text from key events (e.g. Codex) treat it as submit.</summary>
    void WriteAndSubmit(string text);

    /// <summary>Write text without Enter, then after a 200ms delay submit
    /// Enter via <see cref="SendControl"/>. Default ChatMode path: some TUIs
    /// (e.g. Codex) eat the trailing \r when it lands too close to the text,
    /// even with WriteAndSubmit's 50ms gap.</summary>
    void WriteAndEnter(string text);

    /// <summary>Write text to the terminal stdin (async, queued with backpressure).</summary>
    Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default);

    /// <summary>Send a control sequence (Ctrl+C, ESC, etc.).</summary>
    void SendControl(TerminalControl control);

    /// <summary>
    /// Raised when new output arrives from the PTY process.
    /// Subscribers receive incremental output frames — no polling needed.
    /// </summary>
    event Action<TerminalOutputFrame>? OutputReceived;

    /// <summary>Current length of accumulated console output.</summary>
    int OutputLength { get; }

    /// <summary>Read a substring from the accumulated console output log.</summary>
    string ReadOutput(int start, int length);

    /// <summary>Get all visible console text.</summary>
    string GetConsoleText();
}

/// <summary>Well-known terminal control sequences.</summary>
public enum TerminalControl
{
    Interrupt,    // Ctrl+C  (\x03)
    Escape,       // ESC     (\x1b)
    Enter,        // CR      (\r)
    Tab,          // Tab     (\t)
    Backspace,    // BS      (\x7f)
    Space,        // SP      (\x20)
    Delete,       // ESC[3~
    Home,         // ESC[H
    End,          // ESC[F
    PageUp,       // ESC[5~
    PageDown,     // ESC[6~
    DownArrow,    // ESC[B
    UpArrow,      // ESC[A
    LeftArrow,    // ESC[D
    RightArrow,   // ESC[C
    ClearScreen,  // ESC[2J ESC[H
}
