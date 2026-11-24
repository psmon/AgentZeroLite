---
date: 2026-11-24T08:16:53Z
agent: code-coach
type: review
mode: precommit
trigger: "auto: staged diff includes *.cs"
---

# Peer relay (Codex × Hermes through AgentBot) — auto pre-commit review

## Staged scope

```
modified:   Project/ZeroCommon/Actors/{AgentBotActor,AgentLoopActor}.cs
modified:   Project/ZeroCommon/Llm/Tools/{AgentLoopGuards,AgentToolGrammar,ExternalAgentLoop,LocalAgentLoop}.cs
modified:   Project/ZeroCommon/Services/XtermTerminalSession.cs
new file:   Project/ZeroCommon/Actors/{PeerEnvelope,PeerHandshake}.cs
new file:   Project/ZeroCommon/Agents/PeerCliFault.cs
new file:   Project/ZeroCommon/Llm/Tools/PeerSignalInbox.cs
new file:   Project/ZeroCommon/Services/{TerminalSubmit,TerminalScreenText}.cs
new file:   Project/ZeroCommon.Tests/PeerRelayTests.cs
modified:   Project/AgentZeroAvalonia/{App.axaml.cs,Cli/*,Services/WorkspaceToolHost.cs,ViewModels/AgentBotViewModel.cs}
modified:   Project/AgentZeroWpf/{Services/WebViewXtermTerminalSession.cs,Services/WorkspaceTerminalToolHost.cs,UI/APP/MainWindow.xaml.cs}
modified:   CLAUDE.md, .claude/skills/agentzero-cli/*
```

## Cross-stack lens scan

| Lens | Pass / flag | Notes |
|------|-------------|-------|
| .NET modern | Pass | `PeerSignalInbox` is a lock + one TCS (`RunContinuationsAsynchronously`); the wait's timer is cancelled on arrival; cancellation throws rather than reporting silence. `PeerEnvelope` uses a generated regex. |
| Akka.NET | Pass | `_loopRunning` is touched only in the bot's mailbox. Exactly one `AgentLoopResult` per `StartAgentLoop` is kept: a signal taken by `wait` does not also start a cycle (test), an untaken one starts one continuation after a run that ended well. Reset clears the inbox and the flag, since a stopped child may never report. |
| WPF | Flag → fixed | See findings 1–3. |
| LLM integration | Pass | `wait` stays a plain sleep when `WaitForPeerSignals` is null (the watch's loop). The static tool catalog is unchanged — `AgentToolCatalogTests` still green. Prompt grew by the PEER-TO-PEER section only. |
| Windows native | Pass | No new P/Invoke. Enter timing is polling on output length, off the UI thread. |

## Findings

1. **Should-fix — `WriteAndEnter` kept its fixed 200 ms** (both sessions). The chat-mode path would hit the
   same Codex paste trap for a long message that `WriteAndSubmit` did at 50 ms. → fixed: same
   `TerminalSubmit` rule.
2. **Should-fix — WPF `read_terminal` still returned the raw pipe tail** while the Avalonia host had been
   moved to the screen snapshot. → fixed: both hosts use `TerminalScreenText.Tidy(GetConsoleText())` with the
   raw tail as fallback, and both attach the `PeerCliFault` note.
3. **Nit — two regex fields in WPF `MainWindow` were dead** after `PeerEnvelope` took the parsing. → removed.

## Failure-mode review

- **Enter never sent** — the helper always calls `enter()` (cap), and the session wraps it in try/catch with a
  log line. A screen that never goes quiet costs the cap, not the message.
- **Password autofill** (`SshPasswordWatcher` → `WriteAndSubmit`): no echo, so the Enter now goes out at
  ~250 ms instead of 50 ms. Acceptable; covered by a test.
- **Runaway relays** — a continuation starts only after `Success`; Cancel/Reset drop what is waiting.
- **Stale `setup_error.json`** — quoted only when written in the last 30 minutes; the note itself needs the
  marker on the peer's own screen.
- **Not covered by a test**: the Avalonia `terminal-rename` wiring (UI-thread lambda) and the two hosts'
  `ReadTerminalAsync` — exercised by hand on the Debug build only.

## Verification

- `ZeroCommon.Tests`: 1,531 passed (29 new). `McpTests.Settings_round_trip…` failed in 2 of 7 full runs and
  passes alone — it swaps the static `McpSettingsStore.FilePath`, which races other tests; not touched here.
- `AgentZeroAvalonia.Tests`: 140 passed. WPF Debug build: 0 errors.
- Debug GUI, zero-test workspace: "Codex와 Hermes 2라운드" finished in 93 s, 17 turns, each message relayed once.

## 평가

| 축 | 결과 |
|----|------|
| 코드 안전성 | A — 새 동시성 지점(수신함)은 락 하나, 취소·리셋 경로 테스트됨 |
| 아키텍처 정합성 | A — 로직은 ZeroCommon, 두 호스트는 호출만; 핸드셰이크 사본 2개가 1개로 |
| 테스트 가능성 | B — 터미널 타이밍·봉투·수신함·액터 흐름은 헤드리스 테스트, 호스트 배선은 수동 확인 |

## 다음 단계 제안

- 화면으로만 답하는 peer의 턴 종료(`AgentActivityTracker`의 working → done)로 `wait`를 깨우기.
- `McpSettingsStore.FilePath` 정적 교체 테스트를 컬렉션으로 묶어 경합 제거.
