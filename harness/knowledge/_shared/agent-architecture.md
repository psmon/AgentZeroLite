---
agent: _shared
topic: agent-architecture
audience: anyone editing Project/ZeroCommon/Actors/ or Project/ZeroCommon/Llm/Tools/
last_synced: 2026-05-05
---

# Agent architecture — canonical vocabulary

This is the project's binding glossary for the LLM-agent layer. The names
align with Anthropic's *Building effective agents* post and Claude Agent
SDK so external tutorials map directly onto our types. **Do not drift back
to "Reactor" / "ToolLoop" / "Host" terminology** — that naming was
deliberately retired in M0013 (2026-05-05).

## Two-layer model

```
        ┌─────────────────────────────────────────────────────┐
        │  AgentBotActor                                      │ Gateway layer
        │  (Project/ZeroCommon/Actors/AgentBotActor.cs)       │
        │  Path: /user/stage/bot                              │
        │  - Chat / Key mode                                  │
        │  - UI delegate gateway                              │
        │  - Peer-signal routing + handshake state            │
        │  - First-contact terminal introductions             │
        └────────────────────┬────────────────────────────────┘
                             │ spawns lazily
                             ▼
        ┌─────────────────────────────────────────────────────┐
        │  AgentLoopActor                                     │ Agent layer
        │  (Project/ZeroCommon/Actors/AgentLoopActor.cs)      │ (THE agent)
        │  Path: /user/stage/bot/loop                         │
        │  - Owns one IAgentLoop (LLamaContext or REST history)│
        │  - FSM: Idle → Thinking → Generating → Acting → Done │
        │  - Pushes AgentLoopProgress + AgentLoopResult to    │
        │    parent (AgentBotActor) via Tell                  │
        └────────────────────┬────────────────────────────────┘
                             │ runs
                             ▼
        ┌─────────────────────────────────────────────────────┐
        │  IAgentLoop                                         │ Loop layer
        │  - LocalAgentLoop  (LLamaSharp + GBNF)              │ (backends)
        │  - ExternalAgentLoop (OpenAI-compatible REST)       │
        │  RunAsync(userRequest) → AgentLoopRun               │
        │  Drives one or more turns through an IAgentToolbelt │
        │  until the model emits `done` or guards trip.       │
        └─────────────────────────────────────────────────────┘
```

The split exists because **inference and UI must not share a thread**. UI
mode/state belongs to `AgentBotActor`; KV-cache + tool dispatch belong to
`AgentLoopActor`. Tell-based messages are the only seam between them.

## Symbol → meaning

| Symbol | Layer | Meaning |
|---|---|---|
| `AgentBotActor` | Actor | UI gateway. Holds chat/key mode, callback delegates, peer routing, terminal intros. Spawns one `AgentLoopActor` child lazily. |
| `AgentLoopActor` | Actor | The agent. Owns one `IAgentLoop`. FSM: Idle → Thinking → Generating → Acting → Done. |
| `AgentLoopBindings` | Wiring | Record passed at construction. Carries `ToolbeltFactory`, `OptionsFactory`, `AgentLoopFactory`. |
| `IAgentLoop` | Tools | Backend-agnostic contract. `RunAsync(userRequest, ct) → AgentLoopRun`. |
| `LocalAgentLoop` | Tools | On-device LLamaSharp + GBNF (Gemma 4 default, Nemotron via Llama-3.1 chat template). |
| `ExternalAgentLoop` | Tools | OpenAI-compatible REST (Webnori / OpenAI / LMStudio / Ollama). No grammar enforcement; Gemma 4 over-the-wire. |
| `IAgentToolbelt` | Tools | The side-effect surface the agent acts against — `ListTerminalsAsync / ReadTerminalAsync / SendToTerminalAsync / SendKeyAsync`. Production = `WorkspaceTerminalToolHost`; tests = `MockAgentToolbelt`. |
| `AgentLoopOptions` | Tools | Shared options record (MaxIterations, MaxTokensPerTurn, Temperature, repeat/retry caps, OnTurnCompleted/OnGenerationProgress callbacks). |
| `AgentLoopGuards` | Tools | Per-run repeat detection + consecutive-block hard stop + transient-HTTP retry with exponential backoff. |
| `AgentLoopRun` | Tools | Outcome record from one `RunAsync`: Turns + FinalMessage + TerminatedCleanly + FailureReason + GuardStats. |
| `AgentToolGrammar` | Tools | GBNF for tool-call envelopes. *Keeps the "Tool" prefix* — the grammar literally enforces tool calls, so the name is precise. |
| `ToolCall` / `ToolTurn` / `GuardStats` | Tools | Plain data records. No rename. |
| `AgentLoopPhase` | Messages | Enum: Idle / Thinking / Generating / Acting / Done / Error. |
| `AgentLoopProgress` | Messages | AgentLoop → Bot → UI. Phase + text + round + optional tokens / ToolCallInfo. |
| `AgentLoopResult` | Messages | Terminal of one Start. Success + FinalMessage + TurnCount + ElapsedMs + FailureReason. |
| `AgentLoopToolCallInfo` | Messages | Carries tool / argsJson / result on Acting-phase progress. |
| `StartAgentLoop` | Messages | UI → Bot → Loop. Triggers a new turn loop with a user request. |
| `CancelAgentLoop` | Messages | UI → Bot → Loop. Cancel-token-source flick. |
| `ResetAgentLoopMemory` | Messages | UI → Bot → Loop. Disposes the loop (KV cache + history); next Start rebuilds from scratch. |
| `SetAgentLoopCallbacks` | Messages | UI → Bot. Registers OnProgress / OnResult delegates. |
| `AgentLoopBindings` | Messages | UI → Bot. Carries the three factories. |

