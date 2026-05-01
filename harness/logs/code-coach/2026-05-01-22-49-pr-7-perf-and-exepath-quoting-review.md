---
date: 2026-05-01T22:49:00+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "코드검토부터...진행"
pr: https://github.com/psmon/AgentZeroLite/pull/7
pr_branch: jgkim999:feature/optimize
commits: [02853b2, 04baa6f]
---

# PR #7 review — perf: reduce allocations + fix ExePath quoting for spaces

## Execution summary

External contributor PR (jgkim999) bundles two unrelated changes:

1. **Allocation/perf series** following the author's own `Docs/optimization-plan.md`:
   - `LlmRequest.Messages`: `List<T>` → `IReadOnlyList<T>`, callsites switch `_history.ToList()` → `_history.AsReadOnly()`.
   - `VoiceSegmenterFlow`: `List<byte>` → `Microsoft.IO.RecyclableMemoryStream` pooled buffer + `PostStop()` cleanup.
   - `OpenAiCompatibleProvider.BuildRequestBody`: drop intermediate `.ToList()` on the `messages` projection.
   - `CliWorkspacePersistence.LoadCliGroups/LoadCliDefinitions`: `AsNoTracking()`.
   - `ConPtyTerminalSession.Dispose()`: reorder timer cleanup to Cancel → Change(Infinite) → Dispose.
   - `AppLogger.WriteToFile`: `Encoding.UTF8.GetByteCount(entry)` → `entry.Length`.
   - `AgentBotActor`: `_activeConversations.ToList()` → pass `HashSet` directly; `ActiveConversationsReply.Active` narrowed `IReadOnlyList<string>` → `IReadOnlyCollection<string>`.

2. **Bug fix**: `MainWindow.InitializeTerminal` quotes `ExePath` when it contains spaces, so `cmd /c` doesn't parse only `C:\Program` as the executable name.

3. **Tests**: 10 new xunit tests under `OptimizationTests.cs` and `VoiceSegmenterStageTests.cs`.

Files inspected in full (not just diff context):
- `Project/ZeroCommon/Voice/Streams/VoiceSegmenterFlow.cs` (main + PR variant)
- `Project/ZeroCommon/Actors/AgentBotActor.cs` lines 40–210
- `Project/ZeroCommon/Actors/Messages.cs` line 271
- `Project/ZeroCommon/AppLogger.cs` lines 120–170
- `Project/AgentZeroWpf/Services/ConPtyTerminalSession.cs` lines 390–425
- `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs` lines 1825–1860
- `Project/ZeroCommon.Tests/OptimizationTests.cs` (PR-only)
- `Project/ZeroCommon.Tests/AgentReactorActorTests.cs` lines 185–270 (existing usage of `ActiveConversationsReply`)
- All callsites of `LlmRequest`, `QueryActiveConversations`, `ActiveConversationsReply`.

## Findings

### Must-fix

#### M1. `AgentBotActor` leaks live `HashSet<string>` across actor boundary

**File**: `Project/ZeroCommon/Actors/AgentBotActor.cs:173`

```csharp
// PR
Sender.Tell(new ActiveConversationsReply(_activeConversations));

// Suggested rewrite
Sender.Tell(new ActiveConversationsReply([.. _activeConversations]));
```

