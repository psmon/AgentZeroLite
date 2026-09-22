# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

Target framework is **.NET 10** (preview). The WPF host is `net10.0-windows`; shared logic (`ZeroCommon`) is `net10.0` and must remain WPF/Win32-free so its headless xUnit suite can run. The wearable host (`ZeroWearable`) is the one exception: `net10.0-windows10.0.19041.0`, because the BLE central is WinRT.

```bash
# Build the WPF app (pulls ZeroCommon + ZeroWearable via ProjectReference)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# Build just the wearable host (the smartwatch's BLE/actor process)
dotnet build Project/ZeroWearable/ZeroWearable.csproj -c Debug

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

# Wearable host on its own — no GUI, no watch, no radio needed
cd Project/ZeroWearable/bin/Debug/net10.0-windows10.0.19041.0
./AgentZeroWearable.exe --brain Cli --provider echo --ask "안녕"   # brain round-trip
./AgentZeroWearable.exe --speak "안녕하세요" --out hello.wav        # TTS → 16 kHz ADPCM
./AgentZeroWearable.exe --hear hello.wav --lang ko                  # …and back through STT
./AgentZeroWearable.exe --no-ble --port 2562                        # full host, network peers only
```

There is a third build configuration `AgentCLI` declared alongside Debug/Release — leave it in the csproj even though the runtime CLI/GUI split is decided inside `App.OnStartup` by checking `-cli` in args, not by configuration.

## ⚠️ IDE debugging — disable integrated terminal attachment

The app hosts real ConPTY terminals inside WPF tabs. If the IDE attaches its own console to the process's stdio, it intercepts the console events ConPTY needs to own and tabs either refuse to start or render garbled output.

- **Rider**: Run/Debug config → *Use external console = ON*
- **Visual Studio**: Debug properties → uncheck *Use the standard console* / *Redirect standard output*
- **VS Code**: `launch.json` → `"console": "externalTerminal"` (not `internalConsole`)

`dotnet run` from a shell works because it doesn't steal stdio.

## Architecture

Three C# projects with a strict dependency rule: **`AgentZeroWpf → ZeroCommon` and `ZeroWearable → ZeroCommon`, never reversed.** Anything that doesn't need WPF/Win32 APIs belongs in `ZeroCommon` so it stays testable headlessly — and so the wearable host can reuse it. `AgentZeroWpf` references `ZeroWearable` for build order only (`ReferenceOutputAssembly=false`); it launches that exe, it does not link it. `AgentTest` references both; `ZeroCommon.Tests` references only `ZeroCommon`.

### Single exe, two modes
`AgentZeroLite.exe` is a WinExe (assembly name set in `AgentZeroWpf.csproj`; project/namespace stay `AgentZeroWpf.*`). In `App.OnStartup` (`Project/AgentZeroWpf/App.xaml.cs`):
- If args contain `-cli`, `CliHandler.Run` takes over, calls `Environment.Exit`, and the WPF message loop never starts.
- Otherwise a named mutex `Local\AgentZeroLite.SingleInstance` guards single-instance GUI, then `ActorSystemManager.Initialize()` builds the Akka system before `MainWindow` shows.

### Actor topology (Akka.NET)
All message types live in one file: `Project/ZeroCommon/Actors/Messages.cs`. The hierarchy is:

```
/user/stage                       StageActor      — supervisor + message broker
    /bot                          AgentBotActor   — UI gateway: chat/key mode, peer routing,
                                                    intro tracking. Spawns AgentLoopActor lazily.
        /loop                     AgentLoopActor  — THE agent: owns one IAgentLoop, drives
                                                    Idle→Thinking→Generating→Acting→Done FSM.
    /ws-<name>                    WorkspaceActor  — one per workspace (folder)
        /term-<id>                TerminalActor   — wraps one ITerminalSession
```

`ITerminalSession` (`Project/ZeroCommon/Services/ITerminalSession.cs`) is the seam between actors (logic, testable) and `ConPtyTerminalSession` (WPF-side, in `AgentZeroWpf/Services/`). Actor-layer code must not import WPF; if you need to reach the UI, go through `AgentEventStream` / `SetBotUiCallback`.

**Agent vocabulary (M0013)** — the agent loop layer (`Project/ZeroCommon/Llm/Tools/`) uses canonical "agent loop" naming aligned with the Anthropic *Building effective agents* post + Claude Agent SDK: `IAgentLoop` (backend-agnostic contract), `LocalAgentLoop` / `ExternalAgentLoop` (LLamaSharp+GBNF / OpenAI-compat REST), `IAgentToolbelt` (side-effect surface), `AgentLoopRun` (one RunAsync result), `AgentLoopGuards` (repeat / hard-stop / transient-retry defenses). The actor wraps one `IAgentLoop` per session — **`AgentBotActor` is the UI gateway, `AgentLoopActor` is the agent**. Full vocabulary table at `harness/knowledge/_shared/agent-architecture.md`.

