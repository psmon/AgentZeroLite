using AgentOne.Agent;
using AgentOne.Services;

namespace AgentOne.Actors;

// The message vocabulary of the Bot / Loop pair — the same names AgentZero's
// Bot mode uses (ZeroCommon/Actors/Messages.cs §8, harness/knowledge/_shared/
// agent-architecture.md), so a reader of either project recognises the other.
//
// Two semantics, two records: AgentLoopProgress is a phase tick, AgentLoopResult
// is the end of a run. Neither is ever reused for the other. What agent-one
// adds beyond AgentZero — side notes (decisions, titles, designs, what the
// graph learned) and the pause-for-a-person — get their own records rather
// than being folded into Progress.

/// <summary>Where the loop is. Reported, not the actor's behaviour: the actor is only ever Idle or Running.</summary>
public enum AgentLoopPhase { Idle, Thinking, Generating, Acting, Done, Error }

// ----------------------------------------------------------------- to the bot

/// <summary>One turn: the person's line. The bot refuses a second while one runs.</summary>
public sealed record StartAgentLoop(string UserRequest);

/// <summary>Cancel the running turn. The loop still ends with exactly one AgentLoopResult.</summary>
public sealed record CancelAgentLoop
{
    public static readonly CancelAgentLoop Instance = new();
}

/// <summary>The person answered a pause: "y"/"n" for a command, a pick or free text for a design choice.</summary>
public sealed record ResolvePause(int PauseId, string Answer);

/// <summary>
/// How the bot reaches whoever is rendering: the REPL, the window, `run`, the
/// pipe server. Invoked on the bot's thread, in order; a renderer that needs a
/// UI thread marshals from inside its own callback. Never block on the bot
/// from inside one of these.
/// </summary>
public sealed record SetAgentLoopCallbacks(
    Action<AgentLoopProgress> OnProgress,
    Action<AgentLoopResult> OnResult,
    Action<AgentLoopNotice> OnNotice,
    Action<PersonNeeded> OnPause);

/// <summary>Reply to StartAgentLoop: the turn is running; the result will come through OnResult.</summary>
public sealed record TurnAccepted
{
    public static readonly TurnAccepted Instance = new();
}

/// <summary>Reply to StartAgentLoop when one is already running.</summary>
public sealed record TurnRefused(string Reason);

// ---------------------------------------------- session commands (bot → loop)

/// <summary>A command about the session rather than a turn; the bot forwards it to the loop, which owns the session.</summary>
public interface IAgentSessionCommand;

/// <summary>Forget the conversation; keep the mode, the log and the memory. Reply: AgentSessionReset.</summary>
public sealed record ResetAgentLoopMemory : IAgentSessionCommand
{
    public static readonly ResetAgentLoopMemory Instance = new();
}

/// <summary>Everything ResetAgentLoopMemory forgets, plus a new log file. Reply: AgentSessionReset.</summary>
public sealed record NewAgentSession : IAgentSessionCommand
{
    public static readonly NewAgentSession Instance = new();
}

/// <summary>Pick a saved session back up. Reply: AgentSessionResumed.</summary>
public sealed record ResumeAgentSession(string Path) : IAgentSessionCommand;

/// <summary>Reply: AgentSessionList.</summary>
public sealed record QueryAgentSessions : IAgentSessionCommand
{
    public static readonly QueryAgentSessions Instance = new();
}

/// <summary>Reply: SessionStats.</summary>
public sealed record QueryAgentStats : IAgentSessionCommand
{
    public static readonly QueryAgentStats Instance = new();
}

/// <summary>Reply: AgentSessionInfo, or AgentSessionFailed when the session could not be built.</summary>
public sealed record QueryAgentInfo : IAgentSessionCommand
{
    public static readonly QueryAgentInfo Instance = new();
}

