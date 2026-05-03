# Project/Plugins/ — Third-party plugin SDK

External WebDev plugins shipped alongside the AgentZero Lite repo but
**not compiled into the app**. The `AgentZeroWpf.csproj` only includes
its own folder tree; nothing in `Project/Plugins/` is touched by the
build, so a broken or experimental plugin can never block a release.

A plugin is a static web app (HTML + JS + CSS, no build step required)
that runs inside the WebDev WebView2 panel and reaches the host through
a JS bridge — **`window.zero.*`** — getting access to the LLM, voice
(STT/TTS/VAD), token telemetry, and other native services AgentZero Lite
exposes.

---

## Quick start — your first plugin (3 files)

```
Project/Plugins/hello/
├── manifest.json
├── index.html
└── hello.js
```

**`manifest.json`**
```json
{
  "id": "hello",
  "name": "Hello",
  "entry": "index.html",
  "version": "0.1.0",
  "icon": "👋",
  "description": "Smallest possible AgentZero plugin"
}
```

**`index.html`**
```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Hello</title>
  <!-- The host mounts every plugin under https://plugin.local/<id>/ and
       publishes the shared bridge at https://zero.local/common/. -->
  <script src="https://zero.local/common/zero-bridge.js"></script>
</head>
<body>
  <h1 id="title">…</h1>
  <button id="speak">Speak it</button>
  <script src="hello.js"></script>
</body>
</html>
```

**`hello.js`**
```js
(async () => {
  if (!window.zero) {
    document.body.textContent = "Open this from inside AgentZero Lite (WebDev menu).";
    return;
  }
  const { version } = await window.zero.version();
  document.getElementById('title').textContent = `Hello from AgentZero ${version}`;
  document.getElementById('speak').onclick = () => window.zero.voice.speak('Hello.');
})();
```

Install the folder via **WebDev → + Install Plugin → From Git URL…** (or
ZIP it). The viewer will mount it at `https://plugin.local/hello/index.html`
and call into the host every time you click the button.

---

## The `window.zero` API surface

Everything below is exposed by `Project/AgentZeroWpf/Wasm/common/zero-bridge.js`
(JS wrapper) and dispatched by `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs`
(.NET host). All methods return `Promise`s. All events are subscribed via
`zero.on(channel, handler)` and return an `unsubscribe()` function.

> **No HTTP / no network**. The bridge is a `chrome.webview.postMessage`
> JSON-RPC channel — `{ id, op, args }` ↔ `{ id, ok, result, error }` —
> entirely in-process. Your plugin cannot be MITMed, and equally cannot
> reach external services through the bridge (use `fetch` for that).

### 1) Core

| Call | Returns | Notes |
|---|---|---|
| `zero.version()` | `{ version }` | App SemVer (matches `Project/AgentZeroWpf/version.txt`). |
| `zero.invoke(op, args)` | `Promise<any>` | Low-level RPC; the namespaced helpers below are sugar over this. |
| `zero.on(channel, handler)` | `() => void` | Subscribe to a host-pushed event. Returns an unsubscribe fn. |

### 2) LLM (chat)

The host owns the active LLM provider — local Gemma 4 / Nemotron Nano,
or any of the OpenAI-compatible backends configured in Settings. Plugins
just talk to whichever is active.

| Call | Returns / events | Notes |
|---|---|---|
| `zero.chat.status()` | `{ available, backend, model, detail }` | Shows the active backend and a one-line `detail` (warm-up state, error). Call this on plugin load to gate UI. |
| `zero.chat.send(text)` | `{ ok, reply, turn, error }` | Synchronous full-reply turn. Maintains conversation state inside the host. |
| `zero.chat.stream(text, onToken)` | `Promise<void>` (resolves on `chat.done`) | Streams tokens via `onToken(string)` callbacks. Internally subscribes to the `chat.token` / `chat.done` events; resolves/rejects when the stream closes. |
| `zero.chat.reset()` | `{ reset: true }` | Drops the conversation history. New `send`/`stream` start a fresh turn. |

```js
// Streaming example — a typing-effect chat reply
await zero.chat.stream('Summarise the README in 3 bullets.', (tok) => {
  out.textContent += tok;
});
```

> See the built-in **`chat-test`** sandbox under `AgentZeroWpf/Wasm/chat-test/`
> for a complete one-screen reference UI.

### 3) Voice — TTS, mic capture, summarization

#### 3a) TTS (text → speech)

