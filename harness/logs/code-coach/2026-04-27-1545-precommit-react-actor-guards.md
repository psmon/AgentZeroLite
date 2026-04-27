---
date: 2026-04-27T15:45:00+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "코드 코치 리뷰 진행"
target: "TOP 1 — ReActActor guard 3-set + 5 Lite refinements"
---

# Pre-commit Review — ReActActor guard set (P0-1)

## Verdict

**OK to commit.** 0 must-fix, 0 should-fix, 5 advisory suggestions. All
unit tests pass (24/24 new, 70/70 regression). The 4-lens review surfaces
no cross-stack issues; the patch lands inside the ZeroCommon LLM tools
layer with zero spillover into actors, WPF, or native code.

## Change set

| File | Type | LOC | Concern |
|---|---|---:|---|
| `Project/ZeroCommon/Llm/Tools/ToolLoopGuards.cs` | new | ~180 | guard helper, transient classifier, GuardStats record |
| `Project/ZeroCommon/Llm/Tools/IAgentToolHost.cs` | edit | +7 | `AgentToolSession.GuardStats` init-only field, default `Empty` |
| `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs` | edit | +52/-3 | options +3, RunAsync guard insert, GuardStats in returns |
| `Project/ZeroCommon/Llm/Tools/ExternalAgentToolLoop.cs` | edit | +75/-15 | guard insert, `CallProviderWithRetryAsync`, history-aware block injection |
| `Project/ZeroCommon.Tests/ToolLoopGuardsTests.cs` | new | ~250 | 24 unit cases, no model dependency |

## Findings

### S-1 (Suggestion) — `ToolLoopGuards.cs:31-32` property pattern consistency

`BlockedRepeats` uses `{ get; private set; }` while `LlmRetries` and
`ConsecutiveBlocks` use backing field + expression-bodied get. All three
are externally read-only and modified through a single internal entry
point. Recommend the latter pattern for all three. Behavioral diff: 0.

### S-2 (Suggestion) — `ToolLoopGuards.cs:32` recent-buffer collection

`LinkedList<RecentAttempt>` chosen for O(1) `RemoveFirst()`. With
`RecentBufferSize = 5` the linked-list node overhead (~80 B/node)
outweighs the O(n=5) shift of `List<>.RemoveAt(0)` or the natural fit of
`Queue<>` (Enqueue/Dequeue). Micro-optimization scale; current code is
correct.

### S-3 (Suggestion) — `ToolLoopGuards.cs:118-129` switch-style transient classifier

Cleaner with .NET 8+ list pattern:

```csharp
if (ex is HttpRequestException { StatusCode: var sc }
    && sc is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout)
    return true;
```

Same behavior, less ceremony.

### S-4 (Suggestion) — false alarm

Initial concern about `Truncate` being instance-bound — it is already
`private static`. No action.

### S-5 (Suggestion) — `AgentToolLoop.cs:73-76` document session-scope guard

`AgentToolLoop` reuses one instance across multiple `RunAsync` calls
(via `UserSendCount`). A new `ToolLoopGuards` per call is *intentional* —
each user request starts with a clean repeat counter so a long-running
workspace session doesn't accumulate false positives across unrelated
tasks. The intent isn't obvious from the code alone; one comment line
saves future debugging.

```csharp
// New guards per RunAsync so each user request starts with a clean
// repeat counter — long-running sessions don't accumulate false
// positives across unrelated tasks.
var guards = new ToolLoopGuards();
```

### S-6 (Follow-up task) — integration test coverage

Current tests exercise `ToolLoopGuards` standalone. No end-to-end check
that `AgentToolLoop` + `MockAgentToolHost` triggers the guard under a
real-but-mocked LLM. Recommend adding 1–2 `[SkippableFact]` cases to
`AgentToolLoopTests`:

- 5 forced `read_terminal` calls → `session.FailureReason` contains "blocked"
- `session.GuardStats.BlockedRepeats > 0`

Out of scope for this PR; track as separate task.

## 4-lens cross-stack judgment

| Lens | Issues |
|---|---|
| .NET modern | None — record/init/pattern usage idiomatic, no preview-only feature abuse |
| Akka.NET | Untouched; `AgentReactorActor` continues to delegate to the loop, guard policy lives inside |
| WPF | Untouched (all changes in ZeroCommon); `OnTurnCompleted` callback signature preserved, blocked turns stream identically |
| LLM integration | Block message is English (R-1 compliant per `llm-prompt-conventions.md`); REST history correctly receives blocked toolResult so the model self-corrects on next turn |
| Windows native | Not applicable |

## Owned-convention check

- `harness/knowledge/llm-prompt-conventions.md` R-1: ✅ block messages
  written in English by default
- `harness/knowledge/agent-origin-reference.md`: ✅ implementation matches
  `Docs/agent-origin/03-#P0-1` (3-set adoption + 5 Lite refinements);
  reject section honored (no adaptive wait / no 5-state machine /
  no CompletionSignal)

## Test verification

```
ZeroCommon build         OK   (0 errors, 0 new warnings)
AgentZeroWpf build       OK   (0 errors, 7 pre-existing warnings, all
                                unrelated)
ToolLoopGuardsTests      OK   24/24 passed in 36 ms (CPU only)
Headless full suite      OK   70/70 passed in 1m 8s
```

## Rubric

| Axis | Score | Note |
|---|:---:|---|
| Cross-stack judgment | A | All 4 relevant lenses checked; .NET-modern + LLM-integration depth shown |
| Actionability | A | Every suggestion carries file:line + concrete rewrite |
| Research depth | n/a | Mode 2 review, not Mode 3 |
| Knowledge capture | pass | this log + GuardStats persistence path opens future tuning data |

## Recommendation

Three commit-paths, all valid:

1. `ignore and commit` — current code is production-ready
2. `apply S-5 only` — single comment-line addition, highest ROI per minute
3. `apply S-1 + S-3 + S-5` — clean up consistency, ~10 minutes, then re-run tests

Defer S-2 (no measurable benefit) and S-6 (track as follow-up task).

## Related

- Origin guard analysis: `harness/logs/tamer/2026-04-27-15-00-react-actor-guard-analysis.md`
- Implementation log: `harness/logs/tamer/2026-04-27-15-30-react-actor-guard-implementation.md`
- Adoption spec: `Docs/agent-origin/03-adoption-recommendations.md` §P0-1
