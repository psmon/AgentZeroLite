// zero-bridge.js — JS side of the AgentZero IZeroBrowser RPC.
//
// Web apps under Wasm/ load this once and call window.zero.invoke('op', args)
// to reach the native .NET IZeroBrowser host running inside AgentZeroLite.
// All traffic flows over chrome.webview.postMessage — no HTTP, no network.
//
// Wire format (host = AgentZeroWpf/Services/Browser/WebDevBridge.cs):
//   request  → { id, op, args? }
//   response → { id, ok, result?, error? }

(function () {
  if (window.zero) return;

  const wv = window.chrome && window.chrome.webview;
  if (!wv) {
    console.warn('[zero] chrome.webview not present — running outside AgentZero?');
    return;
  }

  const pending = new Map();
  let nextId = 1;

  wv.addEventListener('message', (e) => {
    const msg = e.data;
    if (!msg || typeof msg.id !== 'number') return;
    const slot = pending.get(msg.id);
    if (!slot) return;
    pending.delete(msg.id);
    if (msg.ok) slot.resolve(msg.result);
    else slot.reject(new Error(msg.error || 'unknown error'));
  });

  function invoke(op, args) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      wv.postMessage({ id, op, args: args || {} });
    });
  }

  window.zero = {
    invoke,
    version: () => invoke('version'),
    voice: {
      providers: () => invoke('voice.providers'),
      speak: (text) => invoke('tts.speak', { text }),
      stop: () => invoke('tts.stop'),
    },
  };
})();
