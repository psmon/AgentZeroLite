using System.Text;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// A terminal the bot tests can drive (M0041). The router tests' <c>FakeSession</c> has a
/// no-op <c>OutputReceived</c>, so it cannot feed an <see cref="Agent.Common.Services.AgentEventStream"/>;
/// this one raises real frames and records what was written.
/// </summary>
public sealed class FakeBotSession : ITerminalSession
{
    private readonly StringBuilder _log = new();
    private readonly List<Task> _pending = new();

    public string SessionId => "fake";
    public string InternalId => "fake0001";
    public bool IsRunning { get; set; } = true;

    public List<string> Writes { get; } = new();
    public List<TerminalControl> Controls { get; } = new();
    public List<string> InputAttempts { get; } = new();

    public void Write(ReadOnlySpan<char> text) => Writes.Add(text.ToString());
    public void WriteAndSubmit(string text) => Writes.Add(text);
    public void WriteAndEnter(string text) => Writes.Add(text);

    public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        Writes.Add(text.ToString());
        return Task.CompletedTask;
    }

    public void SendControl(TerminalControl control) => Controls.Add(control);

    public event Action<TerminalOutputFrame>? OutputReceived;
    public event Action<TerminalHealthState>? HealthChanged;

    public int OutputLength => _log.Length;
    public string ReadOutput(int start, int length) => _log.ToString(start, length);
    public string GetConsoleText() => _log.ToString();

    public void NoteInputAttempt(string source) => InputAttempts.Add(source);
    public TerminalHealthState HealthState { get; set; } = TerminalHealthState.Alive;

    /// <summary>Appends to the buffer and raises a frame, as a real PTY does.</summary>
    public void RaiseOutput(string text)
    {
        _log.Append(text);
        OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.UtcNow));
    }

    /// <summary>Lets fire-and-forget sends started by the view model settle.</summary>
    public async Task Drain()
    {
        await Task.Yield();
        await Task.WhenAll(_pending);
    }

    internal void TouchHealth() => HealthChanged?.Invoke(HealthState);
}
