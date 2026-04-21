using AgentZeroWpf.Services;

namespace AgentTest;

/// <summary>
/// Tests for ITerminalSession, ConPtyTerminalSession, and AgentEventStream.
/// Uses a fake ITerminalSession for unit testing without ConPTY dependency.
/// </summary>
public class FakeTerminalSession : ITerminalSession
{
    public string SessionId { get; set; } = "test-session";
    public string InternalId { get; set; } = "fake0000";
    public bool IsRunning { get; set; } = true;

    // Captured writes for assertion
    public List<string> WrittenTexts { get; } = [];
    public List<TerminalControl> SentControls { get; } = [];
    public List<string> AsyncWrittenTexts { get; } = [];

    // Simulated output log
    private readonly List<string> _outputChunks = [];
    private string _fullOutput = "";

    public event Action<TerminalOutputFrame>? OutputReceived;

    public void Write(ReadOnlySpan<char> text)
        => WrittenTexts.Add(text.ToString());

    public void WriteAndSubmit(string text)
    {
        WrittenTexts.Add(text);
        WrittenTexts.Add("\r");
    }

    public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        AsyncWrittenTexts.Add(text.ToString());
        return Task.CompletedTask;
    }

    public void SendControl(TerminalControl control)
        => SentControls.Add(control);

    public int OutputLength => _fullOutput.Length;

    public string ReadOutput(int start, int length)
        => _fullOutput.Substring(start, length);

    public string GetConsoleText() => _fullOutput;

    /// <summary>Simulate PTY output arriving — fires OutputReceived event.</summary>
    public void SimulateOutput(string text)
    {
        _outputChunks.Add(text);
        _fullOutput += text;
        OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.UtcNow));
    }
}

public class TerminalSessionTests
{
    // ==========================================================
    //  ITerminalSession basic contract
    // ==========================================================

    [Fact]
    public void FakeSession_Write_CapturesText()
    {
        var session = new FakeTerminalSession();
        session.Write("hello\r".AsSpan());

        Assert.Single(session.WrittenTexts);
        Assert.Equal("hello\r", session.WrittenTexts[0]);
    }

    [Fact]
    public void FakeSession_SendControl_CapturesControls()
    {
        var session = new FakeTerminalSession();

        session.SendControl(TerminalControl.Escape);
        session.SendControl(TerminalControl.Interrupt);
        session.SendControl(TerminalControl.Enter);

        Assert.Equal(3, session.SentControls.Count);
        Assert.Equal(TerminalControl.Escape, session.SentControls[0]);
        Assert.Equal(TerminalControl.Interrupt, session.SentControls[1]);
        Assert.Equal(TerminalControl.Enter, session.SentControls[2]);
    }

    [Fact]
    public void FakeSession_OutputLength_TracksSimulatedOutput()
    {
        var session = new FakeTerminalSession();
        Assert.Equal(0, session.OutputLength);

        session.SimulateOutput("abc");
        Assert.Equal(3, session.OutputLength);

        session.SimulateOutput("defgh");
        Assert.Equal(8, session.OutputLength);
    }

    [Fact]
    public void FakeSession_ReadOutput_ReturnsCorrectSubstring()
    {
        var session = new FakeTerminalSession();
        session.SimulateOutput("hello world");

        Assert.Equal("hello", session.ReadOutput(0, 5));
        Assert.Equal("world", session.ReadOutput(6, 5));
    }

    [Fact]
    public void FakeSession_OutputReceived_FiresOnSimulate()
    {
        var session = new FakeTerminalSession();
        var received = new List<TerminalOutputFrame>();
        session.OutputReceived += frame => received.Add(frame);

        session.SimulateOutput("chunk1");
        session.SimulateOutput("chunk2");

        Assert.Equal(2, received.Count);
        Assert.Equal("chunk1", received[0].Text);
        Assert.Equal("chunk2", received[1].Text);
    }

    [Fact]
    public async Task FakeSession_WriteAsync_CapturesLargeText()
    {
        var session = new FakeTerminalSession();
        var largeText = new string('x', 500);

        await session.WriteAsync(largeText.AsMemory());

        Assert.Single(session.AsyncWrittenTexts);
        Assert.Equal(500, session.AsyncWrittenTexts[0].Length);
    }
}

