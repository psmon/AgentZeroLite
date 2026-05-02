# Unit-test trigger policy

> Status: binding (project-wide). Pinned in `memory/project_unit_test_policy.md`.
> Owner: `test-runner` agent.

## Rule

**Unit tests are never auto-triggered.** They run only when the user issues one of
the explicit triggers owned by `test-runner`:

| Trigger | Behavior |
|---|---|
| "연관된 유닛테스트 수행해" / "관련 유닛테스트 수행해" / "run related unit tests" | Scoped run — git-diff-driven `--filter` |
| "전체 유닛테스트 수행해" / "run all unit tests" | Full headless suite (and AgentTest if desktop) |
| "유닛테스트 이력" / "최근 유닛테스트 이력알려줘" / "test history" | Read-only — summarize recent test-runner logs |

Any other interaction (code edits, refactors, fixes, builds, releases, commits)
**does not** invoke `dotnet test`. The user asks; the agent runs. Period.

## Why

1. **LLM-backed smoke tests are slow.** Whisper / Gemma / Webnori roundtrip tests
   sit at minutes per run and saturate RAM with concurrent testhosts (see the
   12 GB incident in `harness/knowledge/dotnet-test-execution.md`). Running them
   reflexively after every change burns the user's machine for low marginal value.

2. **Compile errors are a separate problem from test regressions.** Build remains
   the auto-check after code changes — a fast, cheap signal that the code at
   least parses and links. Tests are the deeper, slower check the user reaches
   for deliberately.

3. **Logs become more useful when each run has a reason.** With auto-triggers
   stripped, every test-runner log has a clear "this is why I ran" — much
   better mining material for the next round of improvements than "this fired
   automatically as part of routine".

## What still runs automatically

| Activity | Auto? |
|---|:---:|
| `dotnet build` after code edits (catch compile errors) | yes |
| `dotnet test` (any project, any filter)               | **no** |
| `release-build-pipeline` security-guard step          | yes (release only) |
| `release-build-pipeline` build-doctor step            | yes (release only, build only) |
| `release-build-pipeline` test step                    | **removed** — does not exist anymore |
| `pre-commit-review` (code-coach Mode 2)               | yes (code review only, no tests) |

## Boundary cases

- **"이거 빌드되나?"** → `dotnet build`. No tests.
- **"빌드 점검해" / "release build"** → build-doctor. No tests.
- **"릴리즈 빌드 / 배포해"** → release-build-pipeline (security + build). **No tests** — if the user wants tests before tagging, they say so explicitly via the test-runner triggers.
- **"테스트 점검해" / "coverage check"** → test-sentinel. Structural audit only. **No execution.**
- **"코드 리뷰해줘" / pre-commit** → code-coach. No tests.
- **"버그 픽스했어"** → no tests by default. If the user wants to verify, they ask.

## Coordination with test-sentinel

`test-sentinel` is the structural auditor — it watches the headless-vs-WPF test
boundary, hunts for forbidden Win32 deps in `ZeroCommon`, and tracks coverage
hot spots on paper. It does **not** invoke `dotnet test` under this policy.
Its findings can cite test-runner's recent logs ("the suite has been green for
N runs") but Sentinel itself never executes.

## Override (if the user reverses the policy)

If the user later says something like "이제부터 코드 변경 후 자동 테스트 돌려",
update both:
1. `memory/project_unit_test_policy.md` — flip the rule.
2. This file — describe the new policy.
3. The relevant agents (build-doctor, test-sentinel, release-build-pipeline) —
   reinstate their `dotnet test` steps.

The default this document records is "explicit only" until the user revises it.
