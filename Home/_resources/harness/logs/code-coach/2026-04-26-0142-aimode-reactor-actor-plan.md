---
date: 2026-04-26T01:42+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "AgentBot AIMODE에 Reactor 액터 도입 — UI(상태관리)와 LLM 추론(상태기계) 분리 리팩토링 계획"
---

# AIMODE Reactor refactor — design plan

## Why

Today's AIMODE in AgentZeroLite is a UI-thread plain class (`AgentToolLoop`)
owned by `AgentBotWindow._aiLoop`. Symptoms this causes — three of which the
user already hit in the last hour:

- **No live progress.** UI fires `OnTurnCompleted` only after a *full* tool
  call JSON is parsed. While the model is generating (5–60s on CPU), the
  user sees only `💭 thinking…`. They can't tell "stuck" vs "slow".
- **State and concerns mixed.** `AgentBotWindow.OnAiSendAsync` does eight
  jobs: lazy-load LLM, pick template, clamp settings, instantiate loop,
  drive turns, render bubbles, manage cancellation, dispose. Any change
  to one breaks others (we already saw `MaxTokens` regression cascade
  into Gemma+CPU breaking).
- **No persistent execution context.** A future "wait for terminal DONE"
  pattern (which AgentWin already has) needs a long-lived addressable
  receiver of completion signals. A UI-thread Task can't be the target
  of a `Tell` from `TerminalActor`.

AgentWin's sister branch already solved the same shape with a persistent
Akka actor (`ReActActor` at `/user/stage/bot/react`) running a
Thinking/Acting/Waiting FSM. The architectural shape ports cleanly even
though the LLM mechanics differ — see "Don't port" section.

## Reference (AgentWin) — what to mimic, what to leave

`D:/Code/AI/AgentWin/Project/ZeroCommon/Actors/ReActActor.cs:1-767`

**Mimic** (architecture):
- Persistent child actor of Bot at `/user/stage/bot/react`.
- FSM via `Become()` — Idle → Thinking → Acting (→ Waiting) → Idle.
- Async-pump pattern: `_provider.CompleteAsync(...).PipeTo(Self)` returns
  internal record (`ThinkingDone` / `ThinkingFailed`) so the actor stays
  single-threaded and no `await` crosses message boundaries.
- Progress messages every phase change (`ReActProgress(Phase, Text, Round)`)
  forwarded to parent which calls UI delegates registered via
  `SetReActCallbacks(OnProgress, OnResult)`.
- Cancellation token stored on the actor; `CancelReAct` cancels and
  rebuilds idle.

**Don't port** (semantic mismatch):
- **`ILlmProvider` / `LlmRequest.Tools`.** AgentWin uses *native*
  tool-calling protocol — provider returns `LlmResponse.ToolCalls`.
  AgentZeroLite forces JSON via GBNF on the local `LlamaSharpLocalLlm`;
  one tool call per turn, no parallel tool invocation. Keep the
  `AgentToolLoop` *core* (grammar, parser, executor dispatch); just move
  its driver loop into the actor.
- **`Waiting` phase + `TerminalDoneSignal`.** AgentWin has terminals that
  emit `bot-signal done "msg" -From {name}` so the actor can pause for
  a peer terminal's reply. AgentZeroLite has no peer-DONE protocol yet.
  Skip Waiting phase in v1; design messages to allow it later.
- **`MaxRounds=10`, `MaxToolCallsPerResponse=5`, dialogue mode, etc.**
  All of those guard against AgentWin's network-LLM misbehaviours.
  AgentZeroLite's GBNF already constrains to one call per turn; reuse
  the existing `AgentToolLoopOptions.MaxIterations=8` cap.
- **ReAct system prompt.** Current `AgentToolGrammar.SystemPrompt`
  already teaches the send → read → decide pattern. Don't replace.

## Target shape

```
/user/stage                                          (existing)
    /bot                  AgentBotActor              (existing — unchanged)
        /reactor          AgentReactorActor          (NEW)
    /ws-{name}            WorkspaceActor             (existing)
        /term-{id}        TerminalActor              (existing)
```

Responsibilities after the move:

