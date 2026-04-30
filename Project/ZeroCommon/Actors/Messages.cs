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

// AgentBot ↔ Terminal AI 첫 접촉 추적용. AIMODE에서 send_to_terminal이
// 어떤 (group, tab) 조합으로 처음 발화될 때 한 번만 자기소개를 prepend
// 하고, 같은 (group, tab)에 대해서는 더 이상 prepend하지 않게 한다.
// 상태는 AgentBotActor가 보유 — 액터 모델 원칙 (state는 actor 안에).
//
// IntroduceTerminalIfFirst: Ask 패턴. Reply = bool (true면 첫 접촉).
// ResetIntroductions: AIMODE 세션이 새로 시작되면 모두 잊는다.
public sealed record IntroduceTerminalIfFirst(int GroupIndex, int TabIndex);
public sealed record IntroduceTerminalReply(bool WasFirstContact);
public sealed record ResetIntroductions;

// ═══════════════════════════════════════════════════════════════
// 8. AgentReactor (AIMODE 추론 FSM) 메시지
// ═══════════════════════════════════════════════════════════════
//
// AIMODE 추론은 AgentBotActor의 자식 AgentReactorActor에서 수행된다.
// (경로: /user/stage/bot/reactor)
//
// 책임 분리:
//   - AgentBotActor    : 채팅 UI 상태 + 첫 접촉 추적 + Reactor 자식 관리
//   - AgentReactorActor: AgentToolLoop 라이프사이클 + Idle/Thinking/Acting/Done FSM
//                        + LLamaContext 보관 + 진행 상황 부모로 푸시
//
// UI ↔ 액터 통신은 모두 Tell 기반:
//   - SetReactorCallbacks: UI delegate 등록 (Bot이 보관, Reactor 메시지 수신 시 호출)
//   - StartReactor       : 사용자 발화로 새 turn loop 시작
//   - ReactorProgress    : Thinking/Acting/Generating 상태 변화 → UI 갱신
//   - ReactorResult      : 최종 응답(성공) 또는 실패 사유
//   - CancelReactor      : 진행 중인 turn loop 취소
//   - ResetReactorSession: KV cache + introductions 리셋, 다음 Start에서 새 세션

/// <summary>UI에 Reactor 진행 상황을 전달할 콜백 등록 (UI → Bot).</summary>
public sealed record SetReactorCallbacks(
    Action<ReactorProgress> OnProgress,
    Action<ReactorResult> OnResult);

/// <summary>AIMODE 추론 시작 (UI → Bot → Reactor).</summary>
public sealed record StartReactor(string UserRequest);

/// <summary>Reactor 진행 상황 (Reactor → Bot → UI).</summary>
public sealed record ReactorProgress(
    ReactorPhase Phase,
    string Text,
    int Round)
{
    /// <summary>Acting 단계에서 완료된 도구 호출 정보 (Phase=Acting일 때만).</summary>
    public ReactorToolCallInfo? ToolCall { get; init; }

    /// <summary>Generating 단계 누적 토큰 수 (Phase=Generating일 때만).</summary>
    public int Tokens { get; init; }
}

/// <summary>Reactor 최종 결과 (Reactor → Bot → UI).</summary>
public sealed record ReactorResult(
    bool Success,
    string FinalMessage,
    int TurnCount,
    long ElapsedMs,
    string? FailureReason = null);

/// <summary>도구 호출 정보 — UI 카드 표시용.</summary>
public sealed record ReactorToolCallInfo(
    string Tool,
    string ArgsJson,
    string Result);

/// <summary>Reactor FSM 단계.</summary>
public enum ReactorPhase
{
    Idle,        // 대기
    Thinking,    // 첫 토큰 대기 (prefill)
    Generating,  // 토큰 스트리밍 중 (per-N tokens 갱신)
    Acting,      // 도구 실행 직후 (tool result 확보)
    Done,        // 정상 종료
    Error        // 비정상 종료
}

/// <summary>진행 중 추론 취소 (UI → Bot → Reactor).</summary>
public sealed record CancelReactor;

/// <summary>
/// 현재 Reactor 세션 폐기 (KV cache + tool loop dispose). 다음 StartReactor가
/// 새 KV cache로 재시작하게 한다. UI의 "New Session" 버튼이 사용.
/// 부수 효과: AgentBotActor의 introductions도 함께 클리어.
/// </summary>
public sealed record ResetReactorSession;

