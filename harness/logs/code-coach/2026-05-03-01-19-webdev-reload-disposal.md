---
mode: 2 (auto pre-commit)
verdict: pass (0 findings)
issue_filed: no (pure-pass)
commit_blocked: no
---

# WebDev panel — disposal moved off Unloaded

## Scope

Staged diff: `Project/AgentZeroWpf/UI/Components/WebDevPanel.xaml.cs`
(+23 / −7).

Fix for `ObjectDisposedException` on `OnWebDevReload` /
`OnWebDevDevTools` after the user toggled the Voice tab to change the
TTS provider and returned to the WebDev tab. Reproduced in
`bin/Debug/.../logs/app-log.txt` line 320, 342, 379, 401, 424, 446,
574, 596, 618 — the WebView2 instance is disposed by `UserControl.Unloaded`
on tab switch, after which the next `webDevView.CoreWebView2` access
throws (the property does not return null when disposed).

## Review against the four lenses

### .NET modern
- Plain event sub/unsub on `Window.Closed`; field-flag idiom for
  one-shot subscription. Nullable annotations preserved. No issues.

### Akka.NET
- N/A.

### WPF
- `Loaded` is the correct point to resolve the host window —
  `Window.GetWindow(this)` is reliable once the control is in the
  tree.
- `_windowClosedHooked` guard prevents double-subscribe if the panel
  is detached and re-attached (which is exactly what the Settings
  TabControl already does on every tab switch).
- Tab cycling now leaves WebView2 alive — that is the desired
  behaviour, since WebView2 init is expensive (CoreWebView2Environment
  + CoreWebView2InitializationCompleted async) and the user data
  folder under `%TEMP%/AgentZeroLite_WebDev` is shared anyway.
- WPF-XAML pitfalls checklist (P1–P5) does not apply: no XAML edits.

### LLM integration / Windows native
- No interaction with LLamaSharp, ConPTY, or Win32 P/Invoke.
- WebView2 lifecycle: deferring disposal to `Window.Closed` matches
  the runtime model (the WebView2 process is bound to the
  CoreWebView2Environment for the lifetime of the host); explicit
  `Dispose()` on shutdown is still wired so the user-data folder gets
  released cleanly.

## Resource analysis

- `WebDevHost._playback` (NAudio `WaveOutEvent`) is created once and
  cycled per `Play()` — Stop()/Play() reset the device, so leaving the
  host alive across tab switches is not a leak.
- `WebDevBridge` is just a `WebMessageReceived` handler attached to
  `CoreWebView2`; staying attached is the intended state. `Detach()`
  fires only on window close.
- Window-Closed closure roots the panel for the window's lifetime.
  Panel and window share that lifetime, so no observable leak.

## Findings

None.

## Outcome

Commit approved. No GitHub issue (pure-pass per policy).
