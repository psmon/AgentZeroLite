// zero-bridge.js — JS side of the AgentZero IZeroBrowser RPC.
//
// Web apps under Wasm/ load this once and call window.zero.invoke('op', args)
// to reach the native .NET IZeroBrowser host running inside AgentZeroLite.
// All traffic flows over chrome.webview.postMessage — no HTTP, no network.
//
// Wire format (host = AgentZeroWpf/Services/Browser/WebDevBridge.cs):
//   request  → { id, op, args? }
//   response → { id, ok, result?, error? }
//   event    → { op: "event", channel, data }   (host-pushed; chat streaming)

(function () {
  if (window.zero) return;

  const wv = window.chrome && window.chrome.webview;
  if (!wv) {
    console.warn('[zero] chrome.webview not present — running outside AgentZero?');
    return;
  }

  const pending = new Map();
  let nextId = 1;

  // channel → Set<handler(data)>
  const channels = new Map();

  wv.addEventListener('message', (e) => {
    const msg = e.data;
    if (!msg) return;

    // Event envelope: host-pushed, no id correlation.
    if (msg.op === 'event' && typeof msg.channel === 'string') {
      const subs = channels.get(msg.channel);
      if (!subs) return;
      for (const h of subs) {
        try { h(msg.data); } catch (err) { console.error('[zero] handler error', msg.channel, err); }
      }
      return;
    }

    // Response envelope: id-correlated.
    if (typeof msg.id !== 'number') return;
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

  function on(channel, handler) {
    let subs = channels.get(channel);
    if (!subs) { subs = new Set(); channels.set(channel, subs); }
    subs.add(handler);
    return () => subs.delete(handler);
  }

  // Stream a chat reply token-by-token. Resolves when the host posts `chat.done`.
  function chatStream(text, onToken) {
    const streamId = 's' + Math.random().toString(36).slice(2) + Date.now().toString(36);
    return new Promise((resolve, reject) => {
      const offTok = on('chat.token', (d) => {
        if (d && d.streamId === streamId && typeof d.token === 'string') onToken(d.token);
      });
      const offDone = on('chat.done', (d) => {
        if (!d || d.streamId !== streamId) return;
        offTok(); offDone();
        if (d.ok) resolve();
        else reject(new Error(d.error || 'stream failed'));
      });
      invoke('chat.stream', { text, streamId }).catch((err) => {
        offTok(); offDone();
        reject(err);
      });
    });
  }

  window.zero = {
    invoke,
    on,
    version: () => invoke('version'),
    voice: {
      providers: () => invoke('voice.providers'),
      speak: (text) => invoke('tts.speak', { text }),
      stop: () => invoke('tts.stop'),
    },
    chat: {
      status: () => invoke('chat.status'),
      send: (text) => invoke('chat.send', { text }),
      stream: chatStream,
      reset: () => invoke('chat.reset'),
    },
  };
})();
