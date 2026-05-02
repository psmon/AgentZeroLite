---
mode: 2 (auto pre-commit)
verdict: pass (0 findings)
issue_filed: no (pure-pass)
commit_blocked: no
---

# WebDev — TextLLM chat-test sub-app + dark combo + reload-fix

## Scope

Six files staged (+374 / −10):

- `Project/ZeroCommon/Browser/IZeroBrowser.cs` (+26)
- `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs` (+102)
- `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs` (+50)
- `Project/AgentZeroWpf/Wasm/common/zero-bridge.js` (+50)
- `Project/AgentZeroWpf/UI/Components/WebDevPanel.xaml` (+101)
- `Project/AgentZeroWpf/UI/Components/WebDevPanel.xaml.cs` (+45)

New folder: `Project/AgentZeroWpf/Wasm/chat-test/` (untracked,
will land in same commit) — index.html / chat-test.css /
chat-test.js: streaming chatbot test sandbox.

## Review against the four lenses

### .NET modern
- `IAsyncEnumerable<string> ChatStreamAsync(...)` correctly carries
  `[EnumeratorCancellation]` on the CT parameter so caller-supplied
  tokens flow through.
- `_chatLock` (SemaphoreSlim) serializes Send/Stream/Reset on the
  host-shared session; correct because `ILocalChatSession` is not
  reentrancy-safe (KV cache mutation on Local backend).
- Lazy session via `??=` under the lock; Reset disposes + nulls so
  next call re-opens.
- Fire-and-forget patterns (`_ = StreamChatAsync`, `_ = SwitchEntryAsync`)
  are both wrapped at the outer level with try/catch + log/event
  posting — no exceptions are swallowed silently.
- Synchronous Dispose path uses
  `DisposeAsync().AsTask().GetAwaiter().GetResult()`. Acceptable
  here because Dispose runs from `Window.Closed` (single fire, end
  of process) and the underlying chat-session DisposeAsync doesn't
  marshal back to the UI thread (External: HttpClient release;
  Local: KV cache release on the LlmService dispatcher).

### Akka.NET
- N/A — no actor topology touched.

### WPF
- DarkComboBox + DarkComboBoxItem styles inlined inside
  `WebDevPanel.UserControl.Resources`. Correct: SettingsPanel's
  same-named StaticResource is invisible across UserControl parse
  scopes (P1 in `wpf-xaml-resource-and-window-pitfalls.md`).
- `SwitchEntryAsync` handles three timing windows:
  - SelectionChanged during InitializeComponent (initial
    `IsSelected="True"`) — `_initStarted == false`, just stages
    the path; upcoming Init Navigate uses it.
  - User picks combo while init is in flight — joins the in-flight
    init via `EnsureCoreWebView2Async` (idempotent).
  - User picks combo post-init — straight Navigate.
- `lblWebDevApp is not null` guard catches the (theoretical) case
  where SelectionChanged fires before x:Name binding completes.
- Disposed-getter trap from prior fix not reintroduced — all
  `webDevView.CoreWebView2` access is either inside try/catch in
  the Reload/DevTools handlers (still trivial null-check with
  acceptable surface — the host window's Closed event was the real
  fix and remains intact) or inside `await EnsureCoreWebView2Async()`
  in SwitchEntryAsync.

### LLM integration
- Routes through canonical `LlmGateway.OpenSession()` — no
  duplicate session-construction logic.
- One persistent session per `WebDevHost` instance (lifetime tied
  to the host window). Turn history preserved across calls; user
  can reset via `chat.reset` op.
- Note (not a finding): if the user toggles Local↔External in
  Settings/LLM mid-conversation, the cached session keeps the old
  backend until Reset. Documented sandbox behavior — the chat-test
  page exposes a Reset button for exactly this. Could add a
  status-poll on focus in a future iteration; out of scope for v1.

### JS bridge
- Event-channel pattern: `on(channel, handler) → unsubscribe`
  closure. Subscribers stored in `Map<channel, Set<handler>>`.
- `chatStream` correctly unsubscribes both `chat.token` and
  `chat.done` on resolve, reject, and outer invoke failure (three
  paths). Late events after unsubscribe are silent no-ops.

## Findings
None.

## Outcome
Commit approved. No GitHub issue (pure-pass per policy).
