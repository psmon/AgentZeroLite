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

    // Voice-note plugin surface (M0007). VAD-gated mic capture OR (M0024
    // Phase 3.5) continuous WASAPI loopback capture. Each utterance / chunk
    // auto-transcribes via the active STT provider and pushes a
    // `note.transcript` event. Plugins subscribe with `zero.on(...)`.
    note: {
      // M0024 Phase 3.5 — start accepts either:
      //   • a number (legacy, just sensitivity)
      //   • or an options object: { sensitivity, source, loopbackDeviceId, loopbackChunkSec }
      //     - sensitivity:        0..100 (mic only — 0=least, 100=most)
      //     - source:             "Microphone" | "SystemLoopback"
      //     - loopbackDeviceId:   MMDevice ID for SystemLoopback (omit = Windows default)
      //     - loopbackChunkSec:   5..120 chunk duration for SystemLoopback (default 30)
      // Returns { ok, capturing, sensitivity, threshold, source }.
      start: (arg) => {
        if (arg == null) return invoke('note.start', {});
        if (typeof arg === 'number') return invoke('note.start', { sensitivity: arg });
        return invoke('note.start', arg);
      },
      stop:  () => invoke('note.stop'),
      pause: () => invoke('note.pause'),
      resume: () => invoke('note.resume'),
      setSensitivity: (value) => invoke('note.set-sensitivity', { value }),
      status: () => invoke('note.status'),
      // M0024 Phase 3 — payload extended:
      //   { text: string,
      //     speakerId: number|null,      // 0-based; null when diarization is Off
      //     speakerLabel: string|null,   // "Speaker A" / "Speaker B" / …
      //     isPartial: boolean }         // true = 10s rolling preview; false = committed utterance
      // Pre-Phase-3 plugins ignore the new fields and behave as before.
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

    // Agent Band plugin surface (M0025) — live AST AudioSet classifier +
    // log-banded spectrum. start() boots WASAPI loopback (or mic) + the
    // ONNX session and emits `music.tick` every ~1.5 s with the top-K
    // labels and a 32-bin spectrum snapshot. Plugins subscribe via
    // onTick / onAmplitude.
    music: {
      // opts: { source?, deviceId?, topK?, cadenceMs? }
      //   source:    "Microphone" | "SystemLoopback" (default: SystemLoopback)
      //   deviceId:  MMDevice ID for SystemLoopback, NAudio device # for mic
      //   topK:      1..20 (default 5)
      //   cadenceMs: inference cadence (currently fixed host-side at 1500)
      // Returns { ok, capturing, source, topK, cadenceMs } or
      // { ok: false, error, modelPath? } when the AST model isn't installed.
      start: (opts) => invoke('music.start', opts || {}),
      stop:  () => invoke('music.stop'),
      status: () => invoke('music.status'),
      // Tick payload: { labels: [{name, score, index}], spectrum: float[32],
      //                 frames, bins, inferMs }
      onTick:      (handler) => on('music.tick', handler),
      // Amplitude: { rms } — throttled ~10 Hz.
      onAmplitude: (handler) => on('music.amplitude', handler),
      // Spectrum: { spectrum: float[32] } — throttled ~30 Hz, decoupled
      // from the slow AST inference tick (1.5 s). Plugins drive bar
      // visualisers off this stream for buttery realtime feel.
      onSpectrum:  (handler) => on('music.spectrum', handler),
    },

    // On-device vision surface (M0028) — Florence-2 object detection over the
    // plugin's currently-rendered frame. The HOST captures the WebView2 frame
    // (the plugin's cross-origin YouTube iframe can't be read from JS), crops to
    // the passed device-pixel rect, and runs the model. Used by Agent Band's
    // girl-group mode (person-count → member count, instruments → summon,
    // frame-diff motion → dance sync).
    vision: {
      // → { present: bool, modelDir } — is Florence-2 downloaded?
      status: () => invoke('vision.status'),
      // rect = { x, y, w, h } in DEVICE pixels (CSS px × devicePixelRatio).
      // → { ok, personCount, detections:[{label,xmin,ymin,xmax,ymax}], inferMs }
      //   or { ok:false, error:"model-missing"|"busy"|... }
      analyze: (rect) => invoke('vision.analyze', rect || {}),
      // Cheap frame-diff motion energy for the same rect. → { ok, motion: 0..1 }
      motion:  (rect) => invoke('vision.motion', rect || {}),
      // Drop the motion baseline (call on video change / stop).
      reset:   () => invoke('vision.reset'),
    },

    // LLM-backed text summarization. maxChars defaults to 6000 host-side;
    // longer inputs are halved on a sentence boundary, summarized
    // recursively, then merged. Returns { ok, summary, inputChars, chunks, error }.
    summarize: (text, maxChars) => invoke('summarize', { text, maxChars }),

    // Agent Band (M0026) — YouTube oEmbed metadata is fetched host-side so
    // the plugin isn't blocked by the missing CORS headers on the public
    // oEmbed endpoint. SSRF-safe: only an 11-char video id crosses the bridge.
    youtube: {
      // videoId → { ok, videoId, title, author, thumbnail, error }
      oembed: (videoId) => invoke('youtube.oembed', { videoId }),
    },

    // Stateless one-shot LLM classification (M0026). Does NOT touch the
    // chat.* conversation history — the host opens a throwaway session and
    // clamps the reply to the supplied category whitelist.
    llm: {
      // { title, channel?, categories?: string[] } → { ok, category, raw?, error? }
      classify: (opts) => invoke('llm.classify', opts || {}),
    },

    // Agent Band (M0029) — local MP3 playlist, persisted in the host's
    // SQLite DB (survives reinstall — unlike localStorage). The scan is a
    // BACKGROUND JOB: scan() returns immediately and the host streams
    //   mp3.scan.progress → { phase, done, total, current, added, updated, classified }
    //   mp3.track         → one upserted track DTO (playable immediately;
    //                       new tracks fire again once the LLM category lands)
    //   mp3.scan.done     → { ok, total, added, updated, classified, error? }
    // Playback: the host maps the scan root to https://mp3.local/, so play a
    // track with  audio.src = 'https://mp3.local/' + encodeURI(relativePath).
    mp3: {
      // → { folder, folderExists, scanning, progress?, count }
      status:      () => invoke('mp3.status'),
      // Native folder dialog → { ok, folder } | { ok:false, error:'cancelled' }
      pickFolder:  () => invoke('mp3.pickFolder'),
      // Persist + virtual-host-map the scan root → { ok, folder }
      setFolder:   (folder) => invoke('mp3.setFolder', { folder }),
      // Kick the background scan job → { ok, started } | { ok:false, error:'busy'|'folder-missing' }
      scan:        (categories) => invoke('mp3.scan', { categories: categories || [] }),
      cancelScan:  () => invoke('mp3.scan.cancel'),
      // → { ok, folder, tracks: [{ id, relativePath, title, artist, album,
      //     category, categoryBy, instruments, durationSeconds, available, … }] }
      list:        () => invoke('mp3.list'),
      remove:      (id) => invoke('mp3.remove', { id }),
      markPlayed:  (id) => invoke('mp3.markPlayed', { id }),
      // M0029 확장 — merge instrument keys heard live into the track's
      // persisted set → { ok, id, instruments: string[] }
      setInstruments: (id, instruments) => invoke('mp3.setInstruments', { id, instruments: instruments || [] }),
      // 후속#5 — Florence-2 on the APIC cover → vocal gender hint.
      // { ok, id, gender: 'male'|'female'|'group'|'', by: 'vision'|'cached'|'no-cover', persons? }
      // Persists only while VocalGender is empty; a later LLM verdict wins.
      coverGender: (id) => invoke('mp3.coverGender', { id }),
      // M0030 — mood keys heard live (happy/sad/exciting/…) → persisted set.
      setMoods: (id, moods) => invoke('mp3.setMoods', { id, moods: moods || [] }),
      // M0030 — 느낌 카드 (LLM-curated saved filters; auto-recommend mode).
      // cards() → { ok, cards: [{ id, title, description, filtersJson, createdAtUtc }] }
      // cardCreate() → { ok, card } | { ok:false, error:'llm-not-ready'|'parse-failed'|... }
      cards:      () => invoke('mp3.cards'),
      cardCreate: () => invoke('mp3.cardCreate'),
      cardRemove: (id) => invoke('mp3.cardRemove', { id }),
      onScanProgress: (handler) => on('mp3.scan.progress', handler),
      // M0030 후속#1 — batched upserts: { tracks: [dto, …] } (~2s cadence
      // during a scan). Replaces the per-file mp3.track event, which flooded
      // the bridge and lagged the UI on fast tag scans.
      onTracks:       (handler) => on('mp3.tracks', handler),
      onScanDone:     (handler) => on('mp3.scan.done', handler),
    },

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
