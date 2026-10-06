---
date: 2026-10-06T12:10:00+09:00
agent: agent-loop-auditor
type: improvement
mode: execution
command: "/harness-creator 자식 프로세스 관리 액터 + 옵저버/재확인 패턴으로 보강"
---

# agent-one — process sub-agent (actor-owned commands, observer + check-in)

Follow-up to `2026-10-06-11-45-run-command-hang-flask-server.md`.

## 실행 요약

Strategy from the person: tool execution goes to a child actor that manages it; the
lifecycle (one-shot / long-running / ended) is managed separately so the main turn never
stalls; the parent is notified on completion (observer) and re-checks when it takes long.

Built (`Project/AgentOne/Processes/`):

| Piece | Role |
|---|---|
| `ProcessSupervisorActor` | `/user/bot/loop/procs` — ids, routing, relays lifecycle changes to subscribers, keeps 20 ended |
| `ProcessActor` | `proc-pN` — owns one OS process; chunked reads; Running → Ready → Exited/Killed/Failed; exit is the end, pipes get 1 s |
| `ProcessSupervisor` | facade for the turn: `WaitAsync` = observer signal **or** check-in with a fresh snapshot |
| `CommandLifetime` | detects servers/watchers (incl. `python app.py` whose file has `app.run(`), reads readiness + URL from output |
| `ProcessTree` / `JobObject` | descendant snapshot first, then job terminate, then each pid |

`ShellToolbelt`: one-shot → check-in every 15 s (activity line) → at the timeout asks
wait / background / stop (unattended: stop). Service → asks background / smoke / skip before
starting, returns once ready with the URL. New verbs `process_status`, `process_stop`.
`ChatSession` routes the choices through `Chooser` and background ready/end into `Noted`.

## 결과

- Tests: `ProcessSupervisorTests` (13) — pipe-holding child no longer hangs, overrun goes to
  the chooser, background/stop/status, service skip/background/smoke, dispose kills, grandchild
  kill, detection. Full suite **662/662**.
- Real repro (temporary test, removed): Flask `debug=True` via `python app.py` → asked, started
  in background, ready at `http://127.0.0.1:5057` in ~2 s, GET ok, `process_stop` → port closed,
  no python left.
- The first repro run **found a second gap**: the Python install manager's `python.exe` alias
  breaks away from the Job Object, and after the job killed the shell, .NET's tree kill could
  no longer find the grandchildren (dead parent's start time). Three pythons survived and held
  `dotnet test`'s pipe — the original hang, reproduced one level up. Fixed with `ProcessTree`.
- Native AOT win-x64 publish: no warnings from our code; `session selftest` ok.
- The stalled session (pid 56604) and its orphan python 68000 were left running, as asked.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | B+ | no unbounded await left on the command path; tree kill verified on a breakaway case |
| 아키텍처 정합성 | A | processes are actors under the loop, same Bot/Loop topology rules; turn never owns a process |
| 테스트 가능성 | A | deterministic tests on both shells; real Flask repro done once by hand |

## 다음 단계 제안

1. Handle inheritance: `Process.Start` with redirection still lets a started process inherit
   agent-one's own inheritable handles (the `DetachedProcess` lesson). The tree kill makes the
   leak short-lived; a CreateProcessW launcher with an explicit handle list would remove it.
2. A crash of agent-one does not reach breakaway processes (the job cannot hold them) —
   persist service pids in the workspace and sweep them on the next start.
3. `/ps` in the chat window and `process` lines in `/status` for the person (the model already has `process_status`).
4. The design-pass failure (`streamed no content`) still continues silently — put it to the Chooser like a stall.

## 실사용 검증 (2026-10-06 16:05)

Same board-web session, resumed on the new build (`20261006-104814-chat.jsonl`):
"브라우저에서 사용해보고싶은데 구동해줄래?" → `python app.py` detected as a service →
`process-choice: background` → `process p1 is up at http://127.0.0.1:5000` (~2 s after the
choice) → the turn ended normally → the person used the board in a browser → "종료해죠" →
`process_stop p1` → `process p1 was stopped`. No python left afterwards. The case is closed.
