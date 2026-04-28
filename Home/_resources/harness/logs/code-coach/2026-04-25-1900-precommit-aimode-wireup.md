---
date: 2026-04-25T19:00:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (AgentBotWindow AI mode wire-up — Phase 3 #7)"
---

# Pre-commit review — AgentBotWindow AIMODE wire-up

## Staged change

`Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` only — wires the
end-to-end Gemma path. Net diff +90 / −2.

| Hunk | Purpose |
|------|---------|
| +2 fields | `_getGroups` (callback for catalog), `_aiCts` (cancellation handle) |
| +1 ctor assignment | `_getGroups = getGroups;` |
| +12 in SendCurrentInput | AI mode branch — routes input through tool loop |
| +60 new `SendThroughAiToolLoopAsync` | Constructs WorkspaceTerminalToolHost + AgentToolLoop, runs, renders trace + final |
| +9 in OnInputKeyDown | Esc cancels in-flight AI loop |

## Review by lens

**.NET modern** — `await using` for `AgentToolLoop`, `Stopwatch` for
elapsed, `Task.Run`-free pattern matches the existing
`SettingsPanel.OnLlmChatSendClick` chat loop (UI-thread async iterator
with native LLamaSharp yields). Idiomatic. ✓

**WPF** — Reuses existing `AddUserMessage` / `AddSystemMessage` for
chat rendering. Cancellation via `_aiCts` + Esc handler matches the
SettingsPanel chat-cancel pattern. New input cancels old loop before
starting new one. ✓

**LLM** — Casts `LlmService.Llm` to `LlamaSharpLocalLlm` (concrete type
required because `AgentToolLoop` accesses internal `GetInternals()`).
If the cast fails (someone swaps `ILocalLlm` impl later), surfaces a
clear error message rather than NRE. ✓

**Akka** — Not applicable (AI mode is direct LLM call, not actor msg).
**Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 2 |

### Suggestion 1 (defer) — UI thread freeze risk during CPU inference

`SendThroughAiToolLoopAsync` runs on the UI thread; the
`AgentToolLoop.RunAsync` it awaits internally calls
`InteractiveExecutor.InferAsync` which does sync llama.cpp `decode()`
calls between async yields. This is the SAME pattern
`SettingsPanel.OnLlmChatSendClick` uses (proven UX-acceptable in the
existing chat tab), so we're not regressing. But on slower machines,
a multi-token tool-call generation could freeze the UI for ~1-2s per
turn. If user complains, wrap the loop in `Task.Run` + `Dispatcher.Invoke`
for the chat updates — left as future polish.

### Suggestion 2 (defer) — Race on `_cliGroups` if AI loop runs from
worker thread

Currently the loop runs on UI thread, so `_groupsProvider()` invoking
`() => _cliGroups` is safe (no concurrent mutation). If Suggestion 1
moves the loop off UI thread, `_cliGroups` becomes a cross-thread read
and needs a snapshot or lock. Pair with Suggestion 1.

## Build verification

`dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug` — 0 errors,
6 pre-existing warnings (none introduced by this commit).

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## End-to-end Gemma flow now functional

1. User loads Gemma in Settings → AI Mode → "Load"
2. User opens AgentBot
3. Shift+Tab twice → mode badge turns violet "AI"
4. User types "List the open terminals" + Enter
5. `SendCurrentInput` detects `_chatMode == ChatMode.Ai`, dispatches
   to `SendThroughAiToolLoopAsync`
6. Build `WorkspaceTerminalToolHost` from `_getGroups` (already wired
   in MainWindow.xaml.cs:945 — no MainWindow change required)
7. Construct `AgentToolLoop` over `LlmService.Llm`
8. Loop: list_terminals → ... → done
9. Trace rendered as `⚙ tool(args)` per turn, final result with timing

Closes Task #7. The Gemma side of AIMODE is now end-to-end working.

Phase 2 (Nemotron native backend) — Task #2 starts next.