**Why this is must-fix.** `_activeConversations` is a `HashSet<string>` that is only safe
to read on the AgentBotActor's own dispatcher thread. Akka's hard rule is "messages
must be immutable / not shared mutable state across actors." Once the bare `HashSet`
reference is delivered via `Sender.Tell`, the receiving actor (or an Ask-pattern
caller's continuation, which can hop threads) is free to enumerate it. If the bot
processes a `MarkConversationActive` / `ClearConversationActive` / `ResetReactorSession`
message in between, the receiver gets `InvalidOperationException: Collection was modified
during enumeration` — or worse, undefined results from `HashSet.Contains` mid-rehash.

The previous `_activeConversations.ToList()` was a defensive snapshot precisely to
maintain that invariant. The set typically holds 1–3 entries (one per
connected peer), so the perf upside is essentially nothing while the correctness
regression is real.

`[.. _activeConversations]` (collection-expression spread → `string[]`) keeps the
allocation-cheap-and-immutable shape the optimization plan claimed to want, without
breaking the actor invariant.

### Should-fix

#### S1. `ActiveConversationsReply.Active` narrowed from `IReadOnlyList<string>` to `IReadOnlyCollection<string>`

**File**: `Project/ZeroCommon/Actors/Messages.cs:271`

```csharp
// PR
public sealed record ActiveConversationsReply(IReadOnlyCollection<string> Active);

// Suggested rewrite
public sealed record ActiveConversationsReply(IReadOnlyList<string> Active);
```

**Why.** This is a public actor message contract. `IReadOnlyList<T>` provides indexer
access (`reply.Active[0]`) and stable iteration order; `IReadOnlyCollection<T>` only
exposes `Count` and `IEnumerable`. Existing tests happen not to use the indexer, so
they still pass — but any downstream consumer that does (or future code that wants
to compare with the previous reply by index) silently breaks. The narrowing isn't
required for the perf optimization once M1 is applied — `[.. _activeConversations]`
returns `string[]`, which already implements `IReadOnlyList<string>`.

#### S2. `ExternalChatSession.Messages = _history.AsReadOnly()` exposes a live view of mutable history

**File**: `Project/ZeroCommon/Llm/ExternalChatSession.cs:55`

```csharp
// PR
Messages = _history.AsReadOnly(),

// Suggested rewrite — restore defensive snapshot
Messages = _history.ToArray(),
```

**Why.** `List<T>.AsReadOnly()` returns a `ReadOnlyCollection<T>` *wrapper* over the
same backing list. The previous `_history.ToList()` was a snapshot. The PR even
ships a test pinning this regression as the new contract:

```csharp
// OptimizationTests.cs
[Fact]
public void LlmRequest_Messages_is_not_mutated_through_readonly_wrapper()
{
    var history = new List<LlmMessage> { LlmMessage.User("a") };
    var request = new LlmRequest { Messages = history.AsReadOnly() };

    history.Add(LlmMessage.User("b"));

    Assert.Equal(2, request.Messages.Count);  // ← demonstrates mutation IS visible
}
```

The test name says "is_not_mutated" but the assertion proves the wrapper *is*
mutated when the underlying list changes. That mismatch makes me think the author
meant to demonstrate "you can't mutate through the wrapper" (true), but the test
also locks in "external mutation is reflected" (the semantic regression).

In `OpenAiCompatibleProvider.BuildRequestBody` the body is constructed
synchronously and serialized synchronously inside `CompleteAsync` *before* the
first `await`, so within a single in-flight request there's no race. But if a
session is fanned out (rare, but the architecture does not forbid it), or if a
future caller adds another `await` between `BuildRequestBody` and the actual
`HttpClient.SendAsync`, the wire payload would silently include later-appended
turns.

The same applies to `ExternalAgentToolLoop.cs:202`. Symptom would be: re-entrant
tool loop sends the most recent `_messages` snapshot rather than the historical
one, which violates the contract that each turn captures a frozen view.

`_history.ToArray()` keeps the perf shape (single allocation, no `.AsReadOnly`
wrapper indirection) and restores the snapshot semantic. The 4 collection-construction
tests in `OptimizationTests.cs` should be updated accordingly (and the misleading
test name fixed).

### Suggestion

#### Sg1. `AppLogger` size estimate undercounts UTF-8 by ~3× for CJK content

**File**: `Project/ZeroCommon/AppLogger.cs:138`

```csharp
// PR
_fileSizeEstimate += entry.Length + 2;

// Suggested rewrite
_fileSizeEstimate += entry.Length * 3 + 2;   // conservative CJK-aware bound
```

**Why.** `string.Length` is UTF-16 code-unit count. For CJK characters (BMP), one
char encodes to 3 UTF-8 bytes. The optimization plan claims "대부분의 로그가 ASCII
위주" but this codebase's logs frequently contain Korean (project comments are
Korean, error paths echo user-facing Korean strings). With Korean-heavy logs and
the default `MaxLogBytes`, rotation now triggers at ~3× the configured threshold.
Multiply by 3 (or restore `GetByteCount` — the cost on a single short log line is
negligible) so the bound is conservative rather than optimistic.