Actor names sometimes contain user input (workspace names, terminal IDs). Route them through `ActorNameSanitizer` before constructing paths — Akka rejects characters like `/`, `:`, spaces.

### Akka shutdown quirk
`ActorSystemManager.Shutdown()` is fire-and-forget by design. Previously `ShutdownAsync().GetAwaiter().GetResult()` on the UI thread deadlocked the `synchronized-dispatcher` (which needs to post back to the UI thread), leaving the process alive and the single-instance mutex held. The Akka config sets `coordinated-shutdown.exit-clr = on` so CLR termination happens from the shutdown phases, not from user code.

### CLI ↔ GUI IPC
`AgentZeroLite.exe -cli <cmd>` talks to the running GUI over `WM_COPYDATA` with marker `0x414C "AL"` (send side in `CliHandler.cs`, receive in `CliTerminalIpcHelper.cs` / `MainWindow`). The GUI writes JSON responses to named memory-mapped files with the `AgentZeroLite_*` prefix; the CLI side polls (default 5s timeout; `--no-wait` skips the wait entirely). Helper wrapper: `Project/AgentZeroWpf/AgentZeroLite.ps1` (launches with `-NoNewWindow -Wait` to make stdio visible).

The `web` command group (M0032, `Services/Browser/WebCliCommands.cs`) is the one group whose GUI handler answers **asynchronously** (a navigation takes seconds and WndProc must return at once). Two guards keep the polling CLI honest: `HandleWebCommand` clears its MMF synchronously before the work starts, and the reply echoes the request's `req` id, which the CLI insists on. Default `--timeout` for the group is 45 s.

### CLI test skills (agent-facing usage guides)
`.claude/skills/agentzero-cli/` is a **guide-level skill authored to exercise the `-cli` surface** — it teaches an agent to locate the exe (Debug build preferred for internal testing), invoke it on Windows (WinExe → `Start-Process -NoNewWindow -Wait` or the `.ps1` wrapper), drive terminal tabs, run the handshake / `DONE()` reverse channel and two-terminal discussion loop, and use the native `os` control verbs. It's a testing/dev aid, not product code; `dotnet` builds ignore it. `scripts/find-cli.ps1` resolves the exe by priority; `references/` holds the full command + interaction detail. `codex/prompts/agentzero-cli.md` is the project-bundled Codex analog (Codex has no `skills/` auto-discovery — copy/symlink it into `~/.codex/prompts/` to get `/agentzero-cli`). Both stay thin pointers to `-cli help agentzero` so they can't drift from the binary. Official plugin packaging is tracked separately.

### Wearable host (M0031) — a second process on purpose

`Project/ZeroWearable` (`AgentZeroWearable.exe`) owns the smartwatch's single BLE link and
serves its three apps at once: **AskBot** as a real Akka remoting peer (PDUs tunnelled over
BLE by `BleTunnel`), **Chat** over the older line protocol (`BleChatProxy` turns it into
messages for the same `ChatActor`), and the **Claude HUD** over `POST /status` · `/event`
on :8765. Ported from `D:\…\Arduino\project\samples\akka\host\AkkaHost`; `Docs/` there and
its `PROTOCOL.md` are the reference for the wire format.

It is a separate exe for exactly one reason: `Windows.Devices.Bluetooth` needs a
Windows-SDK TFM, and moving `AgentZeroLite.exe` off `net10.0-windows` would move the bin
path that CLAUDE.md, the `agentzero-cli` skill, `Docs/scripts/launch-self-smoke.ps1` and the
installer all quote. The GUI starts/stops it from the **Wearable** activity-bar entry
(`WearablePagePanel` → `Services/Wearable/WearableHostProcess`) and streams its stdout into
the panel; `[host/ready]` is the line that means the radio is up.

**Nothing model-shaped is configured twice.** The host reads AgentZero's own stores and
loads the bundles the app already installed:

| What | Comes from | Reused type |
|------|-----------|-------------|
| Voice | Settings → Voice (Supertonic) | `SuperTonicSynthesizer` — the sample's 466-line copy was dropped |
| Ear | Settings → Voice (WhisperLocal) | `WhisperModelStore` + Whisper.net, incl. the Vulkan→CPU probe |
| Brain | Settings → LLM | `IAgentLoop` (`LocalAgentLoop` / `ExternalAgentLoop`) over `WearableToolbelt` |