## Names that were retired

| Old | New |
|---|---|
| `AgentReactorActor` | `AgentLoopActor` |
| `IAgentToolLoop` | `IAgentLoop` |
| `AgentToolLoop` | `LocalAgentLoop` |
| `ExternalAgentToolLoop` | `ExternalAgentLoop` |
| `IAgentToolHost` | `IAgentToolbelt` |
| `MockAgentToolHost` | `MockAgentToolbelt` |
| `AgentToolSession` | `AgentLoopRun` |
| `AgentToolLoopOptions` | `AgentLoopOptions` |
| `ToolLoopGuards` | `AgentLoopGuards` |
| `ReactorBindings` | `AgentLoopBindings` |
| `ReactorPhase` | `AgentLoopPhase` |
| `ReactorProgress` | `AgentLoopProgress` |
| `ReactorResult` | `AgentLoopResult` |
| `ReactorToolCallInfo` | `AgentLoopToolCallInfo` |
| `StartReactor` | `StartAgentLoop` |
| `CancelReactor` | `CancelAgentLoop` |
| `ResetReactorSession` | `ResetAgentLoopMemory` |
| `SetReactorCallbacks` | `SetAgentLoopCallbacks` |
| Akka path `/user/stage/bot/reactor` | `/user/stage/bot/loop` |

## Names deliberately kept

- **`AgentBotActor`** — its job genuinely is the *bot* (the user-facing
  mode switcher / UI gateway). Renaming it makes "the agent" ambiguous,
  not clearer. Keep.
- **`AgentToolGrammar`** — the GBNF *literally* constrains JSON tool
  calls, so "Tool" is descriptive, not implementation-flavored. Keep.
- **`ToolCall` / `ToolTurn` / `GuardStats`** — plain data records named
  for what they carry. No churn.
- **`AgentToolLoopMaxTokens`** field on `LlmRuntimeSettings` — kept for
  JSON-persistence backward compat. The setting key is in users' on-disk
  settings file; renaming the C# field breaks deserialization for
  existing installs. Doc-comment was updated to point at `LocalAgentLoop`.

## When you write new code in this layer

1. **Read this glossary first.** Any new type that touches the agent
   loop should reuse vocabulary already here.
2. **No "Reactor" / "ToolLoop" / "Host"** in new identifiers — those are
   retired. If a reviewer sees them, treat it as a regression and rename.
3. **AgentBotActor doesn't run inference** — UI gateway only. Inference
   work goes inside `AgentLoopActor` or its `IAgentLoop`.
4. **Two semantics → two records.** When designing message protocols,
   prefer one record per semantic boundary (e.g. `AgentLoopProgress` for
   phase ticks vs `AgentLoopResult` for run completion). Don't reuse one
   record across both.
5. **Internal mailbox messages stay private** — `TurnCompletedInternal`,
   `GenerationProgressInternal`, `RunCompletedInternal`,
   `RunFailedInternal` are nested inside `AgentLoopActor` because they're
   PipeTo / Self.Tell only. Don't promote them.

## Related

- Mission spec: `harness/missions/M0013-agent-loop-rename.md`
- Mission record: `harness/logs/mission-records/M0013-수행결과.md`
- Sourcing research: `harness/logs/code-coach/2026-05-05-10-46-actor-llm-agent-naming-research.md`
- Anthropic post (external): *Building effective agents* — referenced
  for the canonical "agent loop" noun.
