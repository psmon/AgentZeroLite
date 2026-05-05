---
date: 2026-05-05T01:46:47Z
agent: code-coach
type: research
mode: log-eval
trigger: "지금 Actor 모델이 LLM을 Agent화 하는부분 네이밍이랑 컨셉 설명 해줄래? Agent가까운 느낌으로 이름변경 리팩토링을 하려고함"
---

# Actor ↔ LLM agent-ization — naming research + refactor proposal

## Problem statement

The codebase has two parallel naming layers — **Actor side** (`AgentBotActor`,
`AgentReactorActor`) and **LLM tool-loop side** (`IAgentToolLoop`,
`AgentToolLoop`, `IAgentToolHost`, `AgentToolSession`). The operator finds
the result reads "Akka-flavored" rather than "agent-flavored" and wants a
rename pass that aligns with modern agent vocabulary (Claude Agent SDK,
Anthropic *Building effective agents*, LangChain).

## Current vocabulary map (as built)

| Tier | Symbol | File | Role today |
|---|---|---|---|
| Actor | `AgentBotActor` | `Project/ZeroCommon/Actors/AgentBotActor.cs:16` | Singleton at `/user/stage/bot`. NOT inference. UI gateway: mode (Chat/Key), UI callback, peer routing, terminal first-contact tracking, spawns Reactor child lazily. |
| Actor | `AgentReactorActor` | `Project/ZeroCommon/Actors/AgentReactorActor.cs:27` | At `/user/stage/bot/reactor`. **THIS is the agent.** Owns one `IAgentToolLoop` (LLamaContext-bound), drives Idle → Thinking → Generating → Acting → Done FSM, PipeTo's tool-loop completion back into its mailbox. |
| Wiring | `ReactorBindings` | (record passed to ctor) | HostFactory + OptionsFactory + ToolLoopFactory + UI delegates. |
| Tool loop | `IAgentToolLoop` | `Project/ZeroCommon/Llm/Tools/IAgentToolLoop.cs:11` | Backend-agnostic. `RunAsync(userRequest) → AgentToolSession`. |
| Tool loop | `AgentToolLoop` | `Llm/Tools/AgentToolLoop.cs` | Local LLamaSharp + GBNF (Gemma 4 standard). |
| Tool loop | `ExternalAgentToolLoop` | `Llm/Tools/ExternalAgentToolLoop.cs` | OpenAI-compatible REST. |
| Side effects | `IAgentToolHost` | `Llm/Tools/IAgentToolHost.cs:12` | The surface the agent acts against — `ListTerminalsAsync / ReadTerminalAsync / SendToTerminalAsync / SendKeyAsync`. |
| Outcome | `AgentToolSession` | `Llm/Tools/IAgentToolHost.cs:53` | Session record: `Turns + FinalMessage + TerminatedCleanly + FailureReason + GuardStats`. |
| Grammar | `AgentToolGrammar` | `Llm/Tools/AgentToolGrammar.cs` | GBNF for Gemma 4 standard. |
| FSM | `ReactorPhase` | (in `Messages.cs`) | `Idle / Thinking / Generating / Acting / Done`. |
| Messages | `StartReactor / ReactorProgress / ReactorResult / CancelReactor / ResetReactorSession / SetReactorCallbacks` | `Actors/Messages.cs:147` | UI ↔ Bot ↔ Reactor protocol. |
| Messages | `RegisterBot / CreateBot / BotCreated / SetBotUiCallback / SwitchBotMode / BotResponse / BotMode` | `Actors/Messages.cs:42` | UI gateway protocol. |

**Reach** — `AgentBotActor`, `AgentReactorActor`, `AgentToolLoop`, `IAgentToolHost`,
`ReactorPhase`, `AgentToolSession` are referenced across ~20 files (incl.
WPF host, settings panel, voice stream, and all 4 ZeroCommon.Tests files).

## What reads awkwardly

1. **`AgentBot*` is double-tagged.** "Agent" + "Bot" both mean
   "automated participant", stacked on a class that is actually the
   *UI gateway*, not the agent. New readers expect inference here and find
   none — it's all delegated to the child.
