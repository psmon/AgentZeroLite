# Project/Plugins/

External WebDev plugins shipped alongside the AgentZero Lite repo but
**not compiled into the app**. The `AgentZeroWpf.csproj` only includes
its own folder tree; nothing in `Project/Plugins/` is touched by the
build.

A plugin here is a self-contained folder with this minimum shape:

```
Project/Plugins/<plugin-id>/
├── manifest.json     # required — id, name, entry, optional version/icon/description
├── index.html        # the entry the WebView2 loads
└── ...               # whatever JS/CSS/assets the plugin needs
```

`manifest.json` contract (single source of truth — strict validator
in `Project/AgentZeroWpf/Services/Browser/PluginManifest.cs`):

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

Rules:
- `id` — lower-case kebab-case, 1–63 chars, starts with letter or digit
- `entry` — relative `.html` file inside the plugin folder, no `..`
- everything else optional

## Installing a plugin

End users install plugins from the running AgentZero Lite app via the
**WebDev** menu (globe icon on the activity bar) → bottom-left
**+ Install Plugin** entry. Two channels:

### 1. Local ZIP

1. Zip the plugin folder so `manifest.json` sits at the root (or
   inside one top-level subdirectory — the installer auto-unwraps).
2. WebDev → + Install Plugin → **From .zip…** → pick the file.
3. The installer extracts to
   `%LOCALAPPDATA%\AgentZeroLite\Wasm\plugins\<id>\` and refreshes
   the sample list.

### 2. Git URL (since v0.4.x)

Point the installer at a folder inside a public Git repo, e.g.:

```
https://github.com/psmon/AgentZeroLite/tree/main/Project/Plugins/voice-note
```

WebDev → + Install Plugin → **From Git URL…** → paste the URL.

Flow:
1. Installer parses the URL into `{owner, repo, branch, path}` and
   asks the GitHub raw API for `manifest.json` first.
2. `PluginManifest` validates the same way the ZIP path does.
3. The remaining files declared in the plugin folder (recursively
   listed via the GitHub Trees API) are downloaded.
4. Mount + refresh — same target as the ZIP path:
   `%LOCALAPPDATA%\AgentZeroLite\Wasm\plugins\<id>\`.

No local `git` CLI required — the installer talks plain HTTP.

## Catalogue

| Plugin | Status | Description |
|---|---|---|
| `voice-note/` | planned (M0008) | STT-driven voice journal with VAD-gated capture, sensitivity slider, pause/resume, LLM summary, 3-tier storage (raw timeline / summary / meta) |

(Add new rows here as plugins land in this folder.)

## Why this folder is outside the build

Plugins are end-user installables — they run inside WebView2 in the
shipped app, not inside the .NET project. Keeping them under the
repo (instead of a separate one) keeps reference plugins next to the
runtime that hosts them, while the
`AgentZeroWpf.csproj` deliberately ignores everything here so a new
plugin can never break a release build.

The host bridge (`window.zero.*`) is the contract a plugin codes
against. Its surface lives at:
- `Project/AgentZeroWpf/Wasm/common/zero-bridge.js` — JS wrapper
- `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs` — .NET dispatcher
- `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs` — actual implementations
