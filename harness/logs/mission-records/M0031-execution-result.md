---
mission: M0031
title: Modern terminal spike — xterm.js-in-WebView2 backend behind the ITerminalSession seam
operator: psmon
language: en
dispatched_to: [tamer, code-coach]
status: done
started: 2026-08-31T00:00:00+09:00
finished: 2026-08-31T00:53:35+09:00
artifacts:
  - Project/AgentZeroWpf/Services/ManagedConPtyHost.cs (new)
  - Project/AgentZeroWpf/Services/WebViewXtermTerminalSession.cs (new)
  - Project/AgentZeroWpf/UI/Components/XtermTerminalControl.xaml(.cs) (new)
  - Project/AgentZeroWpf/Wasm/xterm/* (new, offline assets)
  - Project/AgentZeroWpf/NativeMethods.cs (ConPTY interop)
  - Project/AgentZeroWpf/Module/CliConsoleModels.cs (widen Session to ITerminalSession)
  - Project/AgentZeroWpf/Module/CliTerminalIpcHelper.cs (widen TryResolveSession)
  - Project/AgentZeroWpf/Module/CliSessionAccessHelper.cs (backend branch)
  - Project/AgentZeroWpf/Services/AgentStateMonitor.cs (interface, no downcast)
  - Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs (tab creation behind flag)
related: [M0030]
worktree: feat/modern-terminal-webview
pdsa_cycle: agentzero-lite #1
---

# M0031 execution result — Modern terminal spike (xterm.js-in-WebView2)

## Execution summary

Investigate a "more modern" terminal for AgentZero Lite and attempt a replacement that keeps
every existing feature working while giving the WPF shell better control over the terminal window.

Key research finding: AgentZero is **already on the most modern mainstream Windows terminal
backend** (EasyWindowsTerminalControl → the real Windows Terminal `Microsoft.Terminal.Control.dll`
+ `conpty.dll`, hosted via **HwndHost**). The recurring pain (approval overlays can't render above
the terminal / "PTY-FREEZE-DIAG" input-pipe wedge / brittle hard-coded native DLL paths with a
version mismatch) is inherent to the HwndHost + ConPTY approach, not to being outdated.

Chosen direction (operator-confirmed): build a working PoC of an **xterm.js-in-WebView2** backend
as a *second* `ITerminalSession` implementation selectable **behind a `TerminalBackend` flag**
(current backend stays default), in a `feat/` worktree, tracked as PDSA cycle #1.

## Result

Delivered a working PoC of an **xterm.js-in-WebView2** terminal backend as a second
`ITerminalSession` implementation, selectable behind a `terminal-settings.json` flag
(`Backend: EasyConPty | WebViewXterm`, default `EasyConPty`). New pieces (20 files, +1454/−37):

- `Project/ZeroCommon/Services/TerminalSettings.cs` — `TerminalBackend` enum + side-car store.
- `Project/ZeroCommon/Services/TerminalControlSequences.cs` — shared VT table; `ConPtyTerminalSession`
  refactored to use it so both backends stay byte-identical (verified by `TerminalControlTests`).
- `Project/AgentZeroWpf/Services/ManagedConPtyHost.cs` — new managed ConPTY host (P/Invoke
  `CreatePseudoConsole`/pipes/`CreateProcess`+`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, `ReadFile`/
  `WriteFile` I/O, resize, process-exit wait).
- `Project/AgentZeroWpf/UI/Components/XtermTerminalControl.xaml(.cs)` — WebView2 host serving
  offline xterm.js (`Wasm/xterm/*`) via the `term.local` virtual-host mapping + JS↔.NET bridge.
- `Project/AgentZeroWpf/Services/WebViewXtermTerminalSession.cs` — full `ITerminalSession` over the
  host (output buffer reproducing `ConsoleOutputLog` shape, health machine, submit-timing, backpressure).
- Coupling widening (`CliConsoleModels`, `CliTerminalIpcHelper`, `CliSessionAccessHelper`,
  `AgentStateMonitor`, `WorkspaceTerminalToolHost`, `AgentBotWindow.Voice`, `BindSessionToActors`) +
  `MainWindow.InitializeWebViewTerminal` wiring behind the flag, sharing the actor-bind path.

## Tried (시도)

- Created worktree `feat/modern-terminal-webview` off `main`; opened PDSA cycle #1.
- Widened every concrete `ConPtyTerminalSession`/`EasyTerminalControl` coupling to the interface.
- Built the managed ConPTY host; first I/O attempt used `FileStream` over the pipe handles, then
  switched to direct `ReadFile`/`WriteFile`.
- Vendored xterm.js 5.5.0 + fit addon 0.10.0 offline; wired the WebView2 message bridge.
- Added live ConPTY integration tests (spawn/output + stdin) to prove the new native piece.

## Solved (해결)

- Clean **Debug + Release** build (0 warnings / 0 errors); Release version bump reverted.
- **707 tests green** — 561 `ZeroCommon.Tests` + 146 `AgentTest` (incl. the shared-VT refactor,
  `ITerminalSession` contract, ApprovalParser 95, ExternalAgentLoop/DONE 23, orchestration) + 2 new
  live ConPTY host tests. Default `EasyConPty` path unchanged ⇒ zero regression.
- Proved the native `ManagedConPtyHost` end-to-end: **spawn + attach + output streaming + stdin input**
  (the latter via process-handle exit detection).
- xterm assets copy to the build output; offline serving path confirmed.

## Remaining (남은 일)

- **Operator desktop smoke** (the decisive, human-observed part): flip `Backend=WebViewXterm`, open a
  tab, verify render / output / input / resize / **Korean IME**, and the headline **airspace demo**
  (approval toast rendering ABOVE the terminal), plus DONE/handshake + health transitions vs the
  HwndHost path. PDSA verdict is PARTIAL until this is done.
- `GetConsoleText` returns the full VT transcript, not just the visible screen — a `SerializeAddon`
  visible-screen snapshot is a follow-up refinement.
- Wedge-recovery "Restart" and floating/redock are EasyConPty-specific; add WebView equivalents
  before wide adoption.

## Learned (학습)

- AgentZero was **already on the modern Windows Terminal backend**; the real pain (airspace, the
  ConPTY input wedge, brittle native DLL pinning) is inherent to HwndHost + ConPTY, not to being old.
- The app already ships every ingredient the modern path needs — WebView2, the offline
  `SetVirtualHostNameToFolderMapping` pattern, and the JS↔.NET bridge — so the managed ConPTY host was
  the only genuinely new native piece (and must stay WPF-side; `ZeroCommon` is Win32-free).
- **Two test traps cost real time, both fixed:** (1) `cmd /c echo` exits before ConPTY paints — a
  long-lived child (`/k`) is needed to observe output; (2) ConPTY keeps the output pipe open past
  child exit, so pipe-EOF is NOT a valid exit signal — wait on the **process handle** instead. Input
  was correct the whole time; the test's exit-detection was the bug.

# Verification artifacts

- `dotnet build …AgentZeroWpf -c Debug` → 0/0. `-c Release` → 0/0 (version bump reverted).
- `dotnet test ZeroCommon.Tests` → **561 passed**, 0 failed, 24 skipped.
- `dotnet test AgentTest` → **146 passed**, 0 failed, 7 skipped (incl. 2 new live ConPTY host tests).
- `ManagedConPtyHost` live proof: `Spawns_child_and_streams_output` (3/3 reliable, ~75 ms) +
  `Write_reaches_child_stdin` (stdin `exit` → process handle signals, ~576 ms).
- Assets present in `bin/Debug/net10.0-windows/Wasm/xterm/{index.html,term.js,vendor/*}`.
- PDSA cycle #1 (agentzero-lite): Plan→Do→Study→Act recorded; verdict PARTIAL (contract/build/native
  met; airspace+IME visual pending operator).
