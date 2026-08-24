using System.Text;
using Agent.Common.Services;

namespace ZeroCommon.Tests.Remote;

/// <summary>
/// Headless <see cref="ITerminalSession"/> for Remote actor tests. Accumulates an output
/// buffer (so snapshot reads have content), records input calls, and can raise
/// <see cref="OutputReceived"/> on demand to simulate PTY output frames.
/// </summary>
public sealed class FakeTerminalSession : ITerminalSession
{
    private readonly StringBuilder _log = new();

    public string SessionId { get; } = "fake";
    public string InternalId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsRunning { get; set; } = true;

    public List<string> Writes { get; } = new();
    public List<TerminalControl> Controls { get; } = new();
    public List<string> InputAttempts { get; } = new();

    public FakeTerminalSession(string? seed = null)
    {
        if (!string.IsNullOrEmpty(seed)) _log.Append(seed);
    }

    public void Write(ReadOnlySpan<char> text) => Writes.Add(text.ToString());
    public void WriteAndSubmit(string text) => Writes.Add(text);
    public void WriteAndEnter(string text) => Writes.Add(text);
    public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    { Writes.Add(text.ToString()); return Task.CompletedTask; }
    public void SendControl(TerminalControl control) => Controls.Add(control);

    public event Action<TerminalOutputFrame>? OutputReceived;
    public event Action<TerminalHealthState>? HealthChanged;

    public int OutputLength => _log.Length;
    public string ReadOutput(int start, int length) => _log.ToString(start, length);
    public string GetConsoleText() => _log.ToString();

    public void NoteInputAttempt(string source) => InputAttempts.Add(source);
    public TerminalHealthState HealthState { get; set; } = TerminalHealthState.Alive;

    /// <summary>Append to the buffer and fire an output frame, as ConPtyTerminalSession does.</summary>
    public void RaiseOutput(string text)
    {
        _log.Append(text);
        OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.UnixEpoch));
    }

    // Silence "event never raised" analyzer for HealthChanged (unused in these tests).
    internal void TouchHealth() => HealthChanged?.Invoke(HealthState);
}