2. **`Reactor` is an Akka idiom**, not an agent-domain noun. Accurate
   (it reacts to phase transitions) but invisible to readers from the
   LangChain / Anthropic SDK world, who'd recognize "agent loop", "agent
   runner", or just "agent".
3. **`AgentToolLoop` exposes the *how*, not the *what*.** "Tool loop" is
   OpenAI function-calling jargon. The user-meaningful concept is "the
   agent" — the loop is the agent's heartbeat.
4. **Two parallel namespaces** (`Bot*` and `Reactor*`) force readers to
   learn both vocabularies and remember which one carries which state.
5. **`AgentToolSession` is misnamed.** Contains the result of one
   `RunAsync`. "Session" implies persistence across calls; this is per-run.

## Options

### Option A — Pure Agent rename (broad surface, highest readability)

| Old | New |
|---|---|
| `AgentBotActor` | `AgentSessionActor` |
| `AgentReactorActor` | `AgentRunnerActor` |
| `IAgentToolLoop` / `AgentToolLoop` / `ExternalAgentToolLoop` | `IAgentRun` / `LocalAgentRun` / `ExternalAgentRun` |
| `IAgentToolHost` | `IAgentTools` |
| `AgentToolSession` | `AgentRunResult` |
| `ReactorBindings` | `AgentBindings` |
| `ReactorPhase / Progress / Result` | `AgentPhase / AgentProgress / AgentResult` |
| `StartReactor / CancelReactor / ResetReactorSession` | `StartAgent / CancelAgent / ResetAgentMemory` |

Tradeoff: Touches ~25 files (every file in the ref-list). Aligns the
codebase with industry vocabulary so a new contributor reading
`AgentRunner` + `AgentRun` + `AgentTools` + `AgentResult` can guess the
architecture without reading code. Akka path `/user/stage/bot/reactor`
must also be renamed (test fixtures pin those literals).

### Option B — Agent-flavored vocabulary, structural names preserved (recommended)

| Old | New | Reason |
|---|---|---|
| `AgentReactorActor` | `AgentLoopActor` | "agent loop" is the Anthropic *Building effective agents* canonical noun. |
| `IAgentToolLoop` | `IAgentLoop` | The loop calls tools; the loop itself is *the agent's main loop*. Drop "Tool". |
| `AgentToolLoop` / `ExternalAgentToolLoop` | `LocalAgentLoop` / `ExternalAgentLoop` | Backend implementations of the same loop contract. |
| `IAgentToolHost` | `IAgentToolbelt` | "Toolbelt" = the set of tools the agent can reach for. Keeps "tool" as noun (correct), drops "host" (Akka-flavored). |
| `AgentToolSession` | `AgentLoopResult` | One run, one result. |
| `AgentToolGrammar` | (keep) | The GBNF grammar literally enforces "tool" calls. Naming is precise. |
| `ReactorPhase / ReactorProgress / ReactorResult` | `AgentLoopPhase / AgentLoopProgress / AgentLoopResult` | Self-explanatory. |
| `StartReactor / CancelReactor / ResetReactorSession / SetReactorCallbacks` | `StartAgentLoop / CancelAgentLoop / ResetAgentLoopMemory / SetAgentLoopCallbacks` | Mechanical. |
| `ReactorBindings` | `AgentLoopBindings` | Mechanical. |
| `AgentBotActor` | (keep) | Its role really *is* the bot/UI gateway. Renaming this is a separate decision. |
| `Bot*` messages | (keep) | Coupled to the gateway, not the loop. |

Tradeoff: ~12-15 files touched (all the Reactor/ToolLoop call sites, including
4 test files). 80% of the readability win at ~50% of the change surface.
After this lands, the architecture reads as: "AgentBotActor is the UI
gateway → AgentLoopActor runs the agent loop → IAgentLoop is the
backend-agnostic agent contract → IAgentToolbelt is the side-effect
surface." A new reader can map LangChain / Claude Agent SDK concepts in
under a minute.