`WearableToolbelt` (M0032, in `ZeroCommon/Wearable/`) is a thin adapter over two tool
actors — **files** (`FileToolActor`) and **web** (`WebToolActor`). Terminals, mouse,
keyboard and screenshots stay unimplemented and answer "not available": a device across
the room does not drive the machine. The file side is sandboxed to the **allow-listed
folders** in `wearable-settings.json` (`AllowedRoots`: alias + path + per-folder
`Writable`; the model addresses files as `alias/relative/path`, `list_files` with no path
returns the aliases, an empty list is default-deny; a pre-M0032 `WorkspaceRoot` is
migrated on load). `open_file` hands a file to ShellExecute only when `FileOpenPolicy`
classifies its extension as media / image / document — that allow-list is the line between
"play this song" and "run this program". The web side (`web_search` / `web_open` /
`web_read`) uses the GUI's **Browser** page through `AgentZeroLite.exe -cli web …` while
the GUI is running and a headless fetch (`HeadlessWebFetcher` + `WebPageExtractor`)
otherwise; both paths run the same extractor and every envelope says `via: gui | headless`.
`web_search` also opens the first result (`top_page`) and, for weather questions, attaches
a `weather` block from wttr.in — a small model answers from text it is handed and rarely
goes to fetch it. `GuiExePath: "off"` in the settings file keeps browsing headless even
while the GUI runs. Page text is handed to the model as data, with the prompt saying so.
`stop_media` ends what `open_file` started (`MediaPlaybackTracker`: the returned process,
a known player that appeared after the launch, else the system media-stop key).

The agent itself is an actor subtree of the host's ActorSystem, mirroring the main app's
`AgentBotActor` / `AgentLoopActor` split: `ChatActor` is the device gateway only, and
`/user/agent` (`WearableAgentActor`) supervises `/files`, `/web` and one reused
`AgentLoopActor` per conversation (`/session-<key>`). It adds newest-question-wins — cancel
the running loop, queue the new question until the loop is idle — which the main app's
loop actor does not need. `AgentZeroWearable.exe --ask` runs the same subtree on a local,
non-remoting ActorSystem, so the panel's "Test brain" is a real smoke test of that path.

**The tool catalog was extended on purpose in M0032** (`find_files`, `open_file`, `stop_media`,
`web_search`, `web_open`, `web_read`): GBNF, `KnownTools` and the prompt text in `AgentToolGrammar`
move together and `AgentToolCatalogTests` keeps them in step. The main app implements
the same verbs (`WorkspaceTerminalToolHost`: `open_file` inside the active workspace, web
tools against the Browser page via `BrowserToolSurfaceRegistry`). The sample's music
tools were still not ported — a verb family with no use outside the watch.

