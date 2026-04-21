// ─────────────────────────────────────────────────────────────
// AgentZero Actor Messages — Protocol Definition (Lite)
// ─────────────────────────────────────────────────────────────

using Agent.Common.Services;

namespace Agent.Common.Actors;

// ═══════════════════════════════════════════════════════════════
// 0. HealthCheck
// ═══════════════════════════════════════════════════════════════

public sealed record Ping;
public sealed record Pong(string ActorName, string ActorPath, string Status);

// ═══════════════════════════════════════════════════════════════
// 1. Stage 메시지
// ═══════════════════════════════════════════════════════════════

public sealed record RegisterWorkspace(string WorkspaceName, string DirectoryPath);
public sealed record UnregisterWorkspace(string WorkspaceName);
public sealed record QueryStageStatus;

public sealed record StageStatusResponse(
    IReadOnlyList<WorkspaceInfo> Workspaces,
    bool BotRegistered);

public sealed record WorkspaceInfo(
    string Name,
    string DirectoryPath,
    IReadOnlyList<TerminalInfo> Terminals);

public sealed record TerminalInfo(
    string TerminalId,
    string SessionId,
    nint Hwnd = 0);

// ═══════════════════════════════════════════════════════════════
// 2. AgentBot 메시지
// ═══════════════════════════════════════════════════════════════

public sealed record SwitchBotMode(BotMode TargetMode);

public enum BotMode
{
    Chat,   // 터미널 직접 전송
    Key     // 제어키 전송 모드
}

public sealed record UserInput(string Text);
public sealed record SendToTerminal(string WorkspaceName, string TerminalId, string Text);
public sealed record SendControlToTerminal(string WorkspaceName, string TerminalId, string ControlKey);
public sealed record QueryActiveTerminal;

public sealed record ActiveTerminalResponse(
    string? WorkspaceName,
    string? TerminalId);

public sealed record BotResponse(string Text, BotResponseType Type);

public enum BotResponseType
{
    Chat,
    System,
    Error
}

// ═══════════════════════════════════════════════════════════════
// 3. Workspace 메시지
// ═══════════════════════════════════════════════════════════════

public sealed record CreateTerminal(string TerminalId, string SessionId);
public sealed record DestroyTerminal(string TerminalId);
public sealed record RenameTerminal(string OldTerminalId, string NewTerminalId);
public sealed record QueryTerminals;
public sealed record TerminalsResponse(IReadOnlyList<TerminalInfo> Terminals);

// ═══════════════════════════════════════════════════════════════
// 4. Terminal 메시지
// ═══════════════════════════════════════════════════════════════

public sealed record WriteToTerminal(string Text);
public sealed record SendTerminalControl(string ControlKey);
public sealed record TerminalOutput(string TerminalId, string Text, DateTimeOffset Timestamp);
public sealed record QueryTerminalStatus;

public sealed record TerminalStatusResponse(
    string TerminalId,
    string SessionId,
    bool IsRunning);

// ═══════════════════════════════════════════════════════════════
// 5. 생명주기 이벤트
// ═══════════════════════════════════════════════════════════════

public sealed record TerminalRegistered(string WorkspaceName, string TerminalId);
public sealed record TerminalUnregistered(string WorkspaceName, string TerminalId);

// ═══════════════════════════════════════════════════════════════
// 6. Stage 경유 편의 메시지
// ═══════════════════════════════════════════════════════════════

public sealed record CreateTerminalInWorkspace(string WorkspaceName, string TerminalId, string SessionId);
public sealed record DestroyTerminalInWorkspace(string WorkspaceName, string TerminalId);
public sealed record RenameTerminalInWorkspace(string WorkspaceName, string OldTerminalId, string NewTerminalId);
public sealed record SetActiveTerminal(string? WorkspaceName, string? TerminalId);
public sealed record BindSessionInWorkspace(string WorkspaceName, string TerminalId, ITerminalSession Session);
public sealed record BindSession(ITerminalSession Session);
public sealed record UnbindSession;
public sealed record UpdateTerminalHwnd(string WorkspaceName, string TerminalId, nint Hwnd);

public sealed record CreateBot;
public sealed record BotCreated(Akka.Actor.IActorRef BotRef);
public sealed record SetBotUiCallback(Action<string, BotResponseType> Callback);

// ═══════════════════════════════════════════════════════════════
// 7. Bot 생명주기
// ═══════════════════════════════════════════════════════════════

public sealed record RegisterBot;
public sealed record UnregisterBot;
