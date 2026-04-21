# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

Target framework is **.NET 10** (preview). The WPF host is `net10.0-windows`; shared logic (`ZeroCommon`) is `net10.0` and must remain WPF/Win32-free so its headless xUnit suite can run.

```bash
# Build the WPF app (pulls ZeroCommon via ProjectReference)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# Release build — auto-bumps version.txt (patch+1, 9→minor+1) in a post-build target
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Release

# Headless tests — fast, no desktop session needed
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj

# WPF-dependent tests (actors, ConPTY session, approval parser) — needs desktop session
dotnet test Project/AgentTest/AgentTest.csproj

# Run a single test
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj --filter "FullyQualifiedName~ApprovalParserTests.ParsesBashPrompt"

# Launch the GUI
Project/AgentZeroWpf/bin/Debug/net10.0-windows/AgentZeroLite.exe

# Drive a running GUI from any shell (exe double-dispatches on `-cli`)
Project/AgentZeroWpf/bin/Release/net10.0-windows/AgentZeroLite.exe -cli status
```

There is a third build configuration `AgentCLI` declared alongside Debug/Release — leave it in the csproj even though the runtime CLI/GUI split is decided inside `App.OnStartup` by checking `-cli` in args, not by configuration.

## ⚠️ IDE debugging — disable integrated terminal attachment

The app hosts real ConPTY terminals inside WPF tabs. If the IDE attaches its own console to the process's stdio, it intercepts the console events ConPTY needs to own and tabs either refuse to start or render garbled output.

- **Rider**: Run/Debug config → *Use external console = ON*
- **Visual Studio**: Debug properties → uncheck *Use the standard console* / *Redirect standard output*
- **VS Code**: `launch.json` → `"console": "externalTerminal"` (not `internalConsole`)

`dotnet run` from a shell works because it doesn't steal stdio.

## Architecture

Two C# projects with a strict dependency rule: **`AgentZeroWpf → ZeroCommon`, never reversed.** Anything that doesn't need WPF/Win32 APIs belongs in `ZeroCommon` so it stays testable headlessly. `AgentTest` references both; `ZeroCommon.Tests` references only `ZeroCommon`.

### Single exe, two modes
`AgentZeroLite.exe` is a WinExe (assembly name set in `AgentZeroWpf.csproj`; project/namespace stay `AgentZeroWpf.*`). In `App.OnStartup` (`Project/AgentZeroWpf/App.xaml.cs`):
- If args contain `-cli`, `CliHandler.Run` takes over, calls `Environment.Exit`, and the WPF message loop never starts.
- Otherwise a named mutex `Local\AgentZeroLite.SingleInstance` guards single-instance GUI, then `ActorSystemManager.Initialize()` builds the Akka system before `MainWindow` shows.

### Actor topology (Akka.NET)
All message types live in one file: `Project/ZeroCommon/Actors/Messages.cs`. The hierarchy is:

```
/user/stage                       StageActor     — supervisor + message broker
    /bot                          AgentBotActor  — 0 or 1; routes UserInput to active terminal
    /ws-<name>                    WorkspaceActor — one per workspace (folder)
        /term-<id>                TerminalActor  — wraps one ITerminalSession
```

`ITerminalSession` (`Project/ZeroCommon/Services/ITerminalSession.cs`) is the seam between actors (logic, testable) and `ConPtyTerminalSession` (WPF-side, in `AgentZeroWpf/Services/`). Actor-layer code must not import WPF; if you need to reach the UI, go through `AgentEventStream` / `SetBotUiCallback`.

Actor names sometimes contain user input (workspace names, terminal IDs). Route them through `ActorNameSanitizer` before constructing paths — Akka rejects characters like `/`, `:`, spaces.

### Akka shutdown quirk
`ActorSystemManager.Shutdown()` is fire-and-forget by design. Previously `ShutdownAsync().GetAwaiter().GetResult()` on the UI thread deadlocked the `synchronized-dispatcher` (which needs to post back to the UI thread), leaving the process alive and the single-instance mutex held. The Akka config sets `coordinated-shutdown.exit-clr = on` so CLR termination happens from the shutdown phases, not from user code.

### CLI ↔ GUI IPC
`AgentZeroLite.exe -cli <cmd>` talks to the running GUI over `WM_COPYDATA` with marker `0x414C "AL"` (send side in `CliHandler.cs`, receive in `CliTerminalIpcHelper.cs` / `MainWindow`). The GUI writes JSON responses to named memory-mapped files with the `AgentZeroLite_*` prefix; the CLI side polls (default 5s timeout; `--no-wait` skips the wait entirely). Helper wrapper: `Project/AgentZeroWpf/AgentZeroLite.ps1` (launches with `-NoNewWindow -Wait` to make stdio visible).

### Persistence
EF Core + SQLite. DB file: `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`, created/migrated by `AppDbContext.InitializeDatabase()` on first run. **Migrations live in `Project/ZeroCommon/Data/Migrations/`** — the `AgentZeroWpf/Data/Migrations/` folder exists but is empty; don't scaffold into it. Seeded `CliDefinition` rows (CMD, PW5, PW7, Claude) are marked `IsBuiltIn = true` and must not be deletable from the UI.

### Native DLLs
`conpty.dll` and `Microsoft.Terminal.Control.dll` are pulled from the `CI.Microsoft.*` NuGet packages via hard-coded `$(NuGetPackageRoot)` paths in `AgentZeroWpf.csproj`. If you bump those packages, update the version segments in the two `<Content Include=...>` entries or the copy step will silently drop — the app runs but ConPTY tabs won't start.

### Mermaid/Pencil rendering
`Assets/mermaid.min.js` is embedded as a logical resource (`LogicalName="mermaid.min.js"`) for offline Markdown preview; `MarkdownViewer` + `MermaidRenderer` + WebView2 handle the render. Pencil (`.pen`) files go through the `pencil` MCP server — those files are encrypted, never read them with `Read`/`Grep`.