Link-only settings live in `wearable-settings.json` (`Agent.Common.Wearable`), and that file
is the entire contract between the two processes — the host reads it once at startup, so
"apply" in the panel means **restart**. Build output is copied to `bin/…/wearable/` and
`dotnet publish` re-publishes it into `publish/wearable/` with the GUI's RID and
self-contained flag (the installer's `[Files]` already recurses).

Two things about the child process that are easy to get wrong again:

- **The HUD endpoint starts whether or not BLE does**, and every S/E line is logged at Info
  as either `-> watch` or `dropped (n) — <reason>`. The hooks in `~/.claude/hud_amoled` post
  to :8765 regardless, so an endpoint that quietly never opened, and one that works but has
  no watch in range, used to look identical. Only one process can hold that port — this host
  **or** claude_hud_amoled's `ble_bridge.py`, never both.
- **A Job Object (`WearableHostJob`) ties the host's lifetime to the GUI's.** Stop and normal
  shutdown already worked; this covers the crash / "End task" / `Stop-Process -Force` path,
  where an orphaned host keeps the BLE link and :8765 and the next run's host then refuses to
  start on its single-instance mutex.

UI note: every `<ComboBox>` MainWindow hosts is dark because MainWindow's **implicit**
ComboBox style is a full re-template (toggle + popup + item). That is Pitfall 6 in
`harness/knowledge/code-coach/wpf-xaml-resource-and-window-pitfalls.md` — `Background` /
`Foreground` setters alone leave the chrome and the popup drawn from `SystemColors`.

### Persistence
EF Core + SQLite. DB file: `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`, created/migrated by `AppDbContext.InitializeDatabase()` on first run. **Migrations live in `Project/ZeroCommon/Data/Migrations/`** — the `AgentZeroWpf/Data/Migrations/` folder exists but is empty; don't scaffold into it. Seeded `CliDefinition` rows (CMD, PW5, PW7, Claude) are marked `IsBuiltIn = true` and must not be deletable from the UI.

### Terminal
One backend: **xterm.js in a WebView2**, driven by `ManagedConPtyHost` — our own pseudo-console over plain `kernel32` P/Invoke (`CreatePseudoConsole`, present since Windows 10 1809). The app therefore ships **no native terminal DLLs**; `conpty.dll`, `Microsoft.Terminal.Control.dll` and the `EasyWindowsTerminalControl` / `CI.Microsoft.*` packages are gone, and with them the hard-coded `$(NuGetPackageRoot)` copy step that failed silently on a version bump.

Assets live in `Project/AgentZeroWpf/Wasm/xterm/` and are served offline through a `term.local` virtual-host mapping — CSP in `index.html` blocks the network. JetBrains Mono ships alongside them (`vendor/fonts/`, OFL 1.1 — the licence must travel with the files).

Two things that are easy to get wrong here:

- **`GetConsoleText()` means "what is on the screen", not "everything the pipe produced."** The approval parser, the agent-state monitor and the bot's context all ask it that question. Only the emulator knows the answer, and the emulator is xterm.js in the renderer, so the renderer pushes a viewport snapshot back (`term.js` → `TerminalConsoleBuffer`). Returning the transcript instead re-matches prompts answered long ago and grows the model's context without bound.
- **The child's environment is built, not inherited** (`TerminalEnvironment`). Handing a terminal child whatever launched the GUI is how a `NO_COLOR=1` picked up from an IDE terminal switched colour off in every tab. Same reason `CLAUDE_CODE_*` markers are dropped: a tab the user opened is not a nested agent session.

### Mermaid/Pencil rendering
`Assets/mermaid.min.js` is embedded as a logical resource (`LogicalName="mermaid.min.js"`) for offline Markdown preview; `MarkdownViewer` + `MermaidRenderer` + WebView2 handle the render. Pencil (`.pen`) files go through the `pencil` MCP server — those files are encrypted, never read them with `Read`/`Grep`.

### Cross-platform host — `Project/AgentZeroAvalonia` (M0033–M0040, merged 2026-09-19)

A second GUI host, Avalonia 12 on plain `net10.0`, that runs on Windows **and macOS** beside the WPF one. It shares ZeroCommon (actors, agent loop, stores, database). Guide + conversion playbook: `README-Avalonia.md` / `README-Avalonia-KR.md`; design and per-milestone findings: `Docs/avalonia-v2/DESIGN.md`.

**Strategy: WPF first, then converge.** WPF is not being replaced — it is the Windows-first lab where features are built and tried quickly; what earns its keep is *converted* into the Avalonia host so it reaches macOS. Keep the conversion cheap: logic without a UI dependency goes to `ZeroCommon` (if a feature cannot be converted without copying logic, move the logic down first); both hosts read and write the same DB/settings/layout rows; the CLI JSON contract is one for both; Windows-only pieces stay fenced by `OperatingSystem.IsWindows()`. **Conversion work never edits the WPF project** — the gate is `git diff --stat main -- Project/AgentZeroWpf` empty for conversion commits; shared fixes go into ZeroCommon. When a WPF feature changes, list its conversion in `README-Avalonia.md`'s table (✅ / ⏳) and keep the playbook there current.

```bash
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Debug         # Windows dev build
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r osx-arm64
dotnet test  Project/AgentZeroAvalonia.Tests/AgentZeroAvalonia.Tests.csproj      # headless: codec, asset server, PTY, layout, hotkeys, bot, settings, router
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe                    # GUI (same exe name, different folder)
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli status        # drives THIS host over a named pipe
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli selftest all  # ipc / secrets / pty without a display (CI uses it)
```

What differs from the WPF host, and why:

- **Terminal**: xterm.js inside `NativeWebView`, served by `LocalAssetServer` (loopback, token path) because the Avalonia WebView cannot map a folder; the PTY is `ConPtyHost` (a copy of `ManagedConPtyHost`) on Windows and `PortaPtyHost` (Porta.Pty) on macOS behind ZeroCommon's `IPtyHost`/`XtermTerminalSession`. Two ConPTY facts the WPF copy never hit: a parent whose stdio is a pipe hands its std handles to the child (blanked around `CreateProcess`), and pipe EOF is not the exit signal (a process-handle watcher is).
- **Split panes**: `WorkspaceLayout<T>` + one Canvas that positions every renderer over its pane slot — no native re-parenting. The stored `CliGroup.LayoutJson` is byte-identical to what WPF writes.
- **CLI**: same request/response JSON as WPF over the pipe `AgentZeroLite.cli` (not WM_COPYDATA), so `-cli help agentzero` applies; extra verbs `bot-ask` and `layout`. Wrappers: `AgentZeroLite.ps1` / `AgentZeroLite.sh`.
- **Both GUIs share the SQLite file and the settings files** and refuse to run side by side (same single-instance mutex on Windows). Secrets: DPAPI on Windows, AES-GCM (`aesg:v1:`) elsewhere.
- **Local LLM is Windows-only** (LLamaSharp DLLs); macOS uses External providers. Gemma 4's native tool-call syntax is converted to the JSON envelope by `GemmaNativeToolCall` (ZeroCommon, benefits both hosts).
- CI: `.github/workflows/avalonia-build.yml` (windows-latest + macos-14, `.app` bundle via `macos/build-app.sh`); `release.yml` is untouched. macOS GUI checks need a person: `Docs/avalonia-v2/macos-smoke.md`.

### `Project/AgentOne` — a standalone CLI agent (`agent-one`), npm-bound

A second, **independent** product in this repo: a cross-platform CLI agent that
publishes as a Native AOT single binary (win-x64 / linux-x64 / osx-arm64 /
osx-x64, ~6 MB, no runtime to install) and ships through npm as
`@webnori/agent-one`. Skeleton borrowed from `C:\code\psmon\CodeScan` — argv
switch in `Program.cs` → `Commands/`, all state under `~/.agent-one/`
(`Services/AppPaths`), `version.txt` MSBuild auto-bump, `packaging/npm/` wrapper
that downloads a release asset and verifies its SHA256. Full guide:
`Project/AgentOne/README.md`.

```bash
dotnet build Project/AgentOne/AgentOne.csproj -c Debug
dotnet test  Project/AgentOne.Tests/AgentOne.Tests.csproj     # headless, cross-platform
Project/AgentOne/bin/Debug/net10.0/agent-one run "hello" --provider echo
dotnet publish Project/AgentOne/AgentOne.csproj -c Release -r win-x64 -o out/win-x64

# Dev shortcut: builds if missing/stale, then runs. Works from any directory.
Project/AgentOne/agent-one.ps1 run "hello" --provider echo
Project/AgentOne/agent-one.ps1 tui
```

`agent-one.ps1` declares **no** PowerShell parameters on purpose — agent-one's
own flags include `-r`, `-p`, `-m` and `-v`, and PowerShell would bind those to
any parameter whose name starts with the same letter (`-r` → `-Rebuild`) before
the binary saw them. It reads `$args` raw and lifts out only `-Rebuild` /
`-NoBuild`. It also pins the version from `version.txt` so the wrapper never
dirties that tracked file, and calls the exe directly (not `Start-Process`)
because the TUI needs the real console.

**It references nothing else in this solution, and nothing references it.** That
is the point, not an oversight: ZeroCommon's agent loop is bound to Akka, EF
Core, LLamaSharp and ONNX with `runtimes/win-x64-*` natives — none of which
survives Native AOT or a non-Windows target. agent-one reimplements the small
part it needs (`Agent/AgentLoop` over `IChatProvider` + `IToolbelt`, one JSON
envelope per turn) so it stays extractable into its own repo. The intended
integration is process-level: launch `agent-one --json` and read one object off
stdout, the way the GUI launches `AgentZeroWearable.exe`.

**Settings TUI** (`agent-one setup`; `tui` and `config tui` are aliases) — a five-step stack
over `~/.agent-one/config.json`: **1. Connection** (provider, baseUrl, apiKeyEnv)
→ **2. Model** → **3. Reasoning** (the slow, strong model hard questions are
escalated to: `reasoningBaseUrl` / `reasoningApiKey` / `reasoningModel`, each
empty meaning "same as the step before"; `AgentConfig.ForReasoning()` derives
the config a provider needs, and `ApiKey.Resolve` honours its `KeySlot`) →
**4. Options** → **5. Smart**. The order is the dependency: you cannot pick a
model until the endpoint and key are right, and the endpoint is what knows which
models exist. **Arriving at step 2 calls `GET {baseUrl}/models`**, which is
deliberately also the health check for step 1 — one request covers base URL,
network and key — so an empty or rejected list is reported as a failure naming
both suspects rather than as an empty picker. `t` sends one real request through
the settings as they stand. `Esc` means "back a step" and only quits from the
first one. Built on **Termina** (`Tui/`), the one TUI measured to survive Native
AOT here — see `Docs/agent-netclaw/README.md`. The rules live in
`Tui/ConfigTuiModel.cs`, a state machine over `ConsoleKeyInfo` with no terminal
in it, so the steps and the key map are unit tested; the Termina page only
projects it. `agent-one setup --selftest` drives the real screen from a scripted
key source, and the release workflow runs it on every RID.

**Smart mode** (`--smart`, or Shift+Tab in chat) asks `IDecisionEngine` (Jev)
two fixed-option questions per turn, in `Agent/SmartRouter` — no planning LLM
call (a planner-generated option set cost 12–15 s and rarely separated; a fixed
one costs 0.3 s). **① Route**, before the loop: web / files / answer directly;
a confident choice is *enforced* — `AgentLoop.RunAsync(…, families)` refuses a
call outside the family rather than merely suggesting, because a small model
treats a suggestion as one option among many. **② Escalate**, after the
everyday model's draft: the engine sees the request, every tool result, the
draft and *both model names*, and if it says the problem needs more,
`Agent/ReasoningSubtask` hands the same material to the reasoning model (TUI
step 3, `AgentConfig.ForReasoning()`) and its answer goes back into the
everyday model's conversation as `[reasoning:<model>]` for that model to write
the final answer. Requests under `SmartRouter.MinRequestChars` (10) skip the
engine; a route steers only at or above `jevConfidenceFloor`, but escalation
follows the engine's choice alone (a two-option judgement call sat at 0.25 for
a plainly shallow draft — the strong model costs time, not correctness); a
failed engine or unreachable strong model leaves the turn as basic mode would
have run it.
`run` is one turn of the same `ChatSession`, so there is exactly one copy of
this flow.

