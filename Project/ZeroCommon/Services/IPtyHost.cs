namespace Agent.Common.Services;

/// <summary>
/// A pseudo-terminal with a child process attached — the thing an
/// <see cref="XtermTerminalSession"/> writes to and reads from (M0033). Implementations
/// live in the hosts: the Windows one is the ConPTY host the WPF app already runs on
/// (kernel32 only), the macOS one wraps Porta.Pty. ZeroCommon owns only this seam so
/// the session, the health tracker and their tests stay free of natives.
/// </summary>
public interface IPtyHost : IDisposable
{
    /// <summary>Raw VT output from the child, in arrival order, already decoded to UTF-16.</summary>
    event Action<string>? Output;

    /// <summary>The child ended (or the pipe closed).</summary>
    event Action? Exited;

    bool IsRunning { get; }

    /// <summary>Write keystrokes / VT input to the child. Throws when the host is down.</summary>
    void Write(ReadOnlySpan<char> text);

    /// <summary>Tell the child its screen size changed.</summary>
    void Resize(int cols, int rows);

    /// <summary>One line for the log: what was launched and how it is doing.</summary>
    string Diagnostics { get; }
}