| Concern | Before | After |
|---------|--------|-------|
| AIMODE turn loop | `AgentToolLoop` plain class on UI task in `OnAiSendAsync` | `AgentReactorActor` instance method (one per loaded LLM lifecycle) |
| KV cache + `LLamaContext` | Held by `_aiLoop` in window field | Held inside actor (disposed on `ResetReactor` / actor `PostStop`) |
| Live progress visible to user | `OnTurnCompleted` per turn only | `ReactorProgress(Phase, ...)` per phase change — Thinking/Acting/Done |
| Cancellation | `_aiCts.Cancel()` from window | `Tell(CancelReactor)` from window |
| First-contact tracking | `AgentBotActor._introducedTerminals` | unchanged — stays in Bot |
| UI bubble rendering | `RenderToolTurnLive` + `AddBotMessage` | unchanged surface; Bot just calls registered callbacks |

## Files affected

### New
1. `Project/ZeroCommon/Actors/AgentReactorActor.cs` — the FSM actor.
2. `Project/ZeroCommon.Tests/AgentReactorActorTests.cs` — TestKit-based
   FSM behavior tests (no model load needed for state-machine tests;
   live-LLM tests gated on file existence like `T1G_*` already are).

### Modified
3. `Project/ZeroCommon/Actors/Messages.cs` — add `StartReactor`,
   `ReactorProgress`, `ReactorResult`, `CancelReactor`,
   `ResetReactorSession`, `SetReactorCallbacks`, `ReactorPhase`,
   `ReactorToolCallInfo` (records / enum).
4. `Project/ZeroCommon/Actors/AgentBotActor.cs` — add child-create + forward
   handlers. ~30 LOC of additions, no removals.
5. `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` —
   `OnAiSendAsync` becomes "Tell to bot, render via callbacks". Removes
   `_aiLoop`, `_aiCts`, `DisposeAiLoopAsync`, the inline loop body.
   Net delete: ~60 LOC; net add: ~40 LOC.
6. `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs` — keep, but expose two
   things the actor needs:
   - **NEW callback** `Action<string, int>? OnGenerationProgress` on
     options — fires on `Thinking` start and on every N=64 tokens
     during `_executor.InferAsync`. This is what fixes the user's "can't
     tell slow vs broken" complaint. Implementation just counts tokens
     in the streaming loop and pumps a callback every 64.
   - **NO actor coupling** — the loop stays a plain class so unit tests
     don't need an `ActorSystem`. The actor wraps it.

### Unchanged (deliberately)
- `AgentToolGrammar.cs` — system prompt, GBNF, tool surface.
- `ChatTemplates.cs` — Gemma / Llama31 specifics.
- `WorkspaceTerminalToolHost.cs` — already an `IAgentToolHost`;
  works whether driven by class or actor.
- `LlamaSharpLocalLlm` / `LlmService` — actor calls `LlmService.Llm`
  the same way the window does today.

## Phased plan

### Phase 1 — message protocol + per-token progress (no actor yet)

Smallest safe step. Adds the visible-progress fix without architecture change.

**Steps**:
- a. Add `OnGenerationProgress(string phase, int tokensSoFar)` to
  `AgentToolLoopOptions`. In `GenerateOneTurnAsync`, count tokens,
  fire every 64 tokens.
- b. Window subscribes: shows `💭 generating… (124 tok)` inline,
  updating the same system message instead of stacking. Kills the
  "is it stuck?" question.
- c. Headless test in `ZeroCommon.Tests`: mock executor, assert callback
  fires N times for stream of M tokens.

**Exit**: user can see live token count during inference. No actor yet,
no risk to other code paths.

### Phase 2 — `AgentReactorActor` skeleton + Idle→Thinking→Done FSM

**Steps**:
- a. Add new messages to `Messages.cs`. Mirror `ReActProgress` / `ReActResult`
  shape — `ReactorPhase = { Thinking, Acting, Done, Error }` (no Waiting
  yet — explicitly noted as future).
