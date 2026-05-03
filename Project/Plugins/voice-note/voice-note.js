/**
 * Voice Note plugin (M0008)
 *
 * Calls into AgentZero via window.zero.note.* (VAD-gated capture +
 * per-utterance STT) and window.zero.summarize (length-chunked LLM
 * summary). All notes live in IndexedDB so the plugin survives
 * navigation away and back without losing state.
 */
(function () {
  if (!window.zero) {
    document.body.innerHTML =
      '<div style="padding:40px; color:#FFB454; font-family:Consolas;">window.zero bridge missing — open this from the WebDev menu inside AgentZero Lite.</div>';
    return;
  }

  // ─── IndexedDB wrapper ─────────────────────────────────────────────
  const DB_NAME = 'voice-note-db';
  const DB_VERSION = 1;
  const STORE = 'notes';

  function openDb() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(STORE)) {
          const s = db.createObjectStore(STORE, { keyPath: 'id' });
          s.createIndex('byUpdatedAt', 'updatedAt');
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  async function dbAll() {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readonly');
      const req = tx.objectStore(STORE).getAll();
      req.onsuccess = () => resolve(req.result || []);
      req.onerror = () => reject(req.error);
    });
  }

  async function dbPut(note) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(note);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  async function dbDelete(id) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).delete(id);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  // ─── Helpers ───────────────────────────────────────────────────────

  function uuid() {
    if (crypto.randomUUID) return crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
      const r = Math.random() * 16 | 0;
      const v = c === 'x' ? r : (r & 0x3 | 0x8);
      return v.toString(16);
    });
  }

  function nowIso() { return new Date().toISOString(); }

  function fmtClock(iso) {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '—';
    return d.toLocaleString();
  }

  function fmtElapsed(startMs, line) {
    const d = new Date(line.ts).getTime() - startMs;
    if (!isFinite(d) || d < 0) return '00:00';
    const t = Math.floor(d / 1000);
    return `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`;
  }

  // Debounce — coalesce rapid mutations into one IDB write.
  function debounce(fn, ms) {
    let t = null;
    return (...args) => {
      clearTimeout(t);
      t = setTimeout(() => fn(...args), ms);
    };
  }

  // ─── State ─────────────────────────────────────────────────────────

  const state = {
    notes: /** @type {any[]} */ ([]),
    activeId: null,
    capturing: false,
    paused: false,
    sensitivity: 50,
    captureStartedMs: 0,
    activeTab: 'raw',
    busy: false,
    transcribing: false,
  };

  // Currently-subscribed bridge handlers (returned by zero.on(...)).
  let unsubTranscript = null;
  let unsubUttStart = null;
  let unsubUttEnd = null;
  let unsubError = null;

  // ─── DOM refs ──────────────────────────────────────────────────────
  const $ = (id) => document.getElementById(id);

  const refs = {
    list: $('vn-list'),
    count: $('vn-count'),
    btnNew: $('vn-new'),

    empty: $('vn-empty'),
    noteSec: $('vn-note'),
    title: $('vn-title'),
    meta: $('vn-meta'),

    btnRec: $('vn-rec'),
    btnRecLabel: $('vn-rec-label'),
    btnPause: $('vn-pause'),
    sense: $('vn-sense-range'),
    senseVal: $('vn-sense-val'),
    btnSummarize: $('vn-summarize'),
    btnDelete: $('vn-delete'),

    tabRaw: $('vn-tab-raw'),
    tabSummary: $('vn-tab-summary'),
    tabMeta: $('vn-tab-meta'),
    bodyRaw: $('vn-tab-raw-body'),
    bodySummary: $('vn-tab-summary-body'),
    bodyMeta: $('vn-tab-meta-body'),
    raw: $('vn-raw'),
    summary: $('vn-summary'),
    metaJson: $('vn-meta-json'),
    status: $('vn-status'),
  };

  // ─── Rendering ─────────────────────────────────────────────────────

  function renderList() {
    refs.count.textContent = String(state.notes.length);
    if (!state.notes.length) {
      refs.list.innerHTML = '<div class="vn-list-empty">No notes yet. Press <strong>+ New Note</strong> to start.</div>';
      return;
    }
    // newest updated first
    const sorted = [...state.notes].sort((a, b) => (b.updatedAt || '').localeCompare(a.updatedAt || ''));
    refs.list.replaceChildren(...sorted.map(n => itemEl(n)));
  }

  function itemEl(n) {
    const div = document.createElement('div');
    div.className = 'vn-item' + (n.id === state.activeId ? ' active' : '');
    div.tabIndex = 0;
    div.onclick = () => selectNote(n.id);
    div.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectNote(n.id); } };

    const title = document.createElement('div');
    title.className = 'vn-item-title';
    title.textContent = n.title || 'Untitled note';

    const meta = document.createElement('div');
    meta.className = 'vn-item-meta';
    const lines = (n.raw && n.raw.length) || 0;
    meta.textContent = `${(n.updatedAt || '').slice(0, 10)} · ${lines} ${lines === 1 ? 'line' : 'lines'}`;
    if (n.id === state.activeId && state.capturing) {
      const tag = document.createElement('span');
      tag.className = 'vn-item-tag-rec';
      tag.textContent = 'REC';
      meta.appendChild(tag);
    }
    div.appendChild(title);
    div.appendChild(meta);
    return div;
  }

  function renderActive() {
    const n = activeNote();
    if (!n) {
      refs.empty.hidden = false;
      refs.noteSec.hidden = true;
      return;
    }
    refs.empty.hidden = true;
    refs.noteSec.hidden = false;

    refs.title.value = n.title || '';
    refs.meta.textContent = `created ${fmtClock(n.createdAt)} · updated ${fmtClock(n.updatedAt)}`;
    refs.btnSummarize.disabled = !(n.raw && n.raw.length);

    renderRaw();
    renderSummary();
    renderMeta();
    renderTabs();
    renderRecButton();
  }

  function renderRaw() {
    const n = activeNote();
    if (!n) return;
    if (!n.raw || !n.raw.length) {
      refs.raw.innerHTML = '<div class="vn-raw-empty">Press <strong>Start</strong> and speak. Each utterance lands here as a timestamped line.</div>';
      return;
    }
    const startMs = state.captureStartedMs || new Date(n.raw[0].ts).getTime();
    refs.raw.replaceChildren(...n.raw.map(line => {
      const row = document.createElement('div');
      row.className = 'vn-line';
      const ts = document.createElement('div');
      ts.className = 'vn-line-ts';
      ts.textContent = fmtElapsed(startMs, line);
      const text = document.createElement('div');
      text.className = 'vn-line-text';
      text.textContent = line.text;
      row.appendChild(ts); row.appendChild(text);
      return row;
    }));
    refs.raw.scrollTop = refs.raw.scrollHeight;
  }

  function renderSummary() {
    const n = activeNote();
    if (!n) return;
    refs.summary.textContent = n.summary
      ? n.summary
      : 'No summary yet. Press Summarize after you\'ve captured some text.';
  }

  function renderMeta() {
    const n = activeNote();
    if (!n) return;
    refs.metaJson.textContent = JSON.stringify(n.meta || {}, null, 2);
  }

  function renderTabs() {
    const map = {
      raw: [refs.tabRaw, refs.bodyRaw],
      summary: [refs.tabSummary, refs.bodySummary],
      meta: [refs.tabMeta, refs.bodyMeta],
    };
    for (const k of Object.keys(map)) {
      const [tab, body] = map[k];
      tab.classList.toggle('vn-tab-active', state.activeTab === k);
      body.hidden = state.activeTab !== k;
    }
  }

  function renderRecButton() {
    const recording = state.capturing && !!activeNote();
    refs.btnRec.classList.toggle('recording', recording);
    refs.btnRecLabel.textContent = recording ? (state.paused ? 'Resume' : 'Stop') : 'Start';
    refs.btnPause.disabled = !recording;
    refs.btnPause.textContent = state.paused ? 'Resume' : 'Pause';
  }

  function setStatus(text, isError) {
    refs.status.textContent = text || '';
    refs.status.classList.toggle('error', !!isError);
  }

  // ─── Note ops ──────────────────────────────────────────────────────

  function activeNote() {
    return state.notes.find(n => n.id === state.activeId) || null;
  }

  function newNote() {
    const n = {
      id: uuid(),
      title: 'New note ' + new Date().toLocaleString(),
      createdAt: nowIso(),
      updatedAt: nowIso(),
      raw: [],
      summary: null,
      meta: {},
    };
    state.notes.push(n);
    state.activeId = n.id;
    persist(n);
    renderList();
    renderActive();
  }

  async function selectNote(id) {
    if (state.capturing) {
      // never silently steal capture from another note
      const ok = confirm('Recording is in progress on the current note. Stop it and switch?');
      if (!ok) return;
      await stopCapture();
    }
    state.activeId = id;
    state.captureStartedMs = 0;
    renderList();
    renderActive();
  }

  const persist = debounce(async (n) => {
    n.updatedAt = nowIso();
    try { await dbPut(n); }
    catch (e) { setStatus('IDB save failed: ' + e.message, true); }
  }, 400);

  async function deleteActive() {
    const n = activeNote();
    if (!n) return;
    if (!confirm(`Delete "${n.title || 'Untitled note'}"? This cannot be undone.`)) return;
    if (state.capturing) await stopCapture();
    try { await dbDelete(n.id); } catch (e) { setStatus('IDB delete failed: ' + e.message, true); return; }
    state.notes = state.notes.filter(x => x.id !== n.id);
    state.activeId = null;
    renderList();
    renderActive();
  }

  // ─── Capture ───────────────────────────────────────────────────────

  function bindBridge() {
    unsubTranscript = window.zero.note.onTranscript((d) => {
      if (!d || typeof d.text !== 'string' || !d.text) return;
      const n = activeNote();
      if (!n) return;
      n.raw = n.raw || [];
      n.raw.push({ ts: nowIso(), text: d.text });
      persist(n);
      renderRaw();
      renderList(); // line count update
      refs.btnSummarize.disabled = false;
      state.transcribing = false;
      setStatus(`captured · ${n.raw.length} ${n.raw.length === 1 ? 'line' : 'lines'}`);
    });
    unsubUttStart = window.zero.note.onUtteranceStart(() => {
      state.transcribing = true;
      setStatus('listening…');
    });
    unsubUttEnd = window.zero.note.onUtteranceEnd(() => {
      setStatus('transcribing…');
    });
    unsubError = window.zero.note.onError((d) => {
      setStatus(d?.message || 'capture error', true);
    });
  }

  function unbindBridge() {
    [unsubTranscript, unsubUttStart, unsubUttEnd, unsubError].forEach(fn => { try { fn && fn(); } catch (_) {} });
    unsubTranscript = unsubUttStart = unsubUttEnd = unsubError = null;
  }

  async function startCapture() {
    const n = activeNote();
    if (!n) return;
    bindBridge();
    setStatus('starting capture…');
    try {
      const r = await window.zero.note.start(state.sensitivity);
      if (!r || !r.ok) { setStatus('capture rejected', true); unbindBridge(); return; }
      state.capturing = true;
      state.paused = false;
      state.captureStartedMs = Date.now();
      n.meta = { ...(n.meta || {}), startedAt: nowIso(), sensitivity: state.sensitivity };
      persist(n);
      renderRecButton();
      renderList();
      setStatus('capturing');
    } catch (e) {
      unbindBridge();
      setStatus('start failed: ' + e.message, true);
    }
  }

  async function stopCapture() {
    try { await window.zero.note.stop(); } catch (_) {}
    state.capturing = false;
    state.paused = false;
    unbindBridge();
    const n = activeNote();
    if (n) {
      n.meta = { ...(n.meta || {}), endedAt: nowIso() };
      persist(n);
    }
    renderRecButton();
    renderList();
    setStatus('stopped');
  }

  async function togglePause() {
    if (!state.capturing) return;
    try {
      if (state.paused) { await window.zero.note.resume(); state.paused = false; setStatus('capturing'); }
      else              { await window.zero.note.pause();  state.paused = true;  setStatus('paused'); }
    } catch (e) { setStatus('toggle failed: ' + e.message, true); }
    renderRecButton();
  }

  // ─── Summarize ─────────────────────────────────────────────────────

  async function summarize() {
    const n = activeNote();
    if (!n || !(n.raw && n.raw.length) || state.busy) return;
    state.busy = true;
    refs.btnSummarize.disabled = true;
    setStatus('summarizing…');
    try {
      const text = n.raw.map(l => l.text).join('\n');
      const r = await window.zero.summarize(text, 6000);
      if (r && r.ok) {
        n.summary = r.summary;
        n.meta = { ...(n.meta || {}),
          summarizedAt: nowIso(),
          summarizedInputChars: r.inputChars,
          summarizedChunks: r.chunks,
        };
        persist(n);
        state.activeTab = 'summary';
        renderSummary(); renderMeta(); renderTabs();
        setStatus(`summary ready · ${r.chunks} chunk${r.chunks === 1 ? '' : 's'}`);
      } else {
        setStatus('summary failed: ' + (r?.error || 'unknown'), true);
      }
    } catch (e) {
      setStatus('summary failed: ' + e.message, true);
    } finally {
      state.busy = false;
      refs.btnSummarize.disabled = !(n.raw && n.raw.length);
    }
  }

  // ─── Wire up ───────────────────────────────────────────────────────

  function wire() {
    refs.btnNew.onclick = () => newNote();
    refs.btnDelete.onclick = () => deleteActive();
    refs.btnRec.onclick = () => state.capturing ? stopCapture() : startCapture();
    refs.btnPause.onclick = () => togglePause();
    refs.btnSummarize.onclick = () => summarize();

    refs.title.oninput = () => {
      const n = activeNote();
      if (!n) return;
      n.title = refs.title.value;
      persist(n);
      renderList();
    };

    refs.sense.oninput = async () => {
      state.sensitivity = parseInt(refs.sense.value, 10) || 0;
      refs.senseVal.textContent = String(state.sensitivity);
      if (state.capturing) {
        try { await window.zero.note.setSensitivity(state.sensitivity); }
        catch (e) { setStatus('sensitivity failed: ' + e.message, true); }
      }
    };

    const setTab = (k) => { state.activeTab = k; renderTabs(); };
    refs.tabRaw.onclick = () => setTab('raw');
    refs.tabSummary.onclick = () => setTab('summary');
    refs.tabMeta.onclick = () => setTab('meta');

    // Last-chance cleanup if the user tears down the WebView2 host while
    // capture is running.
    window.addEventListener('beforeunload', () => {
      if (state.capturing) { try { window.zero.note.stop(); } catch (_) {} }
    });
  }

  // ─── Boot ──────────────────────────────────────────────────────────

  (async function boot() {
    wire();
    try {
      state.notes = await dbAll();
    } catch (e) {
      setStatus('IDB open failed: ' + e.message, true);
      state.notes = [];
    }
    if (state.notes.length) {
      // pick most-recently-updated
      state.notes.sort((a, b) => (b.updatedAt || '').localeCompare(a.updatedAt || ''));
      state.activeId = state.notes[0].id;
    }
    renderList();
    renderActive();
    setStatus('ready');
  })();
})();