/// <summary>
/// Reactor 자식 생성에 필요한 호스트 의존성. UI가 첫 SetReactorCallbacks
/// 시점에 함께 전달.
///
/// <para><b>Backend-agnostic</b>: <see cref="ToolLoopFactory"/>는 활성 백엔드
/// (Local: LLamaSharp+GBNF / External: REST)를 보고 적절한 <see cref="Agent.Common.Llm.Tools.IAgentToolLoop"/>
/// 구현을 만들어 돌려준다. 액터는 어느 쪽인지 알 필요 없이 인터페이스만 호출.</para>
///
/// <para>팩토리는 (a) 액터가 OnTurnCompleted/OnGenerationProgress 콜백을
/// 주입한 옵션과 (b) 액터가 만든 호스트 인스턴스를 인자로 받아, 둘 다
/// 결합한 루프를 반환한다. 백엔드가 준비 안됐으면 (Local 미로드, External
/// 키 미설정 등) null 반환 — 액터는 실패 메시지를 부모에게 푸시하고 종료.</para>
/// </summary>
public sealed record ReactorBindings(
    Func<Agent.Common.Llm.Tools.IAgentToolHost> HostFactory,
    Func<Agent.Common.Llm.Tools.AgentToolLoopOptions> OptionsFactory,
    Func<Agent.Common.Llm.Tools.AgentToolLoopOptions, Agent.Common.Llm.Tools.IAgentToolHost,
         Agent.Common.Llm.Tools.IAgentToolLoop?> ToolLoopFactory);

// ─── Peer-signal protocol (terminal → bot push, primary path) ───
//
// Design principle: during an active conversation, AgentBot prefers to
// REACT to terminal-pushed messages over polling read_terminal. The
// terminal AI calls back into AgentZero via the existing
// `AgentZeroLite.exe -cli bot-chat <message> --from <peerName>` CLI
// command — that CLI already exists and uses WM_COPYDATA IPC to reach
// the running GUI. This protocol routes that CLI-delivered message
// from MainWindow.HandleBotChat into the actor system so the reactor
// can wake up and react.
//
// IDs are STRINGS (peerName) because:
//   - CLI's `--from <name>` is a free-form string
//   - TerminalActor knows (workspace, terminalId) — no native (g, t)
//   - LLM's tool calls use (g, t) but the bot can map (g, t) → display
//     name when marking conversations active
// The string IS the contract between the peer's `--from` value and
// the bot's bookkeeping.

/// <summary>
/// A peer terminal AI sent a message addressed to AgentBot via the
/// CLI bot-chat path. <paramref name="PeerName"/> matches the
/// <c>--from</c> value used at the CLI (e.g. "Claude").
/// </summary>
public sealed record TerminalSentToBot(string PeerName, string Text);

/// <summary>
/// Mark <paramref name="PeerName"/> as an active conversation context.
/// While active, <see cref="TerminalSentToBot"/> for this peer triggers
/// a continuation cycle on the reactor. When inactive, peer signals
/// are logged and dropped (peer signals from un-asked-for terminals
/// are noise, not invitations).
/// </summary>
public sealed record MarkConversationActive(string PeerName);

/// <summary>End the active conversation context for the named peer.</summary>
public sealed record ClearConversationActive(string PeerName);

/// <summary>Ask pattern → <see cref="ActiveConversationsReply"/>.</summary>
public sealed record QueryActiveConversations;

/// <summary>Reply for QueryActiveConversations.</summary>
public sealed record ActiveConversationsReply(IReadOnlyCollection<string> Active);

// ─── Handshake state (per peer) ───
//
// Conversation lifecycle:
//   NotConnected      — peer never proved it can call back via bot-chat
//   HandshakeSent     — bot sent handshake message; awaiting peer's bot-chat
//   Connected         — at least one bot-chat received from this peer
//
// On `MarkHandshakeSent` → state becomes HandshakeSent.
// On `TerminalSentToBot` → state becomes Connected (and the signal still
//   gets routed to the reactor as a continuation trigger).

public enum HandshakeState
{
    NotConnected,
    HandshakeSent,
    Connected
}

/// <summary>Bot records that a handshake message was just dispatched to PeerName.</summary>
public sealed record MarkHandshakeSent(string PeerName);

/// <summary>Ask pattern → <see cref="HandshakeStateReply"/>.</summary>
public sealed record QueryHandshakeState(string PeerName);

/// <summary>Reply for QueryHandshakeState.</summary>
public sealed record HandshakeStateReply(string PeerName, HandshakeState State);