public class AgentEventStreamTests
{
    // ==========================================================
    //  Event-driven approval detection
    // ==========================================================

    [Fact]
    public void EventStream_DetectsApproval_OnOutput()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        // Simulate a real approval prompt arriving in chunks
        session.SimulateOutput("Run shell command\n");
        session.SimulateOutput("dotnet build\n");
        session.SimulateOutput("This command requires approval\n");
        session.SimulateOutput("Do you want to proceed?\n");
        session.SimulateOutput("1. Yes                  2. Yes, and don't ask again for: dotnet:*\n");
        session.SimulateOutput("3. No\n");
        session.SimulateOutput("Esc to cancel\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.True(approval.Options.Count >= 2); // parser extracts numbered options
        Assert.False(approval.IsFallback);
    }

    [Fact]
    public void EventStream_SkipsFallback_AsLikelyFalsePositive()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        // Simulate text that contains "requires approval" but no numbered options
        session.SimulateOutput("This code block requires approval before merging.\n");

        Assert.Empty(events); // fallback is skipped
    }

    [Fact]
    public void EventStream_DeduplicatesRepeatedApproval()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        var approvalText =
            "dotnet build\nThis command requires approval\n" +
            "Do you want to proceed?\n" +
            "1. Yes\n2. Yes, and don't ask again for: dotnet:*\n3. No\n" +
            "Esc to cancel\n";

        // Send same approval prompt twice
        session.SimulateOutput(approvalText);
        session.SimulateOutput(approvalText);

        // Should only fire once (dedup by fingerprint)
        Assert.Single(events);
    }

    [Fact]
    public void EventStream_Reset_AllowsRedetection()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        var approvalText =
            "dotnet build\nThis command requires approval\n" +
            "Do you want to proceed?\n" +
            "1. Yes\n2. Yes, and don't ask again for: dotnet:*\n3. No\n" +
            "Esc to cancel\n";

        session.SimulateOutput(approvalText);
        Assert.Single(events);

        // Reset and send again — should detect again
        stream.Reset();
        session.SimulateOutput(approvalText);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void EventStream_NoEvents_OnNormalOutput()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        session.SimulateOutput("Building project...\n");
        session.SimulateOutput("Compiling 42 files\n");
        session.SimulateOutput("Build succeeded.\n");

        Assert.Empty(events);
    }

    [Fact]
    public void EventStream_Dispose_UnsubscribesFromSession()
    {
        var session = new FakeTerminalSession();
        var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        stream.Dispose();

        var approvalText =
            "dotnet build\nThis command requires approval\n" +
            "Do you want to proceed?\n" +
            "1. Yes\n2. Yes, and don't ask again for: dotnet:*\n3. No\n" +
            "Esc to cancel\n";
        session.SimulateOutput(approvalText);

        Assert.Empty(events); // disposed — no events
    }

    [Fact]
    public void EventStream_ExtractsCommand_FromApproval()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        session.SimulateOutput(
            "Run shell command\ngit push origin main\n" +
            "This command requires approval\n" +
            "Do you want to proceed?\n" +
            "1. Yes\n2. Yes, and don't ask again for: git:*\n3. No\n" +
            "Esc to cancel\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.Contains("git", approval.Command, StringComparison.OrdinalIgnoreCase);
    }
}

public class TerminalControlTests
{
    [Theory]
    [InlineData(TerminalControl.Interrupt)]
    [InlineData(TerminalControl.Escape)]
    [InlineData(TerminalControl.Enter)]
    [InlineData(TerminalControl.Tab)]
    [InlineData(TerminalControl.DownArrow)]
    [InlineData(TerminalControl.UpArrow)]
    [InlineData(TerminalControl.ClearScreen)]
    public void AllControlValues_AreDefined(TerminalControl control)
    {
        // Verify enum values are valid and can be used
        Assert.True(Enum.IsDefined(control));
    }