### Option C — Two-tier rename: split "Agent" from "Bot" cleanly

| Old | New |
|---|---|
| `AgentBotActor` | `BotGatewayActor` |
| `AgentReactorActor` | `AgentActor` |
| `IAgentToolLoop` | `IAgentExecutor` |
| `AgentToolLoop` / `ExternalAgentToolLoop` | `LocalAgentExecutor` / `ExternalAgentExecutor` |
| `IAgentToolHost` | `IAgentTools` |
| `AgentToolSession` | `AgentTurnLog` |

Tradeoff: Most semantically sharp — "Bot = UI gateway, Agent = inference
engine" is teachable in one sentence. But ~30 files, and `AgentActor` is
dangerously generic — if the project ever grows multiple specialist
agents (security-guard agent, planner agent), `AgentActor` would block
that namespace. Recommend deferring until that need actually appears.

## Recommendation

**Option B** as the first pass — `AgentLoopActor` + `IAgentLoop` + `IAgentToolbelt`
+ `AgentLoopResult`. Reasons:

- Maximum readability win per file touched.
- Doesn't burn the `AgentActor` name (keeps it free for Option C later if
  multi-agent expansion happens).
- Keeps `AgentBotActor` because its job genuinely is the bot/UI gateway;
  the friction was on the inference side, not the gateway side.
- Aligns with the Anthropic *Building effective agents* post + Claude Agent
  SDK terminology, both of which use "agent loop" as the canonical noun.

## Concrete next steps

1. **Pin the symbol map as a mission** — create `harness/missions/M00xx-agent-rename.md`
   with the Option B before/after table. Mission record will live at
   `harness/logs/mission-records/M00xx-수행결과.md` after execution.
2. **Single PR, mechanical rename**:
   - Rename files + class names + record names (Rider's Refactor → Rename
     handles ~95% of the fan-out).
   - Akka path: `/user/stage/bot/reactor` → `/user/stage/bot/loop`.
     Update `AgentBotActor.cs` actor.props line, `AgentReactorActorTests`
     path assertions, and any operator-visible logging that prints the path.
   - `AgentReactorActorTests.cs` → `AgentLoopActorTests.cs`
     (`AgentToolLoopTests.cs` → `AgentLoopTests.cs`,
     `ExternalAgentToolLoopTests.cs` → `ExternalAgentLoopTests.cs`).
3. **Update CLAUDE.md** actor topology block — replace the
   `AgentBotActor` / `AgentReactorActor` lines with the new pair.
4. **(Optional) Knowledge note** — the operator may want
   `harness/knowledge/_shared/agent-architecture.md` documenting the
   vocabulary so future contributors don't drift back. Code Coach can
   write this on request — see Mode 3 step 4.
5. **Verification gates** — `dotnet test Project/ZeroCommon.Tests` then
   `Project/AgentTest`. The latter exercises the Akka actor lifecycle and
   will catch path-string regressions.

## Evaluation (3-axis)

- **Code safety** — `B`. Pure rename, no semantics changed. Risk surface
  is the Akka path string and test-fixture literal expectations. Both
  catchable by the existing test suite.
- **Architecture fit** — `A`. Aligns the local vocabulary with the
  industry's, so external docs (Anthropic / LangChain) become directly
  applicable. Keeps the AgentBot gateway / AgentLoop runner separation
  visible in the type names instead of hidden in comments.
- **Testability** — `A`. Test files are part of the rename surface;
  no new test code needed beyond the rename. The four ZeroCommon.Tests
  files (`AgentReactorActorTests`, `AgentToolLoopTests`,
  `ExternalAgentToolLoopTests`, `NemotronProbeTests`) already pin the
  contract; rename keeps coverage intact.

## What to ask the operator before implementing

1. **Confirm Option B** vs A or C.
2. **Akka path** — keep `/bot/reactor` or rename to `/bot/loop`? Path is
   operator-visible in `Ping` responses and logs.
3. **Knowledge note?** Yes/no on writing
   `harness/knowledge/_shared/agent-architecture.md`.
