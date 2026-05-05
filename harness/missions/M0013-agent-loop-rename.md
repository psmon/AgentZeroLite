---
id: M0013
title: Reactor → AgentLoop 리네임 (Agent vocabulary 정렬)
operator: psmon
language: ko
status: in_progress
priority: medium
created: 2026-05-05
started: 2026-05-05T10:50:00+09:00
related: [M0004]
---

# 요청 (Brief)

`Project/ZeroCommon/Actors/AgentReactorActor` 와 `Project/ZeroCommon/Llm/Tools/`
계열의 LLM 에이전트 코드 어휘를 산업 표준(Anthropic *Building effective agents*
+ Claude Agent SDK + LangChain) 의 **"agent loop"** 에 맞춰 정리한다.

사전조사: `harness/logs/code-coach/2026-05-05-10-46-actor-llm-agent-naming-research.md`
(Option B 추천 — Agent-flavored, 구조 보존).

## 결정사항

- **Option B 채택** — `AgentBotActor` 는 그대로(진짜로 게이트웨이 역할), 추론
  쪽 `Reactor` 와 `ToolLoop` 만 어휘 정리.
- **Akka path** `/user/stage/bot/reactor` → `/user/stage/bot/loop`.
- **knowledge 노트 신규 작성** — `harness/knowledge/_shared/agent-architecture.md`
  에 canonical 어휘 박아 미래 PR 의 drift 방지.

## 심볼 매핑

### 파일 rename

| 기존 | 신규 |
|---|---|
| `Project/ZeroCommon/Actors/AgentReactorActor.cs` | `AgentLoopActor.cs` |
| `Project/ZeroCommon/Llm/Tools/IAgentToolLoop.cs` | `IAgentLoop.cs` |
| `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs` | `LocalAgentLoop.cs` |
| `Project/ZeroCommon/Llm/Tools/ExternalAgentToolLoop.cs` | `ExternalAgentLoop.cs` |
| `Project/ZeroCommon/Llm/Tools/IAgentToolHost.cs` | `IAgentToolbelt.cs` |
| `Project/ZeroCommon/Llm/Tools/ToolLoopGuards.cs` | `AgentLoopGuards.cs` |
| `Project/ZeroCommon.Tests/AgentReactorActorTests.cs` | `AgentLoopActorTests.cs` |
| `Project/ZeroCommon.Tests/AgentToolLoopTests.cs` | `AgentLoopTests.cs` |
| `Project/ZeroCommon.Tests/ExternalAgentToolLoopTests.cs` | `ExternalAgentLoopTests.cs` |

### 심볼 rename (codebase-wide replace_all)

| 기존 | 신규 | 비고 |
|---|---|---|
| `AgentReactorActor` | `AgentLoopActor` | actor 클래스 |
| `IAgentToolLoop` | `IAgentLoop` | 백엔드-agnostic 계약 |
| `AgentToolLoop` | `LocalAgentLoop` | 로컬 LLamaSharp + GBNF 구현 |
| `ExternalAgentToolLoop` | `ExternalAgentLoop` | OpenAI-compatible REST 구현 |
| `IAgentToolHost` | `IAgentToolbelt` | 사이드이펙트 surface |
| `MockAgentToolHost` | `MockAgentToolbelt` | 테스트용 더블 |
| `AgentToolSession` | `AgentLoopRun` | RunAsync 1회 실행 기록 (Turns + FinalMessage + GuardStats) |
| `ReactorBindings` | `AgentLoopBindings` | factory + UI delegates |
| `ReactorPhase` | `AgentLoopPhase` | Idle/Thinking/Generating/Acting/Done |
| `ReactorProgress` | `AgentLoopProgress` | actor → UI phase 변화 |
| `ReactorResult` | `AgentLoopResult` | actor → UI 최종 결과 |
| `ReactorToolCallInfo` | `AgentLoopToolCallInfo` | progress payload |
| `StartReactor` | `StartAgentLoop` | UI → Bot → Loop |
| `CancelReactor` | `CancelAgentLoop` | UI → Bot → Loop |
| `ResetReactorSession` | `ResetAgentLoopMemory` | KV cache + introductions 리셋 |
| `SetReactorCallbacks` | `SetAgentLoopCallbacks` | UI delegate 등록 |
| `/bot/reactor` (Akka path) | `/bot/loop` | Ping 응답·로그 노출 |

### 유지

- `AgentBotActor` — 게이트웨이 역할이라 이름이 정확. 변경 없음.
- `RegisterBot / CreateBot / BotCreated / SetBotUiCallback / SwitchBotMode / BotResponse / BotMode` — 게이트웨이 프로토콜. 변경 없음.
- `AgentToolGrammar` — GBNF 가 실제로 "tool" 호출을 강제. 정확한 이름.
- `ToolCall / ToolTurn / GuardStats` — 데이터 모양에 충실한 이름. 변경 없음.
- `TurnCompletedInternal / GenerationProgressInternal` — actor 내부 메시지. 변경 없음.

## 변경 surface

19 파일. ~80% mechanical replace, ~20% 문맥 의존 (XML doc + 주석).

```
Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs
Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs
Project/AgentZeroWpf/UI/Components/SettingsPanel.Llm.cs
Project/ZeroCommon/Actors/AgentBotActor.cs
Project/ZeroCommon/Actors/AgentReactorActor.cs   → AgentLoopActor.cs
Project/ZeroCommon/Actors/Messages.cs
Project/ZeroCommon/Actors/StageActor.cs
Project/ZeroCommon/Llm/LlmRuntimeSettings.cs
Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs (주석만)
Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs    → LocalAgentLoop.cs
Project/ZeroCommon/Llm/Tools/ChatTemplates.cs
Project/ZeroCommon/Llm/Tools/ExternalAgentToolLoop.cs → ExternalAgentLoop.cs
Project/ZeroCommon/Llm/Tools/IAgentToolHost.cs   → IAgentToolbelt.cs
Project/ZeroCommon/Llm/Tools/IAgentToolLoop.cs   → IAgentLoop.cs
Project/ZeroCommon/Llm/Tools/T0Probe.cs
Project/ZeroCommon/Llm/Tools/ToolLoopGuards.cs   → AgentLoopGuards.cs
Project/ZeroCommon/Voice/Streams/VoiceStreamMessages.cs
Project/ZeroCommon.Tests/AgentReactorActorTests.cs   → AgentLoopActorTests.cs
Project/ZeroCommon.Tests/AgentToolLoopTests.cs       → AgentLoopTests.cs
Project/ZeroCommon.Tests/ExternalAgentToolLoopTests.cs → ExternalAgentLoopTests.cs
Project/ZeroCommon.Tests/NemotronProbeTests.cs
```

## 검증 게이트

- `dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug` 통과
- `dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj` 전 케이스 green
- `Project/AgentTest` 는 desktop-session 의존 — 본 미션에선 skip, 후속 gate 로 표시
- CLAUDE.md actor-topology 블록 갱신
- `harness/knowledge/_shared/agent-architecture.md` 신규

## 비-목표

- 행동 변경 0. 순수 rename.
- `AgentBotActor` 분리 (Option C) 는 본 미션 범위 밖. 추후 multi-agent 확장이
  실제로 필요해지면 별도 미션.
