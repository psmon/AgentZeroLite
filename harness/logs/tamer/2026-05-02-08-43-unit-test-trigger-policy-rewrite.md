---
date: 2026-05-02T08:43:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "유닛테스트 빈도를 조절하려함 ... 모든 활동에 자동 유닛테스트 작동은 제거 ... 관련 스킬및 하네스 업데이트"
harness_version_before: 1.1.7
harness_version_after: 1.2.0
---

# Unit-test trigger policy rewrite — auto-runs removed, test-runner agent introduced

## Why this change
LLM-backed smoke tests (Whisper, Gemma, Webnori) are slow and RAM-heavy.
Auto-running them after every code change / commit / build / release was
burning the user's machine (see the 12 GB testhost incident in
`harness/knowledge/dotnet-test-execution.md`) for marginal signal.

The user requested:
1. No automatic `dotnet test` after any activity (code edit, commit, build,
   release) — explicit-only.
2. Two execution triggers: "연관된 유닛테스트 수행해" (scope-limited via
   `--filter`) and "전체 유닛테스트 수행해" (full suite).
3. A separate log stream for test runs that records *why* each run happened,
   so future improvement passes can mine that history.
4. A history-query trigger ("유닛테스트 이력") to surface the most recent
   runs without invoking dotnet at all.
5. Strip the test step from the release pipeline.
6. Keep `dotnet build` auto-running as before — compile-error catching is
   fast and cheap.

## Result

### Files added
- `harness/agents/test-runner.md` — new agent owning all `dotnet test`
  invocation. Three modes (scoped / full / history). No auto-trigger.
- `harness/knowledge/unit-test-policy.md` — binding rule, lists every
  user activity and whether it triggers tests (none auto-trigger).
- `harness/docs/v1.2.0.md` — release note for this minor bump.
- `memory/project_unit_test_policy.md` (in user-scope `.claude/projects/`
  memory) + `MEMORY.md` index entry — cross-session pin.

### Files modified
- `harness/agents/build-doctor.md` — removed `dotnet test` steps; build
  audit only. Rubric updated.
- `harness/agents/test-sentinel.md` — repositioned as structural-only
  auditor. Cites recent `harness/logs/test-runner/*.md` instead of
  executing. Still owns `dotnet-test-execution.md` canon for the
  test-runner to follow.
- `harness/engine/release-build-pipeline.md` — Step 2 (build-doctor)
  no longer runs `dotnet test`. Security gate + build only.
- `harness/harness.config.json` — version 1.1.7 → 1.2.0;
  agents += `test-runner`; `lastUpdated` 2026-05-02.

### Behavior diff

| Activity | Before | After |
|---|---|---|
| Code edit + build verification | `dotnet build` runs | `dotnet build` runs (unchanged) |
| Code edit (test verification) | sometimes auto | **never auto** — must ask |
| `git commit` | code-coach review + sometimes test | code-coach review only (no tests) |
| `release-build-pipeline` | security + build + test | security + build (no test) |
| `test-sentinel` triggered | runs full suite + structural audit | structural audit only |
| `build-doctor` triggered | build + tests | build only |
| New triggers | n/a | "연관된 유닛테스트 수행해", "전체 유닛테스트 수행해", "유닛테스트 이력" |

## Evaluation (tamer 3-axis rubric)

| Axis | Result | Note |
|---|---|---|
| Workflow improvement | A | Removes a real RAM/time tax; replaces it with explicit, traceable triggers. |
| Claude skill usage | 4/5 | The `agent-zero-build` skill no longer carries an implicit test step (release pipeline removed it); the test-runner agent uses standard `Bash`/`Read`/`Grep` only — no new skill dependency. |
| Harness maturity | L4 → L4 | Adds a new agent with full role/log/eval contract; promotes a shared knowledge doc to convention status. Did not need to add an engine; the existing `release-build-pipeline` engine was edited rather than duplicated. |

## Risks / next-step proposals

1. **Stale-suite drift** — with no auto-runs, the suite can rot
   undetected. Mitigation: `test-sentinel` flags when the latest
   `harness/logs/test-runner/*.md` is > 7 days old. Acceptable for
   a project where the user is the gate.
2. **Ambiguous scope on shared changes** — when a `ZeroCommon`
   helper used by both test projects changes, "연관된 유닛테스트
   수행해" may need to ask before running. The agent procedure
   already allows this fallback.
3. **Release-time blind spot** — the user can ship without ever
   running tests. By choice. If a regression makes it through, the
   feedback loop is post-release; consider whether to add an
   advisory "마지막 전체 테스트는 N일 전입니다" line in the
   release pipeline log without re-introducing auto-execution.

## Files touched

```
harness/agents/build-doctor.md       (modified)
harness/agents/test-runner.md        (new)
harness/agents/test-sentinel.md      (modified)
harness/docs/v1.2.0.md               (new)
harness/engine/release-build-pipeline.md  (modified)
harness/harness.config.json          (modified — v1.2.0)
harness/knowledge/unit-test-policy.md     (new)
memory/MEMORY.md                     (modified — index entry added)
memory/project_unit_test_policy.md   (new)
```
