---
date: 2026-10-06T11:45:00+09:00
agent: agent-loop-auditor
type: review
mode: execution
command: "/harness-creator agent-one board-web 진행 멈춤 원인파악"
---

# agent-one hang — run_command never returns after a dev server outlives the kill

## 실행 요약

Live inspection, processes left running as the user asked.

- `agent-one.exe chat` pid 56604, started 10:48:14, workspace `C:\code\psmon\a1-test\board-web`
  (`~/.agent-one/workspaces/board-web-902c99b302/`).
- Session log `sessions/20261006-104814-chat.jsonl`, last entry 10:52:38:
  `safety · safe — asking you · confidence 0.23` (the next command was put to the person).
- No entry after that for 44 minutes. agent-one has **no child process left**.
- `python.exe app.py` pid 68000, created 10:53:37, parent pid 18828 — **18828 no longer exists**.
  Port 127.0.0.1:5000 is still listening (owner reported as the dead 18828; the socket lives on in 68000).

Timeline: files written (app.py + 3 templates) by 10:52:36 → model asks to run `python app.py`
→ person approves ~10:53:3x → Flask dev server (`debug=True`, Werkzeug reloader: parent 18828 +
child 68000) starts and never exits → 120 s `commandTimeoutSeconds` fires at ~10:55:37 →
`TryKill(entireProcessTree)` kills the shell and 18828, **68000 survives as an orphan** → hang.

## 결과

Root cause, `Project/AgentOne/Tools/ShellToolbelt.cs`:

```csharp
catch (OperationCanceledException)
{
    TryKill(process);
    if (ct.IsCancellationRequested) throw;
    return ToolResult.Failure($"killed after … output so far:\n{Clip(await SafeAsync(stdout))}");  // line 97
}
```

`stdout` is `ReadToEndAsync(ct)` — EOF arrives only when **every** holder of the pipe's write end
closes it. The orphaned reloader child inherited that handle, so EOF never comes and `ct` (the
turn token) is never cancelled → line 97 awaits forever. The same trap exists on the normal path
(lines 100–101): any background grandchild that keeps the handle blocks a command whose shell
exited cleanly.

Why 68000 escaped the tree kill is not proven (descendant enumeration races / reloader respawn
are candidates); the hang does not depend on it — any surviving descendant holding the pipe
produces it.

Secondary findings in the same turn:
1. Design pass failed (`qwen/qwen3.8-27b … streamed no content — building without a design`) and
   the turn continued silently without a plan.
2. A long-running server command (`python app.py`) was treated like any one-shot command — the
   only question asked was safety (0.23), not "this never ends; how do you want to run it?".
3. No watchdog over tool execution: the stall machinery (`ChatProviderStalledException` →
   Chooser) covers model streams only. A stuck tool shows nothing to the person.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | C | unbounded await after kill; orphan process keeps port 5000 |
| 아키텍처 정합성 | B | stall → Chooser pattern exists but is not applied to tools |
| 테스트 가능성 | B | reproducible with a command that backgrounds a child holding stdout |

## 다음 단계 제안

1. **Bounded drain** in ShellToolbelt: after exit or kill, wait ≤ 2 s for stdout/stderr, then
   return what was read (`[output pipe still held by a child process]`). Never await EOF unbounded.
2. **Kill what we started, all of it**: Windows Job Object (`KILL_ON_JOB_CLOSE`) around the shell;
   POSIX process group + `kill(-pgid)`. Test: command that spawns a detached child holding stdout.
3. **Long-running command strategy** (`CommandRisk`-style pattern: `python app.py` with
   `app.run`, `flask run`, `uvicorn`, `npm start|run dev`, `dotnet run|watch`, `vite`, `serve`…)
   → `Chooser` before start: ① start in background, wait for readiness (port / "Running on"),
   report URL, keep or stop ② short smoke run (N s) then stop ③ don't run — give the person the command.
4. **Tool watchdog**: no progress from a tool for X s → `Chooser`: keep waiting / stop this step /
   switch strategy (unattended: stop the step after the timeout + grace).
5. Design failure should be surfaced as a choice (retry / build without design), consistent with
   the stall policy.
