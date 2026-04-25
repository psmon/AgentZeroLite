---
date: 2026-04-25T18:45:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (WorkspaceTerminalToolHost — Phase 3 #6)"
---

# Pre-commit review — WorkspaceTerminalToolHost

## Staged change

`Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs` — new
production implementation of `IAgentToolHost` (the interface lives in
`Agent.Common.Llm.Tools`). Bridges the on-device tool loop to the live
workspace + ConPTY topology.

## Review by lens

**.NET modern** — Constructor injection of a `Func<IReadOnlyList<CliGroupInfo>>`
provider (read-only, no live binding) keeps the host loosely coupled to
MainWindow. switch expression for the key map. Idiomatic. ✓

**WPF** — Reuses `CliTerminalIpcHelper.TryResolveSession` and
`CliTerminalIpcHelper.BuildTerminalListJson` — the **exact same helpers**
the `WM_COPYDATA` CLI handlers use. That's the contract guarantee:
a `terminal-send` over CLI and a `send_to_terminal` from Gemma reach
the terminal in the exact same way. ✓

**LLM** — Implements `IAgentToolHost` correctly. All four async methods
exist and dispatch properly. The host is mockable via the same interface
(test doubles in `MockAgentToolHost`). No new LLM dependencies. ✓

**Akka** — Not applicable.
**Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 1 |

### Suggestion (defer)

`MapKeyName` duplicates the same switch in `MainWindow.HandleTerminalKey`
(lines 583–600). Two copies = drift risk. When time permits, extract a
shared `TerminalKeyMap.GetSequence(string keyName)` static method that
both call sites use. For now the comment in `WorkspaceTerminalToolHost`
flags the duplication explicitly so any maintainer modifying one will
see the reference. Defer to Phase 3 cleanup pass.

## Build verification

`dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug` — 0 errors,
6 pre-existing warnings (none introduced by this commit).

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## Next step

Phase 3 #7 — wire `AgentBotWindow.SendCurrentInput` to construct an
`AgentToolLoop` over this host when `_chatMode == ChatMode.Ai`. That
closes the AIMODE feature loop end-to-end with the Gemma backend
(Nemotron native backend lands separately as #2 + #3).