/// <summary>Flip basic ↔ smart. Reply: SmartModeToggled.</summary>
public sealed record ToggleSmartMode : IAgentSessionCommand
{
    public static readonly ToggleSmartMode Instance = new();
}

// ------------------------------------------------------------ replies (loop →)

/// <summary>What a renderer shows in its banner and header; static for the session's life except Title, Smart and LogPath.</summary>
public sealed record AgentSessionInfo(
    string Root,
    string ProviderName,
    string Model,
    string? ReasoningModel,
    string ToolScope,
    string Shell,
    string? LogPath,
    bool SmartAvailable,
    bool Smart,
    string? Title,
    int MemoryChars);

/// <summary>The session could not be built — a provider without a key, say. The message is for the person.</summary>
public sealed record AgentSessionFailed(string Message);

public sealed record AgentSessionReset(string? LogPath);

public sealed record AgentSessionResumed(IReadOnlyList<SessionEntry> Entries, string? Title, string? LogPath);

public sealed record AgentSessionList(IReadOnlyList<SessionSummary> Sessions);

public sealed record SmartModeToggled(bool Changed, string Message, bool Smart);

// ------------------------------------------------------- from the loop (→ bot)

/// <summary>A phase tick. Thinking carries what the agent is doing, Acting the tool step, Generating a fragment of the answer.</summary>
public sealed record AgentLoopProgress(AgentLoopPhase Phase, string Text, int Round)
{
    /// <summary>The finished tool step, on Acting.</summary>
    public AgentStep? Step { get; init; }
}

/// <summary>The end of a run: exactly one per StartAgentLoop, whatever happened.</summary>
/// <param name="FailureReason">Null on success; "cancelled" when CancelAgentLoop ended it; else what went wrong.</param>
public sealed record AgentLoopResult(bool Success, string FinalMessage, int TurnCount, long ElapsedMs, string? FailureReason = null)
{
    /// <summary>The whole run, for renderers that print more than the text. Null when the line was empty or the turn threw.</summary>
    public AgentRun? Run { get; init; }

    public const string Cancelled = "cancelled";
}

/// <summary>Something beside the phases that a renderer shows once. Raised in order with the progress ticks.</summary>
public abstract record AgentLoopNotice;

/// <summary>Smart mode decided something: route, scope, safety, escalation, graph, knowledge.</summary>
public sealed record DecisionNotice(SmartNote Note) : AgentLoopNotice;

/// <summary>A one-line note: a read grant, an approval outcome, what the graph learned.</summary>
public sealed record NoteNotice(string Text) : AgentLoopNotice;

/// <summary>The task got a (new) name.</summary>
public sealed record TitleNotice(string Title) : AgentLoopNotice;

/// <summary>The strong model's design came back: its first lines.</summary>
public sealed record DesignNotice(IReadOnlyList<string> Lines) : AgentLoopNotice;

/// <summary>The turn taught something and the graph kept it.</summary>
public sealed record LearnedNotice(IReadOnlyList<Distilled> Items) : AgentLoopNotice;

/// <summary>The turn is parked until a person answers; ResolvePause with the same id continues it.</summary>
public abstract record PersonNeeded(int PauseId);

/// <summary>A command the gate would not run on its own.</summary>
public sealed record ApprovalNeeded(int PauseId, ApprovalRequest Request) : PersonNeeded(PauseId);

/// <summary>A design that hinges on a choice.</summary>
public sealed record ChoiceNeeded(int PauseId, ChoiceRequest Request) : PersonNeeded(PauseId);

// ------------------------------------------------------------------- wiring

/// <summary>
/// How the loop actor gets its session, handed in by whoever creates the bot —
/// the CLI builds a real <see cref="ChatSession"/>, a test a scripted one. The
/// factory runs inside the loop actor, on first use, so a provider that cannot
/// be built surfaces as AgentSessionFailed rather than as a crashed actor.
/// </summary>
public sealed record AgentLoopBindings(Func<ChatSession> SessionFactory);