- b. Create `AgentReactorActor.cs`. Constructor takes `LlamaSharpLocalLlm`,
  `IAgentToolHost`, `ChatTemplate`, `AgentToolLoopOptions`. Owns one
  `AgentToolLoop` instance lazily (same lazy-create behaviour as today).
  States:
  - `Idle`: receives `StartReactor(userRequest)`, transitions to
    `Thinking`, kicks off `_aiLoop.RunAsync(...).PipeTo(Self)`.
  - `Thinking`: forwards `ReactorProgress(Thinking, "generating", tokens)`
    on each `OnGenerationProgress` callback (relay through `Self.Tell`
    so we stay single-threaded).
  - `Acting`: on `OnTurnCompleted`, sends `ReactorProgress(Acting, ...)`
    + `ReactorToolCallInfo` to parent, then back to `Thinking`.
  - On `RunAsync` complete: `ReactorResult(success, finalMsg, turns)`,
    Become `Idle`.
  - On `CancelReactor` in any state: cancel cts, become `Idle`.
- c. `AgentBotActor` gains:
  - `Receive<SetReactorCallbacks>` — store `Action<ReactorProgress>` and
    `Action<ReactorResult>` delegates.
  - `Receive<StartReactor>` — `EnsureReactorChild()` then forward.
  - `Receive<ReactorProgress>` and `Receive<ReactorResult>` — call the
    delegates on the dispatcher (UI marshals via `BeginInvoke` itself).
  - `Receive<ResetReactorSession>` — kill child via `Context.Stop`,
    null out the ref so next StartReactor recreates.
- d. WPF `OnAiSendAsync` rewrite (the heart of the change):
  - At `OnWindowLoaded`: `botActor.Tell(SetReactorCallbacks(progress => ..., result => ...))`.
  - On send: `botActor.Tell(StartReactor(userRequest))`. Done.
  - On New Session button: `botActor.Tell(ResetReactorSession)`.
  - Callbacks run `Dispatcher.BeginInvoke` to render the same bubbles
    `RenderToolTurnLive` / `AddBotMessage` already render.

**Exit**: same external behaviour as today + live progress + actor
isolation. Headless tests verify actor lifecycle without a model file.

### Phase 3 — defer "Waiting" + peer-DONE protocol

Out of scope for this refactor. Recorded as a follow-up note in
`harness/knowledge/aimode-reactor-design.md` (created by Phase 2 if
it has shelf life beyond the refactor).

## Test plan

| Test (new) | Project | What it asserts |
|------------|---------|-----------------|
| `OnGenerationProgressFires_AfterEvery64Tokens` | ZeroCommon.Tests | callback receives N tokens / 64 ≈ K calls (mocked stream) |
| `Reactor_StartReactor_TransitionsToThinking_ThenDone` | ZeroCommon.Tests (TestKit) | Idle → Thinking → Done sequence under a stub host |
| `Reactor_CancelReactor_FromThinking_ReturnsToIdle_Cleanly` | ZeroCommon.Tests (TestKit) | no leaked context, sends ReactorResult(success=false) |
| `Reactor_OnTurnCompleted_RelaysActingProgress_WithToolCallInfo` | ZeroCommon.Tests (TestKit) | ToolCallInfo carries tool name + parsed args + result |
| `Reactor_ResetReactorSession_DisposesContext` | ZeroCommon.Tests (TestKit) | actor lifetime → child stop → new ctx on next start |
| Existing `T1G/T2G/T3G/T4G` (Gemma) | ZeroCommon.Tests | unchanged, must still pass — they construct `AgentToolLoop` directly |

Live-model tests (T1N etc.) keep using `AgentToolLoop` directly — they
don't need the actor to validate the *core*. Actor is integration glue.

## Risks + mitigations

| Risk | Mitigation |
|------|-----------|
| Akka dispatcher reentrancy with `LLamaContext` (already a known
  pain point — see CLAUDE.md "Akka shutdown quirk") | Run inference on
  `TaskPool` via `PipeTo`, not on the actor's dispatcher thread. Same
  pattern AgentWin's ReActActor uses. |
| `LLamaContext` lifetime tied to actor — if actor crashes, context
  leaks | `PostStop` disposes `_aiLoop` (which disposes `LLamaContext`).
  Wrap in try/catch — the existing finalizer is the backstop. |
