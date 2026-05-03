# AgentZero Lite — Architecture Map (for harness agents)

> Quick-reference map of the load-bearing pieces. Agents use this to anchor file:line
> findings and to know where invariants live.

## Two C# projects, strict dependency rule

| Project | TFM | May depend on | May contain WPF/Win32? |
|---------|-----|----------------|------------------------|
| `Project/ZeroCommon/` | `net10.0` | (none) | **No** — must stay headless |
| `Project/AgentZeroWpf/` | `net10.0-windows` | `ZeroCommon` | Yes |
| `Project/ZeroCommon.Tests/` | `net10.0` | `ZeroCommon` | **No** — runs in CI without desktop |
| `Project/AgentTest/` | `net10.0-windows` | both | Yes — needs desktop session |

Rule: **never reverse the arrow.** `ZeroCommon → AgentZeroWpf` is forbidden. The seam
that lets the actor layer reach the UI is `ITerminalSession`
(`Project/ZeroCommon/Services/ITerminalSession.cs`), implemented WPF-side as
`ConPtyTerminalSession`. Cross-thread UI dispatch goes through `AgentEventStream` /
`SetBotUiCallback`.

## Single exe, two modes

`AgentZeroLite.exe` (assembly name set in `AgentZeroWpf.csproj`; project namespace
remains `AgentZeroWpf.*`). In `App.OnStartup` (`Project/AgentZeroWpf/App.xaml.cs`):

- Args contain `-cli` → `CliHandler.Run`, `Environment.Exit`. WPF loop never starts.
- Otherwise → named mutex `Local\AgentZeroLite.SingleInstance`, then
  `ActorSystemManager.Initialize()`, then `MainWindow`.

The `AgentCLI` build configuration exists in csproj but is *not* the runtime gate.

## Actor topology (Akka.NET)

```
/user/stage                    StageActor      — supervisor + message broker
    /bot                       AgentBotActor   — 0 or 1; routes UserInput
    /ws-<name>                 WorkspaceActor  — one per workspace folder
        /term-<id>             TerminalActor   — wraps one ITerminalSession
```

Message types: `Project/ZeroCommon/Actors/Messages.cs` — single source of truth.
User-supplied strings (workspace names, terminal IDs) **must** pass through
`ActorNameSanitizer` before path construction; Akka rejects `/`, `:`, spaces.

## Akka shutdown invariant (history: deadlock incident)

`ActorSystemManager.Shutdown()` is fire-and-forget by design. A previous version blocked
on `ShutdownAsync().GetAwaiter().GetResult()` from the UI thread, deadlocking the
`synchronized-dispatcher`. The current Akka config sets
`coordinated-shutdown.exit-clr = on` so CLR termination originates from the shutdown
phases, not from user code. **Do not regress this.**

## CLI ↔ GUI IPC

- Send: `CliHandler.cs` posts `WM_COPYDATA` with marker `0x414C "AL"`.
- Receive: `CliTerminalIpcHelper.cs` / `MainWindow`.
- Response: GUI writes JSON to memory-mapped files prefixed `AgentZeroLite_*`.
- CLI polls (default 5s; `--no-wait` skips). Helper wrapper:
  `Project/AgentZeroWpf/AgentZeroLite.ps1` (launches with `-NoNewWindow -Wait`).

## Persistence

EF Core + SQLite. DB: `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`. Migrated by
`AppDbContext.InitializeDatabase()` on first run.

- **Migrations live in** `Project/ZeroCommon/Data/Migrations/`.
- `AgentZeroWpf/Data/Migrations/` exists but is empty — **never scaffold there.**
- Seeded `CliDefinition` rows (CMD, PW5, PW7, Claude) have `IsBuiltIn = true` and
  must not be UI-deletable.

## Native DLL pinning

`conpty.dll` and `Microsoft.Terminal.Control.dll` are pulled from the
`CI.Microsoft.*` NuGet packages via hard-coded `$(NuGetPackageRoot)` paths in
`AgentZeroWpf.csproj`. Bumping those packages requires updating the version segments in
the two `<Content Include=...>` entries — otherwise the copy step silently drops, and
the app runs but ConPTY tabs do not start.

Self-built `llama.dll` / `ggml-*.dll` for Gemma 4 are a documented temporary workaround
(see `Docs/llm/index.md`) until LLamaSharp NuGet ships official Gemma 4 support. Do not
treat them as a steady state.

## Markdown / Pencil rendering

- `Assets/mermaid.min.js` — embedded as a logical resource for offline preview;
  `MarkdownViewer` + `MermaidRenderer` + WebView2 do the render.
- `.pen` files — encrypted; routed through the `pencil` MCP server. Never read with
  `Read`/`Grep`.

## IDE debugging trap (history)

ConPTY needs to own console events. If the IDE attaches its own console to the
process's stdio, ConPTY tabs refuse to start or render garbled output.

- Rider: external console = ON
- Visual Studio: uncheck "Use the standard console" / "Redirect standard output"
- VS Code: `"console": "externalTerminal"`

`dotnet run` from a shell is fine because it doesn't steal stdio.
