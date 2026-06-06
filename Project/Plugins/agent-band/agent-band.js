// agent-band.js — M0025 Agent Band plugin (v0.2.0).
//
// v0.2 changelog (operator feedback):
//   • host now emits a 30 Hz `music.spectrum` event independent of the slow
//     1.5 s AST tick — bars feel real-time
//   • lower SCORE_PRESENT + hysteresis (SCORE_KEEP) so AudioSet's typically
//     low sigmoid scores still spawn performers, and once a performer is on
//     stage it stays unless silence dominates
//   • parent-category fallbacks ("Plucked string instrument" / "Brass" / …)
//     because the AST top-K often emits parent classes higher than the
//     specific sub-class
//   • new stage layout: vocals reserved in canvas center, instruments split
//     into L/R wings sorted by ORDER_RANK, sprite width capped so a single
//     performer doesn't fill the screen
//   • asymmetric attack/release lerp on the bars (fast snap up, smooth decay)

(function () {
  'use strict';

  // ── Tunables ─────────────────────────────────────────────────────────
  const SPRITE_BASE     = 'assets/sprites/';
  const STAGE_BASE      = 'assets/stages/';
  const PLAY_FPS        = 9;
  const IDLE_FPS        = 4;
  const PERFORMER_FRAMES = 4;

  // Score gating — tuned against AST AudioSet sigmoid distributions which
  // typically hover 0.05–0.25 even for clean hits (multilabel + 527 classes).
  // PRESENT = first-time spawn threshold. KEEP = stays-on-stage threshold
  // (hysteresis — once a performer is up, it takes less to keep them).
  // ACTIVE = play-animation threshold; below that they idle.
  const SCORE_ACTIVE    = 0.12;
  const SCORE_PRESENT   = 0.05;
  const SCORE_KEEP      = 0.025;
  const PERSIST_TICKS   = 8;   // ~12 s of unseen labels before fade out
  const FADE_MS         = 700;
  const MAX_PERFORMERS  = 8;

  // Layout — sprite width is capped so 1 performer doesn't blow up to fill
  // the whole canvas; the unused space stays as empty stage on the sides.
  const MAX_SPRITE_W    = 140;
  const MIN_SPRITE_W    = 70;
  const MIN_GAP         = 14;
  const STAGE_BASE_Y    = 0.94;  // ground line as fraction of canvas height
  const SPRITE_TARGET_H = 0.55;  // sprite height as fraction of canvas height

  // Spectrum visual config.
  const SPEC_GRADIENT_FROM = [0x00, 0xe5, 0xff];
  const SPEC_GRADIENT_TO   = [0xff, 0x2d, 0x95];
  const SPEC_ATTACK  = 0.40;   // bars going UP — fast snap
  const SPEC_RELEASE = 0.10;   // bars going DOWN — smooth decay

  // ── AudioSet label → performer mapping ───────────────────────────────
  //
  // Tier 1: specific instrument labels (highest priority). The match-first
  //   order matters — `\bcello\b` before `\bviolin\b` because both can co-occur.
  // Tier 2: parent-category labels — these dominate the top-K when the
  //   model can't decide between specific sub-classes. Map to a sensible
  //   default sprite for the family.
  // Vocals fan out across vocal-1..4 by per-tick round-robin so a "Singing"
  //   + "Choir" co-occurrence shows two distinct singers instead of one.
  const VOCAL_ROTATION = ['vocal-1', 'vocal-2', 'vocal-3', 'vocal-4'];
  let vocalCursor = 0;

  function labelToPerformer(label) {
    const s = label.toLowerCase();

    // ── Tier 1: specific instruments ──
    if (/\bcello\b/.test(s))                                  return 'cello';
    if (/\bviolin\b|\bfiddle\b/.test(s))                      return 'violin';
    if (/\bharp\b/.test(s) && !/harpsichord/.test(s))         return 'harp';
    if (/\bguitar\b/.test(s))                                 return 'guitar';
    if (/\bflute\b/.test(s))                                  return 'flute';
    if (/\bclarinet\b/.test(s))                               return 'clarinet';
    if (/french horn|\bhorn\b/.test(s))                       return 'horn';
    if (/\btrumpet\b/.test(s))                                return 'trumpet';
    if (/\btrombone\b/.test(s))                               return 'trombone';
    if (/\bpiano\b/.test(s))                                  return 'piano';
    if (/\bdrum\b|cymbal|tom-tom|hi-hat|tabla/.test(s))       return 'drum';

    // ── Vocals (fan out) ──
    if (/sing(ing)?|choir|vocal|chant|yodel|rapping|hum/.test(s)) {
      const id = VOCAL_ROTATION[vocalCursor % VOCAL_ROTATION.length];
      vocalCursor++;
      return id;
    }

    // ── Tier 2: parent-category fallbacks ──
    // AST's top-K often features these higher than the specific sub-class.
    if (/bowed string|orchestra|symphony|chamber music/.test(s)) return 'violin';
    if (/plucked string/.test(s))                                 return 'guitar';
    if (/woodwind|wind instrument/.test(s))                       return 'flute';
    if (/\bbrass\b/.test(s))                                      return 'trumpet';
    if (/keyboard \(musical\)/.test(s))                           return 'piano';
    if (/percussion/.test(s))                                     return 'drum';

    return null;
  }

  // ── Sprite frame cache + chroma-key ──────────────────────────────────
  //
  // Source PNGs ship with a solid bright-green background (classic chroma
  // key, ~rgb(40, 220, 50)). We key it out at load time onto an off-screen
  // canvas — that canvas replaces the HTMLImageElement in the cache, so
  // the render loop just `drawImage`'s the keyed result with zero per-frame
  // cost. Two-tier threshold:
  //   • hard key — pure background → alpha = 0
  //   • soft key — anti-aliased edge → alpha attenuated + green despill
  //     (subtract the excess green so the kept silhouette doesn't have a
  //     green halo)
  // The character's dark-green clothing is preserved because the test
  // requires both G dominance AND high overall green value — dark greens
  // sit well below the threshold.
  const spriteCache = new Map(); // key="id|state" -> { frames: [HTMLCanvasElement|null] }

  function chromaKey(img) {
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const px = data.data;

    for (let i = 0; i < px.length; i += 4) {
      const r = px[i], g = px[i + 1], b = px[i + 2];
      const gMinusR = g - r;
      const gMinusB = g - b;

      // Hard key — pure bright green background
      if (g > 170 && gMinusR > 55 && gMinusB > 55) {
        px[i + 3] = 0;
        continue;
      }
      // Soft key — anti-aliased edge: still mostly green but dimmer
      if (g > 110 && gMinusR > 28 && gMinusB > 28) {
        const greenness = Math.min(gMinusR, gMinusB); // 28..55 range
        const t = Math.min(1, (greenness - 28) / 27);  // 0..1
        px[i + 3] = px[i + 3] * (1 - t * 0.85);
        // Despill — pull the excess green back so the silhouette edge
        // doesn't read as a green halo. Replace excess G with the avg
        // of R and B (neutralises the cast).
        const spill = Math.max(0, g - Math.max(r, b));
        if (spill > 0) px[i + 1] = g - spill * t * 0.7;
      }
    }
    ctx.putImageData(data, 0, 0);
    return canvas;
  }

  function ensureSpriteSet(id, state) {
    const key = id + '|' + state;
    let entry = spriteCache.get(key);
    if (entry) return entry;
    entry = { frames: new Array(PERFORMER_FRAMES).fill(null) };
    for (let i = 0; i < PERFORMER_FRAMES; i++) {
      const img = new Image();
      const frameIdx = i;
      img.onload = () => {
        try { entry.frames[frameIdx] = chromaKey(img); }
        catch (ex) {
          console.warn('[agent-band] chroma key failed for', img.src, ex);
          // Fall back to the raw image so something at least renders
          entry.frames[frameIdx] = img;
        }
      };
      img.onerror = () => console.warn('[agent-band] sprite missing:', img.src);
      img.src = `${SPRITE_BASE}${id}-${state}-f${i}.png`;
    }
    spriteCache.set(key, entry);
    return entry;
  }

  // ── Performer registry ───────────────────────────────────────────────
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
    vocalCursor = 0; // reset rotation so same labels yield same vocal sprites

    // Collapse multi-label hits onto a single sprite id with the strongest score
    // ("Guitar" + "Acoustic guitar" → one guitar slot with max score).
    const collapsed = new Map();
    for (const l of labels) {
      const id = labelToPerformer(l.name);
      if (!id) continue;
      const prev = collapsed.get(id);
      if (prev === undefined || l.score > prev) collapsed.set(id, l.score);
    }

    // Hysteresis: PRESENT to spawn, KEEP to retain. Once a performer is on
    // stage, a quieter version of the same label still counts as "seen this
    // tick" — that's why a singer doesn't flicker on/off between ticks.
    const ordered = [...collapsed.entries()].sort((a, b) => b[1] - a[1]);

    for (const [id, score] of ordered) {
      const onStage = performers.has(id);
      const required = onStage ? SCORE_KEEP : SCORE_PRESENT;
      if (score < required) continue;

      if (!performers.has(id) && performers.size >= MAX_PERFORMERS) {
        // Evict a fading performer to make room; never bump an active one.
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
      ensureSpriteSet(id, p.state);
    }

    // Unseen-for-N-ticks → fade-out → evict.
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

  // ── Stage layout — vocals center, instruments wings ──────────────────
  //
  // ORDER_RANK ascending = stage position from L→R. Vocals stay center
  // regardless — we split the instruments by half over ORDER_RANK, put
  // the lower-rank half (strings/plucked) on the left, higher-rank half
  // (brass/keys/percussion) on the right, with vocals filling the middle.
  const ORDER_RANK = {
    'violin': 10, 'viola': 11, 'cello': 12, 'contrabass': 13,
    'guitar': 20, 'harp': 21,
    'flute': 30, 'clarinet': 31, 'oboe': 32,
    'horn': 40, 'trumpet': 41, 'trombone': 42, 'tuba': 43,
    'piano': 50, 'drum': 60,
  };

  function computeLayout(allPerformers, w, h) {
    const layout = new Map();
    if (allPerformers.length === 0) return layout;

    const vocals = allPerformers.filter(p => p.id.startsWith('vocal-'));
    const insts  = allPerformers
      .filter(p => !p.id.startsWith('vocal-'))
      .sort((a, b) => (ORDER_RANK[a.id] ?? 99) - (ORDER_RANK[b.id] ?? 99));

    // Instruments split into L/R wings around the vocal cluster.
    const half = Math.ceil(insts.length / 2);
    // Reverse left wing so the inner element (closest to vocals) is the
    // highest-ranked of the left group — gives a nice arc from violin
    // (outer-left) → piano-ish (inner-left) → vocals → brass (inner-right)
    // → drums (outer-right).
    const leftWing  = insts.slice(0, half).reverse();
    const rightWing = insts.slice(half);

    const ordered = [...leftWing, ...vocals, ...rightWing];
    const n = ordered.length;

    // Compute slot dimensions. Total natural width = n × MAX_W + (n-1) × MIN_GAP.
    // If it doesn't fit the canvas, scale down uniformly but not below MIN_W.
    const naturalW = n * MAX_SPRITE_W + (n - 1) * MIN_GAP;
    const scale = naturalW <= w ? 1 : (w - (n - 1) * MIN_GAP) / (n * MAX_SPRITE_W);
    const spriteW = Math.max(MIN_SPRITE_W, MAX_SPRITE_W * scale);
    const gap = Math.max(8, MIN_GAP * Math.max(scale, 0.5));
    const totalW = n * spriteW + (n - 1) * gap;
    const startX = (w - totalW) / 2;

    const baseY = h * STAGE_BASE_Y;
    const slotH = h * SPRITE_TARGET_H;

    ordered.forEach((p, i) => {
      layout.set(p.id, {
        x: startX + i * (spriteW + gap),
        baseY,
        slotW: spriteW,
        slotH,
      });
    });
    return layout;
  }

  // ── Canvas refs ──────────────────────────────────────────────────────
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

  function drawStageBg(w, h) {
    if (!stageReady || !stageImg) {
      stageCtx.fillStyle = '#0b0d12';
      stageCtx.fillRect(0, 0, w, h);
      return;
    }
    const iw = stageImg.naturalWidth;
    const ih = stageImg.naturalHeight;
    const scale = Math.max(w / iw, h / ih);
    const dw = iw * scale;
    const dh = ih * scale;
    const dx = (w - dw) / 2;
    const dy = h - dh; // bottom-aligned: top crops, performer floor stays
    stageCtx.drawImage(stageImg, dx, dy, dw, dh);
  }

  function drawPerformer(p, x, baseY, slotW, slotH, now) {
    const set = ensureSpriteSet(p.id, p.state);
    const frameIdx = Math.floor(now / 1000 * (p.state === 'play' ? PLAY_FPS : IDLE_FPS)) % PERFORMER_FRAMES;
    const tex = set.frames[frameIdx];
    if (!tex) return; // still loading / keying
    // Canvas exposes width/height; Image exposes naturalWidth/naturalHeight.
    // Both work as drawImage sources, just probe the right size property.
    const srcW = tex.naturalWidth || tex.width;
    const srcH = tex.naturalHeight || tex.height;
    if (!srcW || !srcH) return;

    const aspect = srcH / srcW;
    let drawW = slotW;
    let drawH = drawW * aspect;
    if (drawH > slotH) { drawH = slotH; drawW = drawH / aspect; }

    const dx = x + (slotW - drawW) / 2;
    const dy = baseY - drawH;

    let alpha = 1;
    if (p.fading) {
      alpha = Math.max(0, 1 - (now - p.fadeAt) / FADE_MS);
    } else {
      const since = now - p.addedAt;
      if (since < 280) alpha = Math.max(0.1, since / 280);
    }

    const bob = p.state === 'play' ? Math.sin(now / 120 + x) * 3 : 0;

    stageCtx.save();
    stageCtx.globalAlpha = alpha;
    if (p.state === 'play') {
      stageCtx.shadowColor = `rgba(0, 229, 255, ${0.32 * alpha})`;
      stageCtx.shadowBlur = 22;
    }
    stageCtx.drawImage(tex, dx, dy + bob, drawW, drawH);
    stageCtx.restore();
  }

  // ── Spectrum with asymmetric attack/release lerp ─────────────────────
  let lastSpectrum = null;       // most recent host snapshot
  let smoothed = null;            // per-frame smoothed values

  function setSpectrum(bars) {
    if (!bars || bars.length === 0) return;
    lastSpectrum = bars;
    if (!smoothed || smoothed.length !== bars.length) {
      smoothed = new Float32Array(bars.length);
    }
  }

  function tickSmoothSpectrum() {
    if (!lastSpectrum || !smoothed) return;
    for (let i = 0; i < lastSpectrum.length; i++) {
      const cur = smoothed[i] || 0;
      const tgt = lastSpectrum[i];
      const k = tgt > cur ? SPEC_ATTACK : SPEC_RELEASE;
      smoothed[i] = cur + (tgt - cur) * k;
    }
  }

  function drawSpectrum() {
    if (!smoothed) return;
    const { w, h } = fitCanvasToParent(specCanvas);
    const dpr = window.devicePixelRatio || 1;
    specCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    specCtx.clearRect(0, 0, w, h);

    const n = smoothed.length;
    const gap = 2;
    const barW = Math.max(2, (w - (n - 1) * gap) / n);

    for (let i = 0; i < n; i++) {
      const t = i / (n - 1);
      const r = Math.round(SPEC_GRADIENT_FROM[0] + (SPEC_GRADIENT_TO[0] - SPEC_GRADIENT_FROM[0]) * t);
      const g = Math.round(SPEC_GRADIENT_FROM[1] + (SPEC_GRADIENT_TO[1] - SPEC_GRADIENT_FROM[1]) * t);
      const b = Math.round(SPEC_GRADIENT_FROM[2] + (SPEC_GRADIENT_TO[2] - SPEC_GRADIENT_FROM[2]) * t);
      specCtx.fillStyle = `rgb(${r}, ${g}, ${b})`;
      const v = Math.max(0.02, Math.min(1, smoothed[i]));
      const bh = v * (h - 4);
      specCtx.fillRect(i * (barW + gap), h - bh, barW, bh);
    }
  }

  // ── Render loop ──────────────────────────────────────────────────────
  function renderLoop() {
    const { w, h } = fitCanvasToParent(stageCanvas);
    const dpr = window.devicePixelRatio || 1;
    stageCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    stageCtx.clearRect(0, 0, w, h);
    drawStageBg(w, h);

    const ps = [...performers.values()];
    const layout = computeLayout(ps, w, h);
    const now = performance.now();
    for (const p of ps) {
      const pos = layout.get(p.id);
      if (!pos) continue;
      drawPerformer(p, pos.x, pos.baseY, pos.slotW, pos.slotH, now);
    }

    tickSmoothSpectrum();
    drawSpectrum();
    requestAnimationFrame(renderLoop);
  }

  // ── Label strip ──────────────────────────────────────────────────────
  const labelStrip = document.getElementById('label-strip');
  function renderLabelStrip(labels) {
    if (!labels || labels.length === 0) {
      labelStrip.innerHTML = '<span class="empty">대기 중 …</span>';
      return;
    }
    labelStrip.innerHTML = labels.slice(0, 8).map(l => {
      const active = l.score >= SCORE_ACTIVE ? ' active' : '';
      const pct = (l.score * 100).toFixed(0);
      return `<span class="chip${active}">${escapeHtml(l.name)} <span class="pct">${pct}%</span></span>`;
    }).join('');
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
    try { await window.zero.music.stop(); }
    catch (ex) { console.warn('[agent-band] stop failed', ex); }
    setStatus('idle');
    els.start.disabled = false;
    els.stop.disabled = true;
    performers.clear();
    renderLabelStrip([]);
    lastSpectrum = null;
    if (smoothed) smoothed.fill(0);
  }

  function bindEvents() {
    els.start.addEventListener('click', onStart);
    els.stop.addEventListener('click', onStop);
    els.stagePick.addEventListener('change', () => pickStage(els.stagePick.value));

    if (window.zero && window.zero.music) {
      // Slow tick (1.5 s) — performer registry + label strip + diag.
      window.zero.music.onTick(tick => {
        upsertPerformersFromLabels(tick.labels || []);
        renderLabelStrip(tick.labels || []);
        els.diagTick.textContent = `tick ${tickCounter}`;
        els.diagInfer.textContent = `${tick.inferMs} ms`;
        els.diagMel.textContent = `${tick.frames} × ${tick.bins}`;
        // Use the tick's own spectrum as a seed; subsequent 30 Hz events
        // override it. Without this seed, very early in the session the
        // bars sit flat until the first 30 Hz event arrives.
        setSpectrum(tick.spectrum || []);
      });
      // Fast spectrum stream (30 Hz) — used for the bar visualizer.
      window.zero.music.onSpectrum(evt => {
        setSpectrum(evt.spectrum || []);
      });
    }
  }

  // ── Boot ─────────────────────────────────────────────────────────────
  pickStage(els.stagePick.value);
  bindEvents();
  requestAnimationFrame(renderLoop);

  window.addEventListener('beforeunload', () => {
    try { window.zero?.music?.stop(); } catch (_) { /* host gone */ }
  });
})();
