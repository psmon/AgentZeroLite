---
date: 2026-09-18T20:56:00+09:00
agent: code-coach
type: creation
mode: execution
trigger: "M0032 수행해 — 웨어러블 호스트 AI 영역 강화 (파일 허용목록·open_file·웹 검색/탐색, 별도 액터 모델)"
target: Project/ZeroCommon (Wearable/, Web/, Llm/Tools/), Project/ZeroWearable, Project/AgentZeroWpf (Browser page, -cli web)
engine: mission-dispatch (tamer → code-coach → security-guard → test-runner)
---

# M0032 — wearable file / web tools as an actor subtree

## 실행 요약

The brief asked for two tool families on the watch's agent — allow-listed file
search / analysis / open, and web search / browsing through the GUI's WebView — and
the operator's follow-up directive fixed the shape: **a separate actor model, modelled on
the main app's LLM tool-chain actor**. Four decisions were made first, then the code
followed them.

1. **Reuse `AgentLoopActor` unchanged.** It is WPF-free, already owns one `IAgentLoop`
   with the Idle→Thinking→Generating→Acting→Done FSM and reports to `Context.Parent`. The
   wearable needs one thing it lacks — newest-question-wins (a watch cancels the old
   question with the new one, while `AgentLoopActor` ignores a Start while Running) — so
   that lives in the new parent, `WearableAgentActor`, as cancel + queue-until-idle.
2. **Put the actors in ZeroCommon, not ZeroWearable.** Nothing in them is WinRT; the
   host only composes them. That is what makes `WearableAgentActorTests` (TestKit) run in
   the headless suite — `AgentTest` cannot reference the `net10.0-windows10.0.19041.0`
   host project.
3. **Extend the shared catalog on purpose.** `open_file`, `web_search`, `web_open`,
   `web_read` go into `AgentToolGrammar` (GBNF + `KnownTools` + prompt) and both loops'
   dispatch switches; `IAgentToolbelt` gets "not available" defaults so every host and
   test double keeps compiling. The main app implements the verbs too
   (`WorkspaceTerminalToolHost`). `AgentToolCatalogTests` pins the three places together.
4. **No new IPC.** The host reaches the GUI's Browser page through
   `AgentZeroLite.exe -cli web …` (WM_COPYDATA + MMF), the same channel `bot-chat` uses.
   The GUI handler answers asynchronously, which needed two guards the older commands
   never did (below).

## 결과

### Topology (host ActorSystem `AskBot`)

```
/user/chat                 ChatActor            — device gateway only (BLE / AskBot / voice)
/user/agent                WearableAgentActor   — sessions, routing, newest-question-wins
    /files                 FileToolActor        — AllowedRootResolver + AllowedRootFileTools + open_file
    /web                   WebToolActor         — GuiCliWebToolSurface → HeadlessWebToolSurface
    /session-<key>         AgentLoopActor       — reused; one IAgentLoop per conversation
```

`WearableToolbelt` became an `Ask` adapter over `/files` and `/web`. `ChatActor` sends
`WearableAgentActor.Ask` and receives `Progress` / `Answer` messages; the old
`brain.AskAsync` Task path remains only for the CLI brain (a whole agent of its own).
`AgentZeroWearable.exe --ask` builds the same subtree on a local, non-remoting
ActorSystem, so the panel's "Test brain" exercises the actor path.

### Files

| Area | Files |
|---|---|
| Allow-list | `ZeroCommon/Wearable/WearableSettings.cs` (`AllowedRoot`, `AllowedRoots`, `WebToolsEnabled`, `WebMaxChars`, `GuiExePath`, `Normalize()` migration), `WearableSettingsStore.cs`, `Llm/Tools/AllowedRootResolver.cs`, `AllowedRootFileTools.cs`, `FileOpenPolicy.cs`, `ToolJson.cs`, `FileToolCore.TryResolveInsideRoot` |
| Web | `ZeroCommon/Web/WebPageExtractor.cs`, `WebSearchParser.cs`, `HeadlessWebFetcher.cs`, `IWebToolSurface.cs`, `HeadlessWebToolSurface.cs`, `GuiCliWebToolSurface.cs` |
| Catalog | `AgentToolGrammar.cs`, `IAgentToolbelt.cs`, `LocalAgentLoop.cs`, `ExternalAgentLoop.cs` |
| Actors | `ZeroCommon/Wearable/Actors/WearableAgentActor.cs`, `FileToolActor.cs`, `WebToolActor.cs`, `Wearable/WearableToolbelt.cs`, `WearablePromptFrame.cs` |
| Host | `ZeroWearable/Agent/WearableBrain.cs` (`WearableAgentPlan`, factory split), `Actors/ChatActor.cs`, `Program.cs` (`SpawnAgent`, `AskAgent` via `Inbox`, `LogTools`); `Agent/WearableToolbelt.cs` removed (moved) |
| GUI | `AgentZeroWpf/UI/Components/BrowserPagePanel.xaml(.cs)`, `MainWindow.xaml(.cs)` (`AppPage.Browser`, `HandleWebCommand`), `CliHandler.cs` (`web` group, `TryReadMmf(accept)`, timeout flag), `Services/Browser/WebCliCommands.cs`, `BrowserToolSurfaceRegistry.cs`, `Services/WorkspaceTerminalToolHost.cs`, `UI/Components/WearablePagePanel.xaml(.cs)` (allow-list editor, web toggle), `ZeroCommon/Module/IpcMemoryMappedResponseWriter.Clear` |
| Docs | `CLAUDE.md`, `harness/knowledge/_shared/agent-architecture.md`, `harness/knowledge/security-guard/security-surface.md`, `.claude/skills/agentzero-cli/references/command-reference.md`, `codex/prompts/agentzero-cli.md`, `ZeroCommon/Agents/AgentSkillGuides.cs` |