| Backwards compat — callers that construct `AgentToolLoop` directly
  (the unit tests) | Keep public ctor unchanged; the actor *uses* the
  loop, doesn't replace it. Tests stay green. |
| "New Session" button now needs to round-trip through the actor | Add a
  small ack message `ReactorReset` so the UI can show "session reset"
  only after the child actor has actually stopped. Avoids race on
  immediate next send. |

## Recommended next step

Start Phase 1 — it's a 30-LOC change with high visible value (kills the
"slow vs broken" question), zero architecture risk, and serves as a
forcing function for figuring out the streaming-progress contract that
Phase 2 will reuse.

Phase 2 follows once Phase 1 is on disk and the user has confirmed the
progress UI feels right. The actor protocol is shaped around what
Phase 1 already needs to emit.

## Follow-up — Mode 1 / Mode 2 prompt redesign (2026-04-26 ~02:00)

After Phase 1+2 landed, the user reported the bot routed casual greetings
("안녕") to a terminal as if the user had asked to relay. The system
prompt framed the bot as "your job is to relay between the user and
those terminal AIs" — so any user message looked like relay material.

**Fix**: rewrote `AgentToolGrammar.SystemPrompt` around two explicit modes:
- **Mode 1 — DIRECT ANSWER (default)**: greetings, smalltalk, questions
  about the bot itself → `done` ONCE with reply, no other tools.
  Examples enumerated in both English and Korean.
- **Mode 2 — TERMINAL RELAY**: only when the user EXPLICITLY uses
  trigger phrases listed in both languages ("send to terminal", "tell
  Claude"; "터미널에 보내", "전달해줘", "물어봐줘"...).
- "When in doubt → Mode 1" tiebreaker; explicit "NEVER send a casual
  greeting like '안녕' or 'hello' to a terminal" rule.

Stays English-spec per `harness/knowledge/llm-prompt-conventions.md` R-1
(Korean phrases are listed as recognition data, not prose).

**New test** `T5G_greeting_is_answered_directly_not_relayed_to_terminal_gemma`
runs three greetings ("안녕", "hello", "hi there") and asserts ≥ 2/3
end cleanly via `done` with no `send_to_terminal` turn. Confirmed
3/3 on Gemma 4 E4B + CPU. Existing T1G..T4G stay green — Mode 2
intents still route to terminals correctly.

## Follow-up — Mode 2 turn-2 `done` truncation (2026-04-26 ~02:30)

After the prompt redesign, Gemma + Vulkan handled greetings + Mode-1
direct answers fine. But once the user actually relayed to a terminal
(Mode 2), turn 2 — the final `done` summary — failed every time:

```
[02:25:28.999] options: maxTokens=256 (panel=256), backend=Vulkan, family=gemma
...
[02:26:30.935] result success=False turns=2 raw="{
  "tool": "done",
  "args": {
    "message": "Claude에게 '...'을 요청했고, Claude는 현재 다음과 같이 응답했습니다: '{\"
```

Two compounding causes:

1. **Per-turn cap of 256 too tight for `done` summaries**. The user's
   Settings panel had `MaxTokens=256` and the AIMODE clamp was
   `Math.Clamp(s.MaxTokens, 256, 1024)` — so the 256 panel value passed
   straight through. send_to_terminal / read_terminal envelopes fit in
   256 fine, but the trailing `done` packs JSON + a Korean narrative +
   often a quote of the terminal's reply, which blows past 256 and gets
   chopped mid-string. Grammar can't close the JSON, parser fails.

2. **Prompt invited verbatim quoting**. Nothing in the prompt told the
   model to *summarize* — so it produced messages like
   `"Claude는 현재 다음과 같이 응답했습니다: '{...long paste...}'"`
   which both bloats the token budget AND ships nested JSON that the
   grammar can't reliably escape.

**Fix (both at once)**:

- AIMODE per-turn floor `256 → 512` (`Math.Clamp(s.MaxTokens, 512, 1024)`).
  512 fits any realistic `done` summary in Korean; ceiling stays at 1024
  to keep the trailing-whitespace stall bounded. Log line now reports
  `(panel=X, AIMODE floor=512)` so the next time something looks wrong
  the diagnosis is one line away.
- Added `done` rules to the system prompt:
  - "Keep message SHORT — ideally 1 sentence, max 2."
  - "Do NOT paste terminal's raw output verbatim. Summarize."
  - "Do NOT embed nested JSON, code blocks, or escaped quotes."
  - Concrete bad/good examples enumerated.

**Verification**: Full focused suite (T1G..T5G + Reactor + parser +
sanitizer) — 20/20 pass. WPF build 0 errors. The `done` truncation
case isn't a separate live test yet (would need a real Claude terminal
to relay through); user-side smoke test confirms the fix's intent.

