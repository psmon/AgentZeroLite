using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>
/// What a renderer — the REPL, the window, `run`, the pipe server — needs from
/// a conversation. <see cref="ChatSession"/> is the conversation itself;
/// <see cref="Actors.AgentGateway"/> is the same surface over the Bot / Loop
/// actor pair, which is what the commands use. Tests drive the session
/// directly; production goes through the actors.
///
/// Events are raised in order, one at a time. The two delegates are the
/// pause-for-a-person: a command the gate would not run unasked, a design
/// that hinges on a choice. Null means "refuse" and "take the recommendation".
/// </summary>
public interface IAgentSession : IDisposable
{
    event Action<string>? ActivityStarted;
    event Action<AgentStep>? StepCompleted;
    event Action<string>? AnswerDelta;
    event Action<SmartNote>? Decided;
    event Action<string>? Noted;
    event Action<string>? TitleChanged;
    event Action<IReadOnlyList<string>>? DesignMade;
    event Action<IReadOnlyList<Distilled>>? Learned;

    Func<ApprovalRequest, CancellationToken, Task<bool>>? Approver { get; set; }
    Func<ChoiceRequest, CancellationToken, Task<string>>? Chooser { get; set; }

    string Root { get; }
    string ProviderName { get; }
    string Model { get; }
    string? ReasoningModel { get; }
    string ToolScope { get; }
    string Shell { get; }
    string? LogPath { get; }
    bool SmartAvailable { get; }
    bool Smart { get; }
    string? Title { get; }

    /// <summary>The workspace the session belongs to — for its memory size in a banner.</summary>
    WorkspaceStore Workspace { get; }

    /// <summary>One turn. Null only for an empty line. Throws OperationCanceledException when the token cancels it.</summary>
    Task<AgentRun?> SubmitAsync(string line, CancellationToken ct);

    /// <summary>True while the running turn is held between steps.</summary>
    bool Paused { get; }

    /// <summary>Holds the running turn at its next step. Nothing happens when no turn runs.</summary>
    void Pause();

    /// <summary>What the person typed during the pause: judged as resume / stop / refine, and applied.</summary>
    Task<PauseOutcome> ResumeAsync(string line, CancellationToken ct);

    SessionStats Stats();
    bool TryToggleSmart(out string message);
    void Reset();
    void NewSession();
    IReadOnlyList<SessionSummary> ListSessions();
    IReadOnlyList<SessionEntry> Resume(string path);
}
