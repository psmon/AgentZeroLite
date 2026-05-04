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

    // Voice-note plugin surface (M0007). VAD-gated mic capture; each
    // utterance auto-transcribes via the active STT provider and pushes
    // a `note.transcript` event. Plugins subscribe with `zero.on(...)`.
    note: {
      // sensitivity: 0..100 percent (0 = least sensitive, 100 = most).
      start: (sensitivity) => invoke('note.start', sensitivity == null ? {} : { sensitivity }),
      stop:  () => invoke('note.stop'),
      pause: () => invoke('note.pause'),
      resume: () => invoke('note.resume'),
      setSensitivity: (value) => invoke('note.set-sensitivity', { value }),
      status: () => invoke('note.status'),
      onTranscript:       (handler) => on('note.transcript', handler),
      onUtteranceStart:   (handler) => on('note.utterance-start', handler),
      onUtteranceEnd:     (handler) => on('note.utterance-end', handler),
      onError:            (handler) => on('note.error', handler),
      // RMS amplitude 0..1, ~10 Hz throttled host-side. Payload also
      // carries the current VAD threshold so a meter can draw the
      // "voice-must-be-this-loud" line. { rms, threshold }.
      onAmplitude:        (handler) => on('note.amplitude', handler),
      // Frame-level VAD decision (faster than utterance-start/end).
      // Payload: { speaking: bool }. Use to flash the meter green the
      // instant the user crosses threshold, before the utterance
      // boundary closes (which still requires ~2s of trailing silence).
      onSpeaking:         (handler) => on('note.speaking', handler),
    },

    // LLM-backed text summarization. maxChars defaults to 6000 host-side;
    // longer inputs are halved on a sentence boundary, summarized
    // recursively, then merged. Returns { ok, summary, inputChars, chunks, error }.
    summarize: (text, maxChars) => invoke('summarize', { text, maxChars }),

    // Token-monitor plugin surface (M0009). Read-only — the host runs an
    // internal collector that polls Claude Code / Codex CLI JSONL files
    // every minute and persists rows. Plugins query via these methods
    // and subscribe to `tokens.tick` for live refresh.
    tokens: {
      summary:    (sinceHours) => invoke('tokens.summary',    sinceHours == null ? {} : { sinceHours }),
      byVendor:   (sinceHours) => invoke('tokens.byVendor',   sinceHours == null ? {} : { sinceHours }),
      byAccount:  (sinceHours) => invoke('tokens.byAccount',  sinceHours == null ? {} : { sinceHours }),
      byProject:  (sinceHours, limit) => invoke('tokens.byProject', {
        ...(sinceHours == null ? {} : { sinceHours }),
        ...(limit == null ? {} : { limit }),
      }),
      timeseries: (rangeHours, bucketMinutes) =>
        invoke('tokens.timeseries', { rangeHours, bucketMinutes }),
      sessions:   (sinceHours, limit) =>
        invoke('tokens.sessions', {
          ...(sinceHours == null ? {} : { sinceHours }),
          ...(limit == null ? {} : { limit }),
        }),
      recent:     (limit)      => invoke('tokens.recent', limit == null ? {} : { limit }),
      refresh:    ()           => invoke('tokens.refresh'),
      status:     ()           => invoke('tokens.status'),
      profiles:   ()           => invoke('tokens.profiles'),
      aliases:    ()           => invoke('tokens.aliases'),
      setAlias:   (vendor, accountKey, alias) =>
        invoke('tokens.aliases.set', { vendor, accountKey, alias }),
      removeAlias: (vendor, accountKey) =>
        invoke('tokens.aliases.remove', { vendor, accountKey }),
      reset:      ()           => invoke('tokens.reset'),
      onTick:     (handler)    => on('tokens.tick', handler),

      // Token-remaining widget surface (M0011) — per-account rate-limit
      // telemetry captured by the AgentZero statusLine wrapper. The wrapper
      // tees Claude Code stdin into per-account snapshot files; a 30-second
      // collector dedupes them into the DB (one row per state-change). The
      // widget reads via the methods below.
      remaining: {
        // Lifecycle
        profiles:   ()           => invoke('tokens.remaining.profiles'),
        accounts:   ()           => invoke('tokens.remaining.accounts'),
        install:    (account)    => invoke('tokens.remaining.install', { account }),
        uninstall:  (account, force) => invoke('tokens.remaining.uninstall', { account, force: !!force }),
        // Data
        observedModels: (account) => invoke('tokens.remaining.observedModels', { account }),
        latest:     (account)    => invoke('tokens.remaining.latest', { account }),
        series:     (account, model, hours) =>
          invoke('tokens.remaining.series', { account, model, hours }),
        // Collector control
        status:     ()           => invoke('tokens.remaining.status'),
        refresh:    ()           => invoke('tokens.remaining.refresh'),
        reset:      ()           => invoke('tokens.remaining.reset'),
        onTick:     (handler)    => on('tokens.remaining.tick', handler),

        // Active session panel surface (M0012) — sessions whose
        // statusLine wrapper has ticked within `windowMinutes` (default 5).
        // Distinct from the rate-limit telemetry above; both ride on the
        // same wrapper but have separate tables and tick cadences.
        activeSessions: (windowMinutes) =>
          invoke('tokens.remaining.activeSessions',
            windowMinutes == null ? {} : { windowMinutes }),
        activeSessionsRefresh: () => invoke('tokens.remaining.activeSessions.refresh'),
        activeSessionsStatus:  () => invoke('tokens.remaining.activeSessions.status'),
        onActiveSessionsTick:  (handler) => on('tokens.remaining.activeSessions.tick', handler),
      },
    },
  };
})();