**Nemotron explicitly out of scope** per user direction this iteration —
it has its own function-call strategy issues (T0 probe shows no native
tool tokens on current llama.cpp, would need different prompt or model
bump). Tracked as separate work.

## Follow-up — `wait` tool + multi-turn relay protocol (2026-04-26 ~02:50)

After the prompt + token-cap fix, Gemma's `done` no longer truncated.
But the user reported the actual conversation didn't progress: a 5-turn
autonomous discussion request broke down on the very first reply.

Log evidence:

```
[02:37:20.471] send_to_terminal len=685   ← first-contact intro + payload
[02:37:24.365] result success=True turns=3 final="Claude가 현재 생각 중임을 알리는 응답을 보냈습니다."
                                                   ↑ "Claude is currently thinking"
```

Only **4 seconds** between send and final result. Claude (the terminal
AI) takes 5–15 seconds to start replying meaningfully. The bot read
immediately after sending, captured Claude's `Crafting…` indicator,
and called `done` reporting "Claude is thinking". User retried, same
result. The whole "5-turn autonomous discussion" never happened
because the bot ended after one half-completed exchange.

**Two compounding gaps**:

1. **No `wait` mechanism.** The bot could only chain `send_to_terminal`
   and `read_terminal` back-to-back. Reading too soon = thinking-only
   indicator = false-positive completion.
2. **Prompt let it call `done` on a thinking-state read.** "Call done
   EARLY" rule from the original prompt encouraged early termination.
   No guidance on detecting unfinished replies.

**Fix**:

- **New tool `wait`** with arg `{"seconds": 1..30}`. Pure time delay,
  no host involvement, clamped server-side. Added to GBNF, KnownTools,
  and `AgentToolLoop.ExecuteToolAsync`. Result echoes the actual
  clamped value so the next turn knows what it got.
- **Updated Mode 2 prompt to a 7-step pattern**: list → send → wait(5s)
  → read → if-thinking-indicator → wait+read up to 3 times → on third
  empty send a follow-up like "Are you still there?" → only then `done`.
- **Thinking indicators enumerated** (`"Crafting"`, `"Working"`,
  `"esc to interrupt"`, `"✻"`, `"✶"`, `"✺"`, lone `"..."`, empty).
  These are the actual visible markers in Claude CLI / Codex CLI.
- **Multi-turn discussion guidance**: "For autonomous N-turn
  discussion, plan ~4 tool calls per turn. A 5-turn conversation
  needs 15-20 tool calls — bump MaxIterations or accept truncation."
- **`MaxIterations` default 8 → 12**. Enough for ~3 round-trips with
  wait. Caller can override for longer discussions.
- **UI**: `RenderToolTurnLive` got a `wait` case → shows `⏳ waited Ns`
  as a compact system note instead of the generic `⚙ wait({...})`.

**Verification**: build 0 errors. Headless tests 15/15 pass (Reactor +
parser + sanitizer). Live model tests (T1G..T5G) still need a re-run
with the model present — the prompt got longer by ~250 tokens so
context use is up but still well under 2048.

**Outstanding**: this still doesn't implement the AgentWin-style
peer-DONE protocol (terminal → Stage → Bot via `bot-signal done`).
For that the terminal-side AI needs to know to emit a sentinel when
done. Documented as Phase 3 deferred. The current `wait` + retry
pattern is the polling alternative — works for any terminal AI that
doesn't know our protocol.

