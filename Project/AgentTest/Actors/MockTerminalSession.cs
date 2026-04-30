using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentZeroWpf.Services;

namespace AgentTest.Actors;

/// <summary>
/// Test-only ITerminalSession — records all calls for verification.
/// </summary>
public sealed class MockTerminalSession : ITerminalSession
{
    public string SessionId { get; }
    public string InternalId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsRunning { get; set; } = true;
    public int OutputLength => 0;

    // Recorded calls
    public List<string> WriteAndSubmitCalls { get; } = new();
    public List<string> WriteAndEnterCalls { get; } = new();
    public List<TerminalControl> SendControlCalls { get; } = new();
    public List<string> WriteCalls { get; } = new();

    public MockTerminalSession(string sessionId = "mock-session")
    {
        SessionId = sessionId;
    }

    public void Write(ReadOnlySpan<char> text) => WriteCalls.Add(text.ToString());

    public void WriteAndSubmit(string text) => WriteAndSubmitCalls.Add(text);

    public void WriteAndEnter(string text) => WriteAndEnterCalls.Add(text);

    public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        WriteCalls.Add(text.ToString());
        return Task.CompletedTask;
    }

    public void SendControl(TerminalControl control) => SendControlCalls.Add(control);

#pragma warning disable CS0067 // Event never used — fine for mock
    public event Action<TerminalOutputFrame>? OutputReceived;
#pragma warning restore CS0067

    public string ReadOutput(int start, int length) => "";
    public string GetConsoleText() => "";

    /// <summary>Simulate terminal output for testing OutputReceived event.</summary>
    public void SimulateOutput(string text)
    {
        OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.Now));
    }
}
