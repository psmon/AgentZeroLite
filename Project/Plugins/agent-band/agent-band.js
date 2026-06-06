// agent-band.js — M0025 Agent Band plugin.
//
// Listens to `window.zero.music.onTick` (AudioSet labels + 32-bin spectrum
// from the AST classifier running natively) and turns it into a live stage
// where performer sprites fade in / play / fade out in response to the
// instruments + vocals the model hears.
//
// No external libs — vanilla Canvas 2D. The sprite class loads individual
// PNG frames (loaded lazily on first need) and cycles them at a per-state
// frame rate. Background is one stage PNG scaled to fit, with the top
// portion allowed to crop so performer space at the bottom stays generous.

(function () {
  'use strict';

  // ── Tunables ─────────────────────────────────────────────────────────
  const SPRITE_BASE     = 'assets/sprites/';
  const STAGE_BASE      = 'assets/stages/';
  const PLAY_FPS        = 9;
  const IDLE_FPS        = 4;
  const PERFORMER_FRAMES = 4;

  // Score thresholds: above ACTIVE → performer is "playing", above PRESENT →
  // "idle on stage", below → fade out after PERSIST_TICKS misses.
  const SCORE_ACTIVE    = 0.30;
  const SCORE_PRESENT   = 0.15;
  const PERSIST_TICKS   = 4;   // 4 ticks ≈ 6 s of silence before exit
  const FADE_MS         = 600;
  const MAX_PERFORMERS  = 6;   // crowd cap so the stage stays readable

  // Spectrum visual config.
  const SPEC_GRADIENT_FROM = [0x00, 0xe5, 0xff];
  const SPEC_GRADIENT_TO   = [0xff, 0x2d, 0x95];

  // ── AudioSet label → performer mapping ───────────────────────────────
  //
  // AudioSet labels are noisy and many overlap (Guitar / Acoustic guitar /
  // Electric guitar all imply a single guitar performer). We collapse them
  // to canonical sprite IDs via regex; the first match wins. Vocals are
  // intentionally fanned out across the 4 vocal sprites by rotating index
  // so multiple co-singing labels don't collapse onto one performer.
  //
  // Sprites without a stable AudioSet label (viola / oboe / contrabass /
  // tuba) are still ship-bundled so future model upgrades — or manual
  // "summon X" hooks — can pull them in. They simply never spawn from
  // AudioSet ticks.
  const VOCAL_ROTATION = ['vocal-1', 'vocal-2', 'vocal-3', 'vocal-4'];
  let vocalCursor = 0;

  function labelToPerformer(label) {
    const s = label.toLowerCase();
    // Strings
    if (/\bcello\b/.test(s))                 return 'cello';
    if (/\bviolin|fiddle\b/.test(s))         return 'violin';
    // Plucked
    if (/\bharp\b/.test(s) && !/harpsichord/.test(s))
                                             return 'harp';
    if (/\bguitar\b/.test(s))                return 'guitar';
    // Woodwinds
    if (/\bflute\b/.test(s))                 return 'flute';
    if (/\bclarinet\b/.test(s))              return 'clarinet';
    // Brass
    if (/french horn|\bhorn\b/.test(s))      return 'horn';
    if (/\btrumpet\b/.test(s))               return 'trumpet';
    if (/\btrombone\b/.test(s))              return 'trombone';
    // Keys
    if (/\bpiano\b/.test(s))                 return 'piano';
    // Percussion (drum-kit, drum-machine, snare-drum, bass-drum all → drum)
    if (/\bdrum\b/.test(s))                  return 'drum';
    // Vocals — fan out across the 4 sprites so co-occurring vocal labels
    // (Singing / Choir / Vocal music) feel like separate singers instead
    // of one performer with conflicting scores.
    if (/sing(ing)?|choir|vocal|chant|yodel|rapping/.test(s)) {
      const id = VOCAL_ROTATION[vocalCursor % VOCAL_ROTATION.length];
      vocalCursor++;
      return id;
    }
    return null;
  }

  // ── Sprite frame cache ───────────────────────────────────────────────
  //
  // Each performer × state has 4 frames at SPRITE_BASE/<id>-<state>-f<n>.png.
  // Loaded lazily on first need; later draws skip any frame still pending.
  const spriteCache = new Map(); // key="id|state" -> {frames:[HTMLImageElement], ready:boolean}

  function ensureSpriteSet(id, state) {
    const key = id + '|' + state;
    let entry = spriteCache.get(key);
    if (entry) return entry;
    entry = { frames: new Array(PERFORMER_FRAMES), readyCount: 0 };
    for (let i = 0; i < PERFORMER_FRAMES; i++) {
      const img = new Image();
      // eslint-disable-next-line no-loop-func
      img.onload = () => { entry.readyCount++; };
      img.onerror = () => {
        console.warn('[agent-band] sprite missing:', img.src);
        entry.readyCount++;
      };
      img.src = `${SPRITE_BASE}${id}-${state}-f${i}.png`;
      entry.frames[i] = img;
    }
    spriteCache.set(key, entry);
    return entry;
  }

  // ── Performer registry ───────────────────────────────────────────────
  // One entry per visible performer. Created on first tick, mutated by
  // subsequent ticks, fade-removed when no longer seen.
  /** @typedef {{
   *    id: string,
   *    score: number,
   *    state: 'play' | 'idle',
   *    addedAt: number,
   *    lastSeenTick: number,
   *    fading: boolean,
   *    fadeAt: number,
   *  }} Performer
   */
  /** @type {Map<string, Performer>} */
  const performers = new Map();
  let tickCounter = 0;

  function upsertPerformersFromLabels(labels) {
    tickCounter++;
    const now = performance.now();
    vocalCursor = 0; // reset rotation per-tick so the same labels stay sticky

    // First, group by performer id so multiple AudioSet entries (e.g.
    // "Guitar" + "Acoustic guitar") collapse into one stage slot using
    // the strongest score.
    const collapsed = new Map(); // id -> bestScore
    for (const l of labels) {
      const id = labelToPerformer(l.name);
      if (!id) continue;
      const prev = collapsed.get(id);
      if (prev === undefined || l.score > prev) collapsed.set(id, l.score);
    }

    // Stage cap — keep only the top MAX_PERFORMERS by score from this
    // tick PLUS anything currently on stage that's still active so we
    // don't churn slots between two equally-scored performers.
    const sortedNew = [...collapsed.entries()]
      .sort((a, b) => b[1] - a[1])
      .filter(([, s]) => s >= SCORE_PRESENT);

    for (const [id, score] of sortedNew) {
      if (!performers.has(id) && performers.size >= MAX_PERFORMERS) {
        // No room — but allow eviction of a fading one before bouncing this.
        const fadingId = [...performers.entries()].find(([, p]) => p.fading)?.[0];
        if (fadingId) performers.delete(fadingId);
        else continue;
      }
      let p = performers.get(id);
      if (!p) {
        p = {
          id,
          score,
          state: score >= SCORE_ACTIVE ? 'play' : 'idle',
          addedAt: now,
          lastSeenTick: tickCounter,
          fading: false,
          fadeAt: 0,
        };
        performers.set(id, p);
      } else {
        p.score = score;
        p.state = score >= SCORE_ACTIVE ? 'play' : 'idle';
        p.lastSeenTick = tickCounter;
        p.fading = false;
        p.fadeAt = 0;
      }
      // Kick off frame load if not already cached.
      ensureSpriteSet(id, p.state);
    }

    // Mark performers we didn't see for fade-out, evict after fade.
    for (const [id, p] of performers) {
      if (p.lastSeenTick === tickCounter) continue;
      if (!p.fading && tickCounter - p.lastSeenTick >= PERSIST_TICKS) {
        p.fading = true;
        p.fadeAt = now;
        p.state = 'idle';
      }
      if (p.fading && now - p.fadeAt > FADE_MS) {
        performers.delete(id);
      }
    }
  }

  // ── Scene render ─────────────────────────────────────────────────────
  const stageCanvas = document.getElementById('stage');
  const stageCtx    = stageCanvas.getContext('2d');
  const specCanvas  = document.getElementById('spectrum');
  const specCtx     = specCanvas.getContext('2d');

  /** @type {HTMLImageElement|null} */
  let stageImg = null;
  let stageReady = false;

  function pickStage(name) {
    const img = new Image();
    img.onload = () => { stageImg = img; stageReady = true; };
    img.onerror = () => { console.warn('[agent-band] stage missing:', img.src); stageReady = false; };
    img.src = `${STAGE_BASE}${name}.png`;
  }

  function fitCanvasToParent(canvas) {
    const dpr = Math.max(1, Math.floor(window.devicePixelRatio || 1));
    const r = canvas.parentElement.getBoundingClientRect();
    const w = Math.max(1, Math.floor(r.width));
    const h = Math.max(1, Math.floor(r.height));
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width  = w * dpr;
      canvas.height = h * dpr;
    }
    return { w, h, dpr };
  }

  // Order performers across the stage from left → right; vocals upfront
  // (vocal-1..4 sorted), then strings, brass, woodwinds, keys, perc — so
  // an audience would see a roughly orchestral layout. Slot positions are
  // recomputed every frame because the registry can change tick-to-tick.
  const ORDER_RANK = {
    'vocal-1': 0, 'vocal-2': 1, 'vocal-3': 2, 'vocal-4': 3,
    'violin': 10, 'viola': 11, 'cello': 12, 'contrabass': 13,
    'guitar': 20, 'harp': 21,
    'flute': 30, 'clarinet': 31, 'oboe': 32,
    'horn': 40, 'trumpet': 41, 'trombone': 42, 'tuba': 43,
    'piano': 50, 'drum': 60,
  };

  function sortedPerformers() {
    return [...performers.values()].sort(
      (a, b) => (ORDER_RANK[a.id] ?? 99) - (ORDER_RANK[b.id] ?? 99));
  }

  function drawStageBg(w, h) {
    if (!stageReady || !stageImg) {
      stageCtx.fillStyle = '#0b0d12';
      stageCtx.fillRect(0, 0, w, h);
      return;
    }
    // Cover-fit, biased to bottom: scale so the image fills width and
    // we drop the top crop. Performers sit on the bottom 30% area.
    const iw = stageImg.naturalWidth;
    const ih = stageImg.naturalHeight;
    const scale = Math.max(w / iw, h / ih);
    const dw = iw * scale;
    const dh = ih * scale;
    const dx = (w - dw) / 2;
    const dy = h - dh; // bottom-aligned: top portion crops off, per mission
    stageCtx.drawImage(stageImg, dx, dy, dw, dh);
  }

  function drawPerformer(p, slotX, baseY, slotW, slotH, now) {
    const set = ensureSpriteSet(p.id, p.state);
    const frameIdx = Math.floor(now / 1000 * (p.state === 'play' ? PLAY_FPS : IDLE_FPS)) % PERFORMER_FRAMES;
    const img = set.frames[frameIdx];
    if (!img || !img.complete || img.naturalWidth === 0) return;

    // Sprite aspect ratio — assume vertical character ~2:3 (h:w).
    const aspect = img.naturalHeight / img.naturalWidth;
    let drawW = slotW;
    let drawH = drawW * aspect;
    if (drawH > slotH) { drawH = slotH; drawW = drawH / aspect; }

    const x = slotX + (slotW - drawW) / 2;
    const y = baseY - drawH;

    // Fade-in (ramp 200 ms from add), fade-out (ramp FADE_MS).
    let alpha = 1;
    if (p.fading) {
      const t = (now - p.fadeAt) / FADE_MS;
      alpha = Math.max(0, 1 - t);
    } else {
      const since = now - p.addedAt;
      if (since < 240) alpha = Math.max(0.1, since / 240);
    }

    // Subtle bob when playing — the spec model already drives sprite-frame
    // motion so this is just a tiny anchor wobble to convey "alive".
    const bob = p.state === 'play'
      ? Math.sin(now / 120 + slotX) * 3
      : 0;

    stageCtx.save();
    stageCtx.globalAlpha = alpha;
    // Soft glow for active performers
    if (p.state === 'play') {
      stageCtx.shadowColor = `rgba(0, 229, 255, ${0.35 * alpha})`;
      stageCtx.shadowBlur = 20;
    }
    stageCtx.drawImage(img, x, y + bob, drawW, drawH);
    stageCtx.restore();
  }

  function drawSpectrum(bars) {
    const { w, h } = fitCanvasToParent(specCanvas);
    specCtx.setTransform(window.devicePixelRatio || 1, 0, 0, window.devicePixelRatio || 1, 0, 0);
    specCtx.clearRect(0, 0, w, h);
    if (!bars || bars.length === 0) return;

    const n = bars.length;
    const gap = 2;
    const barW = Math.max(2, (w - (n - 1) * gap) / n);

    for (let i = 0; i < n; i++) {
      const t = i / (n - 1);
      const r = Math.round(SPEC_GRADIENT_FROM[0] + (SPEC_GRADIENT_TO[0] - SPEC_GRADIENT_FROM[0]) * t);
      const g = Math.round(SPEC_GRADIENT_FROM[1] + (SPEC_GRADIENT_TO[1] - SPEC_GRADIENT_FROM[1]) * t);
      const b = Math.round(SPEC_GRADIENT_FROM[2] + (SPEC_GRADIENT_TO[2] - SPEC_GRADIENT_FROM[2]) * t);
      specCtx.fillStyle = `rgb(${r}, ${g}, ${b})`;
      const v = Math.max(0.02, Math.min(1, bars[i]));
      const bh = v * (h - 4);
      specCtx.fillRect(i * (barW + gap), h - bh, barW, bh);
    }
  }

  let lastSpectrum = null;
  function setSpectrum(bars) {
    // Latch the last spectrum so the RAF loop can keep painting between
    // ticks (host emits ~1.5 s cadence; UI runs at 60 Hz).
    lastSpectrum = bars;
  }

  // ── Render loop ──────────────────────────────────────────────────────
  function renderLoop() {
    const { w, h } = fitCanvasToParent(stageCanvas);
    stageCtx.setTransform(window.devicePixelRatio || 1, 0, 0, window.devicePixelRatio || 1, 0, 0);
    stageCtx.clearRect(0, 0, w, h);
    drawStageBg(w, h);

    const ps = sortedPerformers();
    const now = performance.now();
    if (ps.length > 0) {
      const baseY = h * 0.94;        // ground line — sprites stand on this
      const slotH = h * 0.62;        // tallest sprite occupies 62% of stage height
      const slotW = w / ps.length;
      ps.forEach((p, i) => drawPerformer(p, i * slotW, baseY, slotW, slotH, now));
    }

    if (lastSpectrum) drawSpectrum(lastSpectrum);
    requestAnimationFrame(renderLoop);
  }

  // ── Header label strip ───────────────────────────────────────────────
  const labelStrip = document.getElementById('label-strip');
  function renderLabelStrip(labels) {
    if (!labels || labels.length === 0) {
      labelStrip.innerHTML = '<span class="empty">대기 중 …</span>';
      return;
    }
    const html = labels.slice(0, 8).map(l => {
      const active = l.score >= SCORE_ACTIVE ? ' active' : '';
      const pct = (l.score * 100).toFixed(0);
      return `<span class="chip${active}">${escapeHtml(l.name)} <span class="pct">${pct}%</span></span>`;
    }).join('');
    labelStrip.innerHTML = html;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => (
      { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
  }

  // ── Bridge wiring ────────────────────────────────────────────────────
  const els = {
    start: document.getElementById('startBtn'),
    stop: document.getElementById('stopBtn'),
    status: document.getElementById('bandStatus'),
    stagePick: document.getElementById('stagePicker'),
    source: document.getElementById('sourcePicker'),
    topk: document.getElementById('topkInput'),
    diagSource: document.getElementById('diag-source'),
    diagTick: document.getElementById('diag-tick'),
    diagInfer: document.getElementById('diag-infer'),
    diagMel: document.getElementById('diag-mel'),
    hint: document.getElementById('band-hint'),
  };

  function setStatus(text, cls) {
    els.status.textContent = text;
    els.status.className = 'status' + (cls ? ' ' + cls : '');
  }

  function ensureZero() {
    if (!window.zero || !window.zero.music) {
      setStatus('window.zero.music unavailable — open inside AgentZero', 'err');
      return false;
    }
    return true;
  }

  async function onStart() {
    if (!ensureZero()) return;
    const source = els.source.value;
    const topK = Math.max(1, Math.min(20, parseInt(els.topk.value, 10) || 5));
    setStatus('starting …');
    els.start.disabled = true;
    try {
      const r = await window.zero.music.start({ source, topK });
      if (!r || !r.ok) {
        const err = r?.error || 'unknown';
        if (err === 'model-missing') {
          setStatus('AST 모델 미설치 — Settings → Music → Download', 'err');
          els.hint.textContent = `모델 파일이 없습니다: ${r.modelPath}`;
        } else {
          setStatus('start failed — ' + err, 'err');
        }
        els.start.disabled = false;
        return;
      }
      setStatus(`LIVE · ${r.source}`, 'live');
      els.diagSource.textContent = r.source;
      els.stop.disabled = false;
    } catch (ex) {
      setStatus('start failed — ' + ex.message, 'err');
      els.start.disabled = false;
    }
  }

  async function onStop() {
    if (!ensureZero()) return;
    setStatus('stopping …');
    try {
      await window.zero.music.stop();
    } catch (ex) {
      console.warn('[agent-band] stop failed', ex);
    }
    setStatus('idle');
    els.start.disabled = false;
    els.stop.disabled = true;
    performers.clear();
    renderLabelStrip([]);
    lastSpectrum = null;
  }

  function bindEvents() {
    els.start.addEventListener('click', onStart);
    els.stop.addEventListener('click', onStop);
    els.stagePick.addEventListener('change', () => pickStage(els.stagePick.value));

    if (window.zero && window.zero.music) {
      window.zero.music.onTick(tick => {
        upsertPerformersFromLabels(tick.labels || []);
        setSpectrum(tick.spectrum || []);
        renderLabelStrip(tick.labels || []);
        els.diagTick.textContent = `tick ${tickCounter}`;
        els.diagInfer.textContent = `${tick.inferMs} ms`;
        els.diagMel.textContent = `${tick.frames} × ${tick.bins}`;
      });
    }
  }

  // ── Boot ─────────────────────────────────────────────────────────────
  pickStage(els.stagePick.value);
  bindEvents();
  requestAnimationFrame(renderLoop);

  // Stop capture cleanly if the page unloads (panel closed / detach
  // window closed). The host idempotently handles repeat stop calls.
  window.addEventListener('beforeunload', () => {
    try { window.zero?.music?.stop(); } catch (_) { /* host gone */ }
  });
})();