## Follow-up — Split AIMODE per-turn cap into its own setting (2026-04-26 ~03:10)

After landing the `wait` tool + multi-turn protocol, the user pointed
out that the per-turn token cap was still hardcoded
(`Math.Clamp(s.MaxTokens, 512, 1024)`) and asked for it to be a
first-class panel setting. Reasonable: TestBot's `MaxTokens` and
AIMODE's per-turn cap are *different concepts* and were sharing a
slot — every previous regression touched this exact confusion.

**Change**:

- New field `LlmRuntimeSettings.AgentToolLoopMaxTokens` (default 2048).
  Backwards-compat via System.Text.Json: missing key in existing
  `llm-settings.json` → property default → 2048.
- New panel input "AIMODE MaxTok" (purple highlight, Row 3 of the LLM
  config grid). Tooltip: "Per-turn token budget for the AIMODE
  tool-call loop. Default 2048."
- Existing "Max Tokens" relabelled to "Chat MaxTok" with a tooltip
  clarifying it's TestBot-only — same field, just naming the actual
  consumer so the user knows what they're tuning.
- `AgentBotWindow.OnAiSendAsync` now uses
  `Math.Max(256, s.AgentToolLoopMaxTokens)`. 256 floor stays as a
  guardrail for users who set the panel to a tiny value, but
  ceiling is gone — user is in charge.
- Log line updated: `panel.AgentLoop=N` instead of mixed
  `(panel=X, AIMODE floor=512)`.

**Why 2048 default**: bigger than the 1024 ceiling we previously had
(which the user saw exceeded). Fits any realistic `done` summary in
Korean + embedded JSON quotes. On Vulkan/CPU the trailing-whitespace
stall caps at ~10-20s if it ever triggers, which is acceptable for a
per-turn budget.

**Verification**: build 0 errors. Headless suite 15/15 pass.

## Follow-up — One-cycle-per-run + peer-signal protocol (2026-04-26 ~03:30)

User pinpointed the actual antipattern: trying to drive the entire 5-turn
discussion as ONE big tool chain run. They want each tool chain run to
be ONE conversational cycle, with subsequent cycles triggered externally
(user input OR a peer signal from the terminal). Reading terminal output
becomes the FALLBACK; receiving an explicit signal from the terminal is
PRIMARY.

User also requested: tests first, then implement. Gemma 4 only for now —
Nemotron deferred until the basics work.

**Three changes**:

1. **Prompt — "ONE CYCLE PER RUN"** (`AgentToolGrammar.SystemPrompt`).
   Replaced the "for 5-turn discussion plan ~20 tool calls" guidance
   with: "After ONE meaningful exchange, call done. The user (or a
   peer signal) will trigger the NEXT cycle. The KV cache preserves
   conversation history across runs."

2. **Peer-signal message protocol** (`Messages.cs`, additive only):
   - `TerminalSentToBot(g, t, text)` — terminal emitted a message
     addressed at AgentBot (sentinel format detected by Terminal-side
     scanner; that scanner lands in a follow-up commit).
   - `MarkConversationActive(g, t)` / `ClearConversationActive(g, t)` —
     Bot tracks per-(g,t) active conversations.
   - `QueryActiveConversations` (Ask) → `ActiveConversationsReply`.

3. **Bot routing** (`AgentBotActor`):
   - `_activeConversations` HashSet tracks active pairs.
   - On `TerminalSentToBot` for ACTIVE pair → synthesises a
     continuation `StartReactor` framed as `[peer signal from
     terminal g:t] {text}`. Reactor's existing KV cache provides
     conversation continuity.
   - On `TerminalSentToBot` for INACTIVE pair → logged + dropped
     (peer signals from un-asked-for terminals are noise).
   - `ResetReactorSession` now also clears active conversations.

**Tests** (TDD per user request):

- TestKit (headless, 5 new): `MarkConversationActive` + Query, Clear,
  TerminalSentToBot routing for inactive (no reactor wake) vs active
  (reactor wakes — verified via the no-LLM friendly-failure path),
  ResetReactorSession clearing both intros and conversations.