The PR's own test exercises this:

```csharp
AppLogger.Log("한글 테스트 메시지 日本語テスト 中文测试");
```

— under the new estimator, this entry contributes 25 to `_fileSizeEstimate`; under
UTF-8 it would be ~70 bytes. The test only asserts no crash, not the size estimate
fidelity, so the regression slips past CI.

#### Sg2. `VoiceSegmenterFlow._buffer` could stay typed `MemoryStream?` (LSP)

**File**: `Project/ZeroCommon/Voice/Streams/VoiceSegmenterFlow.cs:63, 99`

```csharp
// PR
private RecyclableMemoryStream? _buffer;
...
_buffer = (RecyclableMemoryStream)PoolManager.GetStream("VoiceSegmenter", capacityHint);

// Suggested rewrite
private MemoryStream? _buffer;
...
_buffer = PoolManager.GetStream("VoiceSegmenter", capacityHint);   // no cast
```

**Why.** No `RecyclableMemoryStream`-specific API is used (`Write`, `GetBuffer`,
`Length`, `Dispose` all live on `MemoryStream`). `RecyclableMemoryStreamManager.GetStream`
returns `MemoryStream` precisely so callers don't couple to the derived type. The
explicit cast on line 99 is doing nothing except advertising the implementation
detail.

The pooling behavior (return-to-manager on `Dispose`) is preserved either way —
`Dispose` is virtual and the runtime type is still `RecyclableMemoryStream`.

## Verdict

| Severity | Count | Status |
|---|:---:|---|
| Must-fix | 1 | **commit blocked** until applied or explicitly waived |
| Should-fix | 2 | merge requires a referencing tracking issue |
| Suggestion | 2 | non-blocking |

The bug fix in commit `04baa6f` (ExePath quoting) is independently valuable and
correct as written — it could ship on its own. The perf series in `02853b2` lands
real wins in the LLM/Voice hot paths but ships **two correctness regressions**
(M1, S2) and **one contract narrowing** (S1) that all stem from the same
"`.ToList()` is allocation-bad → strip it" instinct without weighing what the
defensive copy was buying in each spot.

## Recommendation

1. Ask the contributor to address M1, S1, S2 in a follow-up commit on the same
   branch.
2. Sg1 and Sg2 can land in this PR or a follow-up — author's call.
3. Optionally split `04baa6f` (ExePath quoting) into its own PR so it can merge
   immediately while the perf work iterates.

## Evaluation (code-coach rubric)

| Axis | Result |
|---|---|
| Cross-stack judgment (≥ 2 of 4 lenses) | A — findings span Akka.NET (M1, S1), .NET (S2 immutability semantics), Win32 ConPTY (verified, no finding), and on-device LLM session lifecycle (S2 effect). |
| Actionability | A — every finding names file:line and ships a concrete rewrite snippet. |
| Knowledge capture | Pass — the `.ToList()` vs `.AsReadOnly()` vs `.ToArray()` distinction in actor messaging is worth folding into a knowledge note (see Next steps). |
| Issue handoff | Pending — issue to be filed alongside this log. |

## Next steps

- File a single GitHub issue listing M1 + S1 + S2 + Sg1 + Sg2, link this log,
  label `bug` (Must-fix present).
- Consider promoting the actor-message-immutability rule into
  `harness/knowledge/akka-message-immutability.md` so the next contributor doesn't
  hit the same trap when "optimizing" a `.ToList()` away.
