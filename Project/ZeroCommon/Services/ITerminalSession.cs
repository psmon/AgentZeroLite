using System.Threading;
using System.Threading.Channels;

namespace Agent.Common.Services;

/// <summary>
/// Represents a terminal output frame received from the PTY process.
/// </summary>
public readonly record struct TerminalOutputFrame(string Text, DateTimeOffset Timestamp);

/// <summary>
/// Channel health derived from input-vs-output flow. Slow-loading TUIs
/// (claude CLI, tailscale ssh handshake) intermittently leave the pipe in
/// a state where neither stdin writes nor Ctrl+C reach the foreground
/// child — output frozen, input silent. This enum surfaces that.
/// </summary>
public enum TerminalHealthState
{
    /// Default — input attempts produce output (echo, redraw, anything).
    Alive,
    /// Recent input attempts produced no echo. Could be transient (slow
    /// load) or the start of a wedge.
    Stale,
    /// Sustained silent input — channel is wedged. UI should offer
    /// "Restart this terminal".
    Dead,
}

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

    /// <summary>
    /// Notify the session that an input attempt was made — by AgentBot's
    /// Write/SendControl, or by direct keyboard input forwarded through
    /// TermControl. The session schedules a deferred output-growth check;
    /// if the input produced no echo, an INPUT-NO-ECHO log is emitted and
    /// the consecutive-no-echo counter advances, eventually transitioning
    /// <see cref="HealthState"/> to Stale and then Dead.
    /// <para><c>source</c> is a short tag for the log line, e.g.
    /// "write bytes=2", "control=Enter", or "keyboard:H".</para>
    /// </summary>
    void NoteInputAttempt(string source);

    /// <summary>Current input-channel health.</summary>
    TerminalHealthState HealthState { get; }

    /// <summary>Raised on health transitions. Subscribers receive the new state.</summary>
    event Action<TerminalHealthState>? HealthChanged;
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
