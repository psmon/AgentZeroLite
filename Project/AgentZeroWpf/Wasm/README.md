# Wasm/ — Local Web App Sandbox for the WebDev Tab

This folder ships with the exe and is mounted into the in-app browser as
`https://zero.local/` via WebView2's `SetVirtualHostNameToFolderMapping`.
Web apps here run **offline**, with no network — JS calls native .NET methods
on `IZeroBrowser` through the `chrome.webview.postMessage` bridge.

> Folder name is `Wasm/` because future plugins are expected to ship as
> proper WebAssembly modules alongside HTML/JS. Today the contents are plain
> HTML/CSS/JS — that's fine, the structure is what matters.

## Layout

```
Wasm/
├── README.md           ← this file
├── common/
│   └── zero-bridge.js  ← Promise-based RPC wrapper around postMessage
└── voice-test/         ← first sandbox app (TTS demo)
    ├── index.html
    ├── voice-test.css
    └── voice-test.js
```

Each sub-folder is a self-contained app. Drop a new folder, point the WebDev
panel's `EntryPath` at `<name>/index.html`, done.

## JS API (`window.zero`)

Core surface:

```js
await window.zero.version();             // { version: "v0.4.x" }
await window.zero.voice.providers();     // { stt, tts, llmBackend }
await window.zero.voice.speak("hello");  // { ok, provider, bytes, format, error }
await window.zero.voice.stop();          // { stopped: true }

await window.zero.chat.status();         // { available, backend, model, detail }
await window.zero.chat.send("…");        // { ok, reply, turn, error }
await window.zero.chat.stream("…", t => …); // resolves on done; rejects on error
await window.zero.chat.reset();          // { reset: true }
```

Voice-note plugin surface (M0007 — VAD-gated STT + LLM summary):

```js
await window.zero.note.start(50);                  // 0..100 sensitivity
const off = window.zero.note.onTranscript(d => …); // d.text — one utterance
window.zero.note.onUtteranceStart(() => …);        // optional UI cue
window.zero.note.onError(d => console.warn(d.message));
window.zero.note.setSensitivity(70);
await window.zero.note.pause();
await window.zero.note.resume();
await window.zero.note.stop();

await window.zero.summarize(rawTimeline, 6000);    // { ok, summary, inputChars, chunks }
```

All calls return Promises. Errors thrown on the .NET side surface as a
rejected Promise with the host's exception message.

## Adding a new operation

1. Add a method to `IZeroBrowser` in `Project/ZeroCommon/Browser/IZeroBrowser.cs`.
2. Implement it in `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs`.
3. Add the dispatch case in `WebDevBridge.DispatchAsync` (op string + args).
4. Add the JS wrapper in `common/zero-bridge.js`.
5. Use it from any sandbox app.

The contract intentionally lives in `ZeroCommon` (WPF-free) so future WASM
plugins can compile against it without dragging in `PresentationFramework`.