### Two things found by running it, not by reading

- **Async IPC reply vs. a polling CLI.** `TryReadMmf` returns the first non-empty map it
  sees, and the maps live for the GUI's lifetime. For the synchronous handlers that is
  fine (the reply is written before `SendMessageTimeout` returns); for `web` it would
  have returned the *previous* page. Fix: `IpcMemoryMappedResponseWriter.Clear` inside
  the handler (synchronous, before the CLI can poll) plus a `req` id the CLI insists on.
- **`_ = RunWebCommandAsync(...)` is not asynchronous enough.** It ran synchronously to the
  first real await, and the first tab's WebView2 initialisation has a multi-second
  synchronous stretch — the CLI's 3 s `SendMessageTimeout` fired while the page was in
  fact opening (wm-copydata pitfall P2 in a new coat). Fix: `Dispatcher.InvokeAsync(…,
  Background)` so WndProc returns before any work. Cold `web open` now completes within
  the CLI's budget.

### Verification

| Check | Result |
|---|---|
| `dotnet build` ZeroCommon / ZeroWearable / AgentZeroWpf (Debug) | 0 errors |
| `ZeroCommon.Tests` full suite | 787 passed, 0 failed, 24 skipped (LLM-gated) |
| `AgentTest` full suite | 146 passed, 0 failed, 7 skipped |
| `--ask` (External brain, gemma-4-e4b @ Webnori) | actor path: `/user/agent/session-…` created, answer returned |
| `--ask` web search, GUI down | `[web/debug] … GUI is not running` → headless fallback → correct one-sentence answer about the repo |
| `--ask` with scratch allow-list (`docs` ro) | Korean summary of the meeting notes' three decisions |
| `-cli web open / read / tabs / search`, GUI up | all four return `ok:true` JSON with `req` echo; two tabs visible in the Browser page |

### Not done / deferred (see the GitHub issue)

- The watch firmware shows a single "thinking" state and ignores stage text, so tool
  progress is a `think` keep-alive on each `Acting` phase plus a host-log line; a
  per-tool caption on the watch is a firmware change in the sibling repo.
- Search engine is DuckDuckGo HTML only; a bot check yields a clear `ok:false`.
- Junction / symlink escape inside an allowed root is not resolved (pre-existing property
  shared with the main app's workspace sandbox).

## 평가

| Axis | Grade | Why |
|---|---|---|
| Cross-stack judgment | A | Akka (parent/child routing, PipeTo, TestKit), WPF (WebView2 init on the UI thread, Pitfall 6 templates), Win32 IPC (MMF lifetime, WndProc P2) and LLM (GBNF catalog, prompt budget, untrusted-content framing) all had a decision that mattered. |
| Actionability | A | Every finding above names the file and the concrete change; the two runtime findings were fixed in the same pass. |
| Research depth | n/a | Execution, not a consult. |
| Knowledge capture | Pass | `agent-architecture.md` (verbs + wearable topology), `security-surface.md` (new boundaries), CLAUDE.md wearable section rewritten. |
| Issue handoff | Pass | Advisory items filed as one `enhancement` issue: https://github.com/psmon/AgentZeroLite/issues/19 |

## 다음 단계 제안

- Firmware: render `Progress.Text` (tool name) on the watch's think screen — sibling repo.
- `AgentTest` could host a WebView2-backed test of `BrowserPagePanel.ReadAsync` against a
  `data:`-less local page (virtual host mapping) once a desktop-session test pattern for
  WebView2 exists.