| Call | Returns | Notes |
|---|---|---|
| `zero.voice.providers()` | `{ stt, tts, llmBackend }` | Active provider names. Use to label your UI. |
| `zero.voice.speak(text)` | `{ ok, provider, bytes, format, error }` | Plays through whichever TTS the user configured (Win11 SAPI neural by default — free, OS-level). |
| `zero.voice.stop()` | `{ stopped: true }` | Cancels the currently-playing utterance. |

#### 3b) VAD-gated mic capture (`note.*`) — auto-transcribed utterances

This is the same engine the voice-note plugin uses. The host runs Voice
Activity Detection on the mic; each utterance is auto-transcribed by the
active STT provider (Whisper.net by default). Your plugin **never sees raw
audio** — only finished transcript strings + meter telemetry.

| Call | Returns | Notes |
|---|---|---|
| `zero.note.start(sensitivity?)` | `{ ok, capturing, sensitivity, threshold }` | `sensitivity` is 0..100 (omit to use Settings/Voice's stored value). |
| `zero.note.stop()` | `{ ok }` | Releases the mic. |
| `zero.note.pause()` / `zero.note.resume()` | `{ paused }` | Quick mute without releasing the mic. |
| `zero.note.setSensitivity(value)` | `{ sensitivity, threshold }` | Adjust at runtime; matched VAD threshold returned. |
| `zero.note.status()` | `{ capturing }` | Cheap probe. |

**Events** (all via `zero.note.on*(handler)`):

| Event | Payload | When |
|---|---|---|
| `onTranscript` | `{ text }` | A full utterance has been transcribed. |
| `onUtteranceStart` | `{}` | VAD believes the user just started speaking. |
| `onUtteranceEnd` | `{}` | Trailing-silence boundary hit; STT is about to fire. |
| `onSpeaking` | `{ speaking: bool }` | Frame-level VAD decision (~50 Hz). For instant level meter colouring. |
| `onAmplitude` | `{ rms, threshold }` | RMS amplitude (~10 Hz). `threshold` rides every tick so a meter can draw the "must be this loud" line. |
| `onError` | `{ message }` | Mic / STT failure surface. |

```js
zero.note.onTranscript(({ text }) => log.append(text));
zero.note.onAmplitude(({ rms, threshold }) => {
  meter.style.width = (rms * 100) + '%';
  meter.classList.toggle('above', rms > threshold);
});
await zero.note.start(75);   // 75% sensitivity
```

#### 3c) Summarization

| Call | Returns | Notes |
|---|---|---|
| `zero.summarize(text, maxChars?)` | `{ ok, summary, inputChars, chunks, error }` | LLM summarization with **automatic length-chunked recursion** — long inputs are halved on a sentence boundary, summarised in parallel, then merged. `maxChars` defaults to 6000. |

This is a thin wrapper over `zero.chat.*` aimed at one-shot text → text;
useful when you don't want to manage conversation state. The voice-note
plugin uses it as the "Summarise" button.

> The built-in **`voice-test`** sandbox under `AgentZeroWpf/Wasm/voice-test/`
> wires up TTS / STT / chat in a single screen for end-to-end testing.

### 4) Token telemetry (`tokens.*`) — read-only

The host runs an internal collector that polls Claude Code
(`~/.claude*/projects/**/*.jsonl`) and Codex CLI
(`~/.codex/sessions/**/rollout-*.jsonl`) on a 10-minute cadence and persists
per-turn rows in AgentZero's SQLite DB. Multiple Claude profiles
(`CLAUDE_CONFIG_DIR` siblings) are auto-discovered; account is pinned per
file at first ingest.

Plugins query the cumulative dataset (read-only, no write surface beyond
aliases). Subscribe to `tokens.tick` for live refresh after each cycle.

| Call | Returns | Notes |
|---|---|---|
| `zero.tokens.summary(sinceHours?)` | `{ range, totals, byVendor, collector }` | Cards-shaped one-shot. |
| `zero.tokens.byVendor(sinceHours?)` | `[{ vendor, input, output, cacheCreate, cacheRead, reasoning, total, records }]` | |
| `zero.tokens.byAccount(sinceHours?)` | `[{ vendor, accountKey, … same fields … }]` | `accountKey` = Claude profile dir name (`claude`, `claude-qa`) or Codex `plan_type:cli_version`. |
| `zero.tokens.byProject(sinceHours?, limit?)` | `[{ project, pathSample, vendors, …, total, sessions, lastSeen }]` | `project` = leaf folder name of `cwd`. |
| `zero.tokens.timeseries(rangeHours, bucketMinutes)` | `[{ bucketUtc, vendor, input, output, total }]` | For charts. |
| `zero.tokens.sessions(sinceHours?, limit?)` | `[{ vendor, sessionId, cwd, model, lastSeen, …, records }]` | Most-recent sessions. |
| `zero.tokens.recent(limit?)` | Per-row recent rows (default 50). | For "live tail" UIs. |
| `zero.tokens.profiles()` | `[{ vendor, accountKey, fileCount, lineCount, lastUpdatedUtc }]` | Per-checkpoint summary. |
| `zero.tokens.aliases()` | `[{ id, vendor, accountKey, alias, updatedAt }]` | User-assigned friendly names. |
| `zero.tokens.setAlias(vendor, accountKey, alias)` | `AliasRow` | UPSERT. Empty `alias` is **not** a delete — call `removeAlias`. |
| `zero.tokens.removeAlias(vendor, accountKey)` | `{ removed: bool }` | |
| `zero.tokens.refresh()` | `TickSummary` | Force a one-off out-of-band collector tick. |
| `zero.tokens.reset()` | `{ rowsDeleted, checkpointsDeleted }` | Wipes records + checkpoints (UI confirmation strongly recommended). |
| `zero.tokens.status()` | `CollectorState` | Cheap probe. |

**Event**: `zero.tokens.onTick((s) => …)` fires after every collector cycle.
Payload: `{ filesScanned, rowsInserted, claudeRows, codexRows, finishedAt, error }`.

```js
const refresh = async () => {
  const s = await zero.tokens.summary(24);
  cardTotal.textContent = s.totals.total.toLocaleString();
};
zero.tokens.onTick(refresh);
refresh();
```

> The **`token-monitor`** plugin (shipped) is a complete reference for
> this surface — cards, vanilla-canvas chart, alias inline rename, and
> the `tokens.tick` auto-refresh wiring.

---

## Shipped plugins (catalogue)

| Plugin | Status | What it demonstrates |
|---|---|---|
| `voice-note/` | shipped (M0008) | `note.*` VAD-gated capture + `summarize` chunked recursion. 3-tier IndexedDB storage (raw timeline / summary / meta). Per-utterance auto-transcript stream. |
| `token-monitor/` | shipped (M0009) | `tokens.*` read-only dashboard. Cards, vanilla-canvas time-series chart, by-vendor/account/project tables, alias inline rename, Reset & Re-scan, `tokens.tick` auto-refresh. |

## Built-in reference samples (in-app, not in `Project/Plugins/`)

These ship inside the exe under `Project/AgentZeroWpf/Wasm/` and serve as
canonical minimal references for the bridge. Open the WebDev sample list
in a running app to view their source through DevTools (right-click → Inspect).

| Sample | Demonstrates |
|---|---|
| `chat-test/` | `chat.status / send / stream / reset` end-to-end. Backend label, model name, turn counter, streaming reply rendering. |
| `voice-test/` | `voice.providers`, `voice.speak`, plus a single-screen STT → LLM → TTS loop. Free Win11 OS-level voice path. |

---

## `manifest.json` contract

Strict validator: `Project/AgentZeroWpf/Services/Browser/PluginManifest.cs`.

```json
{
  "id": "voice-note",
  "name": "Voice Note",
  "entry": "index.html",
  "version": "0.1.0",
  "description": "STT-driven voice journal with summary",
  "icon": "🎙"
}
```

| Field | Required | Rule |
|---|---|---|
| `id` | yes | Lower-case kebab-case, 1–63 chars, leading letter or digit. Becomes the URL segment and install dir name. |
| `name` | yes | Display name in the WebDev sample list. |
| `entry` | yes | Relative `.html` path inside the plugin folder. No `..`, no `\`, no leading `/`. |
| `version` | no | Free-form (SemVer recommended). Surfaced in the install dialog and sample list. |
| `description` | no | One-liner shown in the install dialog metadata panel. |
| `icon` | no | Single emoji (rendered next to the name). |

A folder name disagreeing with `manifest.id` logs a warning but the folder
name wins for the install path.

---

## Installing — three channels

End users install plugins from the running app via **WebDev → + Install
Plugin** (globe icon → bottom-left). The dialog has three sections:

### 1. Official catalogue (auto-discovered)

A combo at the top that runtime-discovers every subfolder of
`psmon/AgentZeroLite/Project/Plugins/` via the GitHub Trees API and
fetches each `manifest.json` for the metadata panel. **Nothing is
hardcoded** — pushing a new plugin to that folder makes it appear in the
combo on next dialog open. (`Project/AgentZeroWpf/Services/Browser/OfficialPluginCatalog.cs`)

### 2. Custom — local ZIP

1. Zip the plugin folder so `manifest.json` sits at the root (or inside
   one top-level subdirectory — the installer auto-unwraps).
2. WebDev → + Install Plugin → **From .zip…** → pick the file.
3. Extracted to `%LOCALAPPDATA%\AgentZeroLite\Wasm\plugins\<id>\`.

### 3. Custom — Git URL

Point the installer at a folder inside any public Git repo:

```
https://github.com/<owner>/<repo>/tree/<branch>/<path-to-plugin-folder>
```

Flow:
1. Parses `{owner, repo, branch, path}`.
2. Fetches `manifest.json` via the raw URL first (rejects early if
   missing/invalid).
3. `PluginManifest` validates (same contract as ZIP).
4. Walks the GitHub Trees API to enumerate every file in the folder.
5. Downloads each (limits: 200 files, 25 MB / file).
6. Mounts to `%LOCALAPPDATA%\AgentZeroLite\Wasm\plugins\<id>\`.

**No local `git` CLI required** — the installer talks plain HTTPS.

### Self-install loop (this repo)

After a plugin folder lands in this repo's `main`, you can install it
back into a running AgentZero Lite — it's a real, self-contained
zero-install deployable.

```bash
# Local ZIP path
cd Project/Plugins/voice-note
zip -r ../voice-note.zip .

# Git URL path — paste this in WebDev → + Install Plugin → From Git URL…
https://github.com/psmon/AgentZeroLite/tree/main/Project/Plugins/voice-note
```

---

## Hosting model — what runs where

```
WebDev panel (WebView2)
  ├─ https://zero.local/<sample>/      ← built-in samples shipped in the exe
  │                                      under Project/AgentZeroWpf/Wasm/<name>/
  └─ https://plugin.local/<id>/         ← user-installed plugins
                                         under %LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/<id>/
                          ↑
              SetVirtualHostNameToFolderMapping
                          ↑
              chrome.webview.postMessage  ← the only channel
                          ↓
   WebDevBridge.cs (JSON RPC dispatcher) → IZeroBrowser → host services
```

- **Two virtual hosts**: `zero.local` for the shared bridge + built-in
  samples (read-only, in the exe), `plugin.local` for installed plugins
  (read/write under LocalAppData).
- **No file:// access** — every load is via the virtual hosts.
- **No raw network through the bridge** — your plugin can still `fetch()`
  the open internet through WebView2 like any web page; the bridge itself
  doesn't proxy traffic.

---

## Limitations & FAQ

**Can a plugin talk to actors / terminals?** Not directly. The bridge is
deliberately scoped to the surfaces above (LLM, voice, tokens). If you
need a new capability, the path is to add a method to `IZeroBrowser` /
`WebDevHost`, expose it in `WebDevBridge.DispatchAsync`, and add the JS
wrapper in `zero-bridge.js` — then the contract is available to every
plugin.

**Can two plugins share data?** Each plugin has its own IndexedDB origin
(`https://plugin.local/<id>/` is a distinct origin per id). Cross-plugin
state belongs in the host; ask before designing for it.

**Hot reload while developing?** Yes — DevTools refresh (F5) reloads the
plugin without restarting the host. The bridge re-attaches automatically.

**ZIP / Git URL is rejected — why?** Common causes: `id` regex (lower-case
kebab-case only), `entry` not pointing at an `.html`, manifest at the
wrong nesting level, > 200 files in the folder, > 25 MB per file.
Detailed error in the dialog.

**Do plugin updates auto-trigger?** No. Re-install via the same dialog —
the installer overwrites the existing mount atomically (staging dir →
`Directory.Move`).

**Can plugins ship native code?** No. WebView2 sandbox only — HTML / JS /
CSS / WASM modules / static assets. The host is the native shell.

---

## Where to look in the source

| Concern | File |
|---|---|
| JS bridge wrapper | `Project/AgentZeroWpf/Wasm/common/zero-bridge.js` |
| RPC dispatcher (.NET) | `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs` |
| Host implementation | `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs` |
| Public host contract (WPF-free) | `Project/ZeroCommon/Browser/IZeroBrowser.cs` |
| Manifest validator | `Project/AgentZeroWpf/Services/Browser/PluginManifest.cs` |
| Installer (ZIP + Git URL) | `Project/AgentZeroWpf/Services/Browser/WebDevPluginInstaller.cs` |
| Official catalogue (GitHub Trees) | `Project/AgentZeroWpf/Services/Browser/OfficialPluginCatalog.cs` |
| Sample / plugin discovery | `Project/AgentZeroWpf/Services/Browser/WebDevSampleCatalog.cs` |

(Add new plugin rows to the **Catalogue** above as they land here.)