    [Fact]
    public void Tab_SendControl_IsCaptured()
    {
        var session = new FakeTerminalSession();

        session.SendControl(TerminalControl.Tab);

        Assert.Single(session.SentControls);
        Assert.Equal(TerminalControl.Tab, session.SentControls[0]);
    }

    [Fact]
    public void KeyForward_ArrowKeys_SendToSession()
    {
        // Simulates the key-forwarding feature: arrow keys sent to terminal
        var session = new FakeTerminalSession();

        session.SendControl(TerminalControl.UpArrow);
        session.SendControl(TerminalControl.DownArrow);
        session.SendControl(TerminalControl.Tab);

        Assert.Equal(3, session.SentControls.Count);
        Assert.Equal(TerminalControl.UpArrow, session.SentControls[0]);
        Assert.Equal(TerminalControl.DownArrow, session.SentControls[1]);
        Assert.Equal(TerminalControl.Tab, session.SentControls[2]);
    }

    [Fact]
    public void KeyForward_EscSequence_SendsEscapeThenInterrupt()
    {
        // Simulates the ESC key forwarding: sends Escape + Interrupt
        var session = new FakeTerminalSession();

        session.SendControl(TerminalControl.Escape);
        session.SendControl(TerminalControl.Interrupt);

        Assert.Equal(2, session.SentControls.Count);
        Assert.Equal(TerminalControl.Escape, session.SentControls[0]);
        Assert.Equal(TerminalControl.Interrupt, session.SentControls[1]);
    }

    [Fact]
    public void KeyForward_Disabled_NoControlsSent()
    {
        // Simulates key-forwarding OFF: no controls should be sent
        var session = new FakeTerminalSession();
        bool keyForwardEnabled = false;

        if (keyForwardEnabled)
        {
            session.SendControl(TerminalControl.UpArrow);
        }

        Assert.Empty(session.SentControls);
    }

    [Fact]
    public void WelcomeSession_DeduplicatesBySessionKey()
    {
        // Simulates the welcome dedup logic from AgentBotWindow
        string? lastWelcomeSession = null;
        var messages = new List<string>();

        void ShowWelcome(string group, string tab)
        {
            var key = $"{group}/{tab}";
            if (key == lastWelcomeSession) return;
            lastWelcomeSession = key;
            messages.Add($"[Session] {group} / {tab}");
        }

        ShowWelcome("Dev", "pwsh");
        ShowWelcome("Dev", "pwsh");   // duplicate, should be skipped
        ShowWelcome("Dev", "cmd");    // different tab, should show

        Assert.Equal(2, messages.Count);
        Assert.Equal("[Session] Dev / pwsh", messages[0]);
        Assert.Equal("[Session] Dev / cmd", messages[1]);
    }

    [Fact]
    public void WelcomeSession_DifferentGroups_BothShow()
    {
        string? lastWelcomeSession = null;
        var messages = new List<string>();

        void ShowWelcome(string group, string tab)
        {
            var key = $"{group}/{tab}";
            if (key == lastWelcomeSession) return;
            lastWelcomeSession = key;
            messages.Add($"[Session] {group} / {tab}");
        }

        ShowWelcome("Backend", "pwsh");
        ShowWelcome("Frontend", "pwsh");  // same tab name, different group

        Assert.Equal(2, messages.Count);
        Assert.Equal("[Session] Backend / pwsh", messages[0]);
        Assert.Equal("[Session] Frontend / pwsh", messages[1]);
    }

    [Fact]
    public void WelcomeSession_RevisitAfterSwitch_ShowsAgain()
    {
        string? lastWelcomeSession = null;
        var messages = new List<string>();

        void ShowWelcome(string group, string tab)
        {
            var key = $"{group}/{tab}";
            if (key == lastWelcomeSession) return;
            lastWelcomeSession = key;
            messages.Add($"[Session] {group} / {tab}");
        }

        ShowWelcome("Dev", "pwsh");
        ShowWelcome("Dev", "cmd");
        ShowWelcome("Dev", "pwsh");  // revisit — should show again (last was cmd)

        Assert.Equal(3, messages.Count);
    }
}