**Chat has two faces over one pipeline.** `agent-one chat` in a terminal opens a
Termina window (`Tui/ChatTui*`: transcript in a `StreamingTextNode`, input line at
the bottom, mode in the header); piped or with `--plain` it is the line REPL in
`ChatCommand.RunPlainAsync`. Both are renderers over `Agent/ChatSession`, which
owns the loop, smart mode, the pause-for-a-person and the resume. Put a turn
rule in ChatSession, never in a renderer, or the two will drift. The window's
selftest boots it with scripted keys, runs one echo turn, then PageUp, with
**every key queued before the window starts**. Two Termina facts it encodes: a
key pushed into an *idle* `VirtualInputSource` is, under Native AOT, delivered
only when the loop next wakes for something else (2–9 s measured; real console
keys arrive in <100 ms, so only the selftest cares — hence never let the queue
go idle, which works because the echo turn completes inside the Enter
keystroke); and `StreamingTextNode` re-measures a line per cell it draws, so
`Tui/SoftWrap` folds every transcript line to the window width first (one
2,300-char answer line used to freeze the window for 9 s). ChatSession has its
own deterministic tests for pause/resume.

`SessionState` is actor-*shaped*, not Akka: one owner, serialised mutations,
snapshot reads. An actor runtime is exactly the dependency a Native AOT single
binary cannot afford — the same reason this project does not reference
ZeroCommon.