- Live (Gemma 4): **T6G_five_continuation_cycles_each_stay_short_gemma**.
  Runs 5 sequential `RunAsync` calls on ONE shared loop with a
  `ScriptedTerminalHost` that yields canned Claude replies. Asserts
  every cycle ends cleanly AND uses ≤ 6 iterations. Old behaviour
  (pre-prompt-change) would chain 7-12+ iterations trying to do
  everything in one cycle.

**Verification**: 39/39 headless pass, 5/5 existing live Gemma tests
(T1G..T5G) still pass — no regression. T6G live: 5/5 cycles short and
clean.

**Deferred (next iteration)**: TerminalActor-side scanner that watches
ConPTY output for the bot-signal sentinel and emits `TerminalSentToBot`.
For testing purposes the message can be sent manually (or via a CLI
command); production wiring needs the scanner. Also: update the
first-contact intro template so the receiving terminal AI knows to
prefix its replies with the sentinel.

## Follow-up — Anti-passivity rebalance (2026-04-26 ~04:00)

The "ONE CYCLE PER RUN" rule from the previous follow-up worked too
well in one direction: the model became so cautious it stopped sending
altogether. Live evidence in app-log session 2 (03:26–03:29):

| Time | User intent (inferred) | Bot did | turns | Issue |
|------|------------------------|---------|-------|-------|
| 03:27:28 | relay something | done "I cannot talk to terminal AI directly..." | **0** | Refused + lied about own tools |
| 03:28:14 | relay attempt | list_terminals → done (no send) | 1 | Info-only, no send |
| 03:28:45 | "토론 시작해" | done "give me a topic first" | **0** | Bounced back at user |

**Root cause**: prompt over-emphasized "user will trigger next cycle"
and "primary failure mode is doing too much". The model interpreted
this as "passivity is safe, action is dangerous", and started
routinely calling done with NO tool calls — sometimes even denying it
had `send_to_terminal` ("I cannot talk to terminal AI directly").

**Three balanced fixes**:

1. **Broader Mode 2 trigger**. Bar lowered: naming any terminal AI
   (Claude, Codex, gpt) → Mode 2, even if topic vague. Korean phrase
   list expanded ("Claude한테 X해", "Claude랑 이야기", "토론 시작해",
   "대화 시작해", etc.).
2. **Reasonable-default rule**. For vague topics, pick a sensible
   opener and SEND. Concrete examples in prompt (English + Korean) so
   the model has a template instead of bouncing back.
3. **Anti-denial rule (CRITICAL)**. Prompt explicitly forbids the
   model from claiming "I cannot talk to terminal AI directly" or
   variants. Statements like that describe a non-existent limitation
   — the model DOES have `send_to_terminal`.

Also rebalanced the ONE-CYCLE rule: now reads "ONE CYCLE PER RUN, BUT
DO THE CYCLE", with both failure modes named symmetrically — chaining
too much AND bouncing without action. Aim is the middle: one complete
round trip per run.

**New live test T7G** (Gemma 4): three vague Mode 2 prompts ("Claude랑
자유롭게 이야기해봐", "Claude한테 토론 시작해줘", "talk to Claude
about anything") — assert ≥ 2/3 actually trigger `send_to_terminal`.

**Verification**:
- Headless 39/39 pass.
- Live Gemma 4: T1G..T4G (4/4 regression), T5G (Mode 1 greeting), T6G
  (5-cycle continuation, each ≤ 6 iterations), T7G (vague Mode 2
  sends). All green.

The pendulum should now be balanced: short cycles per run AND
proactive within each cycle.

## Evaluation (Code Coach Mode 3 rubric)

| Axis | Result |
|------|--------|
| Cross-stack judgment | A — touches Akka topology, LLamaSharp lifetime, GBNF semantics, WPF dispatcher. Each constraint named. |
| Actionability | A — file paths, message names, phased commits, exit criteria, test list. |
| Research depth | A — explicit "mimic vs don't port" table comparing both branches; rejected ports justified. |
| Knowledge capture | Pass — plan saved here; if Phase 2 lands, it gets a permanent design note in `harness/knowledge/`. |
