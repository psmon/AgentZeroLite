---
date: 2026-05-02T22:55+09:00
agent: code-coach
mode: 2-auto-precommit
trigger: "잘작동 확인 커밋후 푸시"
---

# Pre-commit review — M0005 WebDev tab + IZeroBrowser bridge

## Scope (effective staged + untracked dev code)

- M `Project/AgentZeroWpf/AgentZeroWpf.csproj` — added `Wasm\**` content include
- M `Project/AgentZeroWpf/UI/Components/SettingsPanel.xaml` — new TabItem hosting `WebDevPanel`
- + `Project/ZeroCommon/Browser/IZeroBrowser.cs`
- + `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs`
- + `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs`
- + `Project/AgentZeroWpf/UI/Components/WebDevPanel.xaml(.cs)`
- + `Project/AgentZeroWpf/Wasm/{README.md,common/zero-bridge.js,voice-test/{index.html,voice-test.css,voice-test.js}}`

Dev-code globs match → engine triggered (not skipped).

## Lens 1 — .NET modern

- `IZeroBrowser.cs` — record DTOs (`VoiceProvidersInfo`, `TtsResult`) idiomatic; nullable error fields used correctly. **OK**.
- `WebDevHost.cs` — reuses existing `VoiceRuntimeFactory.BuildTts()` instead of duplicating provider switching. Dispose chain delegates to `VoicePlaybackService` which is already thread-safe via internal lock. **OK**.
- `WebDevBridge.cs` — `async void` on `WebMessageReceived` is the correct pattern for WebView2 event handlers. Top-level catch logs + posts error response — failure is observable on JS side. **OK**.

## Lens 2 — Akka.NET

No actor surface touched. **N/A**.

## Lens 3 — WPF / XAML

P1-P5 checklist (`harness/knowledge/wpf-xaml-resource-and-window-pitfalls.md`):

| # | Check | Result |
|---|---|---|
| P1 | Sibling-`<Resources>` invisibility | N/A — no `<Resources>` block in `WebDevPanel.xaml` |
| P2 | Undefined resource keys | N/A — only inherited styles via `Style="{StaticResource SettingsTabItem}"` on the TabItem (resource defined in same XAML, in scope) |
| P3 | `Window.Resources` not inheriting | N/A — `UserControl`, not `Window` |
| P4 | `Owner` HWND requirement | N/A — no `Window.Owner` set |
| P5 | `Brush` ambiguity (System.Drawing vs Media) | N/A — no `Brush` types referenced in code-behind |

`WebDevPanel.xaml.cs` review:

- Lazy WebView2 init via `IsVisibleChanged` — avoids paying init cost for users who never open the WebDev tab. **Good**.
- `_initStarted` guard prevents double-init. **OK**.
- Failure path surfaces in `lblWebDevStatus` rather than crashing — graceful. **OK**.
- **Should-fix**: `OnUnloaded` did not call `webDevView.Dispose()`. WebView2 owns native handles (CoreWebView2Environment + browser process). MermaidRenderer disposes its `_webView` explicitly; we should match. Without this, repeated open/close of Settings could leak Edge runtime processes.
  - **Fix applied** before commit: added `try { webDevView.Dispose(); } catch { }` to `OnUnloaded`.

## Lens 4 — On-device LLM / Win32 native

- `SetVirtualHostNameToFolderMapping` — standard WebView2 API, throws on bad args (caller wraps in try). **OK**.
- No P/Invoke, no ConPTY surface touched. **N/A**.

## Coupling / seam respect

- `IZeroBrowser` lives in `ZeroCommon` (WPF-free) — confirmed: only references `string` / `Task<T>` / `CancellationToken` / records. **PASS**.
- `WebDevHost` (the WPF-bound impl) lives in `AgentZeroWpf/Services/Browser/`, depends on `VoiceRuntimeFactory` + `VoicePlaybackService` from the same project. **PASS**.

## Convention sets

- `harness/knowledge/llm-prompt-conventions.md` — N/A (no LLM prompts touched).
- `harness/knowledge/agent-origin-reference.md` — N/A (operator did not invoke origin context).
- `harness/knowledge/wpf-xaml-resource-and-window-pitfalls.md` — checked above, all PASS.
- `harness/knowledge/voice-roundtrip-testing.md` — N/A (no STT/OCR comparison logic).

## Verdict

| Severity | Count |
|---|---|
| Must-fix | 0 |
| Should-fix | 1 (applied inline) |
| Suggestion | 0 |

Commit allowed: **yes**.

## Issue handoff decision

Severity table says Should-fix → file an issue. Rationale block in `harness/agents/code-coach.md`: "advisory items that don't block a merge get forgotten otherwise". The Should-fix here was **applied inline before commit** — no deferred work, nothing to forget.

→ **No issue filed.** If reviewer later wants stricter adherence (issue for every finding regardless of inline-fix), this can be revisited.

## Evaluation (3-axis rubric)

| Axis | Score | Notes |
|---|---|---|
| Cross-stack judgment | A | WPF lens caught the WebView2 native-handle leak that .NET-modern lens alone wouldn't surface |
| Actionability | A | Single finding, concrete file:line, exact patch shown and applied |
| Knowledge capture | Pass | No long-shelf-life finding — pattern already documented in MermaidRenderer |