**Every stdin read goes through `Services/StandardInput`.** `Console.In` decodes
a redirected stream with the console code page, which turns piped Korean into
mojibake. That was fixed once in `run` and then reappeared in `chat`, which is
why there is now one reader instead of a fix per call site.

**The API key never goes in `config.json`.** It lives alone in
`~/.agent-one/credentials.json` (`CredentialStore`), and `ApiKey.Resolve` is the
single place that decides the order: stored key first, then `$apiKeyEnv`. The
`apiKeyEnv` field holds the NAME of a variable and now refuses anything that is
not one — a real incident had a key pasted there, where it silently did nothing
and surfaced only as an unexplained 401. `ConfigStore.Load` flags such a file and
`agent-one auth import` repairs it. `agent-one auth set` reads the key from a
hidden prompt or stdin, never from argv, so it stays out of shell history.

Two things about that selftest: step navigation and the picker are verified
**below** the UI, because arriving at step 2 starts an async listing during which
the screen ignores keys — a scripted walk would race it and fail at random. And
`ConfigTuiModel.StepFields` is the single source of truth for which key belongs
to which step; a test asserts every `AgentConfig.Keys` entry is owned by exactly
one step (with `model` being the Model step itself).

Three things that are easy to break here:

- **AOT means no reflection-based JSON.** Every serialized type is declared in
  `AgentOneJson` (config, indented) or `AgentOneWireJson` (wire / JSONL /
  `--json`, compact), and the csproj sets
  `JsonSerializerIsReflectionEnabledByDefault=false` so a stray
  `JsonSerializer.Serialize(obj, type, options)` is an IL2026/IL3050 **warning at
  build time** instead of a crash that only appears in the published binary.
- **`ToolCatalog` is the single source of truth** for the verbs — the system
  prompt is generated from it, `agent-one tools list` prints it, and
  `ToolCatalogTests` asserts the toolbelt answers every verb in it. Add a verb in
  one place only and the tests fail rather than the model getting confused.
- **`AGENT_ONE_HOME` is a process-wide environment variable**, so every test class
  that relocates it joins the `AgentOneHomeCollection` xUnit collection and they
  run one at a time. Add a class that sets it without joining, and unrelated
  config tests start failing in parallel runs for no visible reason.
- **The Windows AOT link step needs `vswhere.exe` on `PATH`** (
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer`) or a Developer
  prompt; Linux needs `clang` + `zlib1g-dev`. The release workflow
  (`.github/workflows/agent-one-release.yml`, tag `agent-one-v*`) handles both and
  smoke-tests each artifact before it reaches the release page.

Tools come in four families routed by `CompositeToolbelt` from each `ToolSpec`'s
`Family`: **files** (`list_files`, `read_file`, `find_files`, `grep`) and
**edit** (`write_file`, whole file, folders created) — both on
`LocalFileToolbelt`, sandboxed to `--root` and resolved through symlinks before
the containment check; **web** (`web_search`, `web_read`), GETs only; and
**exec** (`run_command`, `ShellToolbelt`: PowerShell on Windows, bash/sh
elsewhere, cwd = root, killed past `commandTimeoutSeconds`). The two families
that change something are `ToolCatalog.GuardedFamilies`, and a test keeps every
writing/running verb inside them. **Writes never leave the root**; a folder the
user names by absolute path (`Tools/PathGrants`) is granted for *reading* only,
for the session. **Commands go through a gate** (`ChatSession.GateAsync`):
`Agent/CommandRisk` patterns (rm -rf /, sudo, format, piped installers,
force-push…) always ask a person; otherwise Jev's safety question runs it only
on a *confident* `safe`; everything else is put to `ChatSession.Approver` — the
REPL reads a line, the window parks the turn on the input line, `run` refuses
unless `--yes`. Smart mode also sizes workspace work (`scope`, asked for a
workspace route and for an unsure one): a *confident* `needs_design` — it is a
steer, so the floor applies; "run the build" once got needs_design at 0.55 —
sends the request to the reasoning model for a design (`ReasoningSubtask.
DesignAsync`) that comes back as `[design:<model>]` for the everyday model to
build. The design's head is raised as `DesignMade` for the renderers; a design
that opens with `DECISION NEEDED:` + a numbered list (`ExtractDecision`) is put
to `ChatSession.Chooser` — REPL reads a line, the window parks the turn, `run`
takes the recommendation — and the pick rides into the feedback line. A turn
stopped by MaxSteps/Repeat after tool work gets `WrapUpAsync`: one no-tools
call for "done / left / next steps", the stop reason kept on the run. `maxSteps`
defaults to 50. **A broken tool envelope is never an answer**: `ToolCall.Repair`
escapes raw newlines/tabs and unknown backslash escapes inside JSON strings and
retries the parse (gemma's `write_file` with real newlines, a grep with `\.`);
what still fails gets the nudge, because `LooksLikeAnAnswer` refuses anything
shaped like an envelope — measured, a 2,564-char write_file was once shown to
the user as the answer and the file never written. `/status` (F2 in the window) prints `SessionStats` — task name, context size and
token estimate, Jev calls and ms, escalations, designs, approvals, memory size,
grants; `/new` starts a fresh session and log.

**The background session** (`Commands/SessionCommand`, `Agent/SessionServer`
+ `SessionClient`, `Services/SessionProtocol` + `SessionRegistry`): `session
start` spawns `agent-one session serve` detached (stdout/stderr → `logs/
session.log`), which holds one `ChatSession` behind a `NamedPipeServerStream`
named from a hash of the home dir and records pid/pipe/root in
`~/.agent-one/session.json`; one at a time, a dead pid is forgotten on load.
Protocol is JSON lines: `PipeRequest{op: ask|status|stop|answer}` in,
`PipeEvent{event: activity|step|delta|note|decided|title|design|ask|choose|
result|error}` out; `ask`/`choose` events wait for an `answer` line on the same
connection, which is how `Approver`/`Chooser` reach the CLI (`ask --yes`
approves up front). `agent-one ask` is the REPL's printing over the client;
`session selftest` runs both ends in-process on a private pipe with the echo
provider — the release workflow runs it. Note `ask` used to alias `run`; it no
longer does.

**A session belongs to its workspace** (`Services/WorkspaceStore`, under
`~/.agent-one/workspaces/<name>-<sha1[10]>/`): `memory.md` gets one entry per
turn (asked / did / outcome; `MemoryCapChars` 50 000, oldest entries dropped
at an entry boundary) and its newest `MemoryPromptChars` (6 000) open every
session's system prompt (`SystemPrompt.Build(root, memory)`); `sessions/` holds
the workspace's JSONL logs. `/resume` lists them (`SessionSummary`: title,
turns, first prompt) and `/resume <n>` calls `ChatSession.Resume(path)`, which
rebuilds the loop's context from prompt/result pairs (`AgentLoop.Restore`),
restores the last `title` entry, and keeps appending to the same file
(`SessionStore.Open`); the renderers replay the entries on screen. **Task
titles** (`Agent/TaskTitler`) are made by the everyday model from the request,
*beside* the turn (`RetitleAsync`, fire-and-forget on `_background` as the
turn starts — a greeting got "상담 시작 및 문의 응대" and a long build turn
left the header stale until it ended, so now only requests that pass
`SmartRouter.Applies` are named, and at the start): with an engine,
`SmartRouter.TaskSwitchedAsync` (same_task / new_task, choice only) gates the
naming call; without one the task is named once. `ChatSession.NamesTasks` is
the test switch — a naming call racing a test's assertions on provider calls
is the flake it prevents; `ScriptedChatProvider.TitleReplies` answers naming
calls (recognised by `TaskTitler.SystemPrompt`) so they never eat the turn's
scripted replies.

`grep` is plain substring, not regex, on purpose: the pattern comes from a model,
and a regex from an untrusted source hangs the process on backtracking. Search
uses DuckDuckGo's HTML endpoint (no key), so a markup change degrades to "no
results parsed" rather than to wrong results. `Tools/Web/HtmlText` strips markup
with source-generated regexes — block tags become newlines, inline tags vanish
with no space, or `Akka<b>.NET</b>` reads back as `Akka .NET`.

Tool output reaches the model as `[tool:<name>]` user messages and the system
prompt states it is data, not instructions — naming web pages explicitly, since
that is the one source written by strangers.

**Progress and streaming**: `AgentLoop` raises `ActivityStarted` per turn and
`AnswerDelta` per fragment; `ProgressDisplay` renders a live line on **stderr**
(rewritten in place on a TTY, one plain line per step when redirected) and the
answer streams to **stdout**, so pipes and `--json` are unaffected. Providers
take an optional `onDelta` — the OpenAI one then switches to SSE, and the
accumulated return value stays the truth while deltas are only a preview
(`AgentRun.Unstreamed` is what is left to print). `FinalAnswerStreamer` decodes
the `text` field out of the JSON envelope as it arrives, and deliberately streams
**nothing** for a tool call, because `grep` also has a `text` argument and
printing a search pattern as the answer would be a plausible-looking lie.

## Ancestor reference — AgentWin (Origin)

AgentZeroLite was forked from `D:\Code\AI\AgentWin` (the **Origin** project). When the user mentions *"오리진"*, *"AgentWin"*, *"조상 프로젝트"*, *"the ancestor"*, or asks to *"compare with origin"* / *"오리진이랑 비교"* / *"오리진 참고"*, **read `Docs/agent-origin/` first** instead of crawling the Origin codebase from scratch:

- `Docs/agent-origin/README.md` — Executive summary + adoption priority table (P0~P3)
- `Docs/agent-origin/01-stack-comparison.md` — Per-item spec table (NuGet, csproj, DB, CLI/IPC, build)
- `Docs/agent-origin/02-architecture-comparison.md` — Diagrams + branching rationale (actors, LLM gateway, terminal, harness)
- `Docs/agent-origin/03-adoption-recommendations.md` — Adoption roadmap with cost & trade-offs

These docs are a 2026-04-27 snapshot. If they look stale (e.g. > 6 months) or the user asks about a topic not covered, re-survey `D:\Code\AI\AgentWin` directly and **update the relevant `Docs/agent-origin/*.md` file** so the snapshot stays useful for future sessions.

## Reference projects — read the analysis doc before crawling the clone

External codebases surveyed for adoption. Each has a `Docs/agent-<name>/` doc set;
**read it first**, and re-survey the clone only when the snapshot looks stale or the
topic is not covered — then update the doc so the next session inherits the answer.

| Mentioned as | Doc set | Clone (read-only, never copied into the repo) |
|---|---|---|
| orca, ADE, 병렬 에이전틱 IDE | `Docs/agent-orca/` | `E:\git-other\orca` |
| herdr | `Docs/agent-herdr/` | — |
| netclaw, Termina, TUI, AOT TUI | `Docs/agent-netclaw/` | `C:\code\psmon\research\netclaw` |
| CodeScan (agent-one's skeleton) | — (see `Project/AgentOne/README.md`) | `C:\code\psmon\CodeScan` |
| Jev, TypeSafe, smart mode, System One | `Project/AgentOne/docs/smart-mode-jev.md` | — (hosted API, docs only) |

`C:\code\psmon\research\` is where reference clones for analysis live.

**One finding worth not re-deriving**: `Docs/agent-netclaw/README.md` records a
*measured* result — **Termina 0.16.2 publishes under Native AOT with zero trim
warnings and the published binary actually runs** (5.19 MB probe, headless via
`VirtualInputSource`). Terminal.Gui was not shown to do this: CodeScan ships it
with `-p:TrimMode=""` and a 112 MB non-trimmed binary. So a TUI added to
`Project/AgentOne` uses Termina, not Terminal.Gui. Only win-x64 was measured.
