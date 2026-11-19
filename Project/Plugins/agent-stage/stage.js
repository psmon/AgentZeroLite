/*
 * Agent Stage — AgentZero Lite web plugin.
 *
 * Polls window.zero.invoke('sessions.list') (the host's ACTIVE SESSIONS: every workspace, its
 * running terminals, each one's agent and detected state) and draws it as a live stage:
 *   · a zone per workspace, coloured from its name; the active workspace framed in teal (as in
 *     the sidebar);
 *   · an avatar per terminal — Claude ✳, Codex ◆, AgentOne ❖, Netclaw ✦, Hermes (its desktop icon), a shell ›_ —
 *     whose motion says what it is doing: orbiting sparks, a thinking ring and typing bars while
 *     working, a red pulse when it needs you, a burst when it finishes;
 *   · a Claude session's sub-agents (the Agent / Task tool) as a crew around its avatar — each
 *     launched from its parent along a tether, nested ones further out from theirs, a comet back
 *     to the parent when one hands its report in;
 *   · a Claude tab working in a garden harness (harness-kakashi, "$schema": "kakashi-harness") wears
 *     the garden's mark, and its experts are drawn as experts: a sub-agent launched as one is a
 *     hexagon on a vine-green tether, labelled with its persona (작사가, Security Guard); an expert
 *     the conversation takes on itself (it read harness/agents/<name>.md, or /harness-creator put the
 *     gardener on) is an emblem worn at the avatar's side for that turn;
 *   · click an avatar (or one of its crew) → sessions.focus jumps to that terminal.
 * Outside the app (no bridge) or with ?demo in the URL it runs on simulated sessions.
 */
(() => {
  'use strict';

  const POLL_MS = 1500;
  const DEMO = !window.zero || /[?&]demo\b/.test(location.search);

  const AGENTS = {
    claude:   { color: '#E8885A', glyph: '✳', name: 'Claude' },
    codex:    { color: '#3CCB8F', glyph: '◆', name: 'Codex' },
    agentone: { color: '#B48CFF', glyph: '❖', name: 'AgentOne' },
    netclaw:  { color: '#F2C94C', glyph: '✦', name: 'Netclaw' },
    // Hermes Agent's own desktop icon (img/NOTICE.md), ink on a pearl orb; ⚕ — the mark its TUI prints — until it loads.
    hermes:   { color: '#E4DEF0', glyph: '⚕', name: 'Hermes', img: 'img/hermes.png' },
    shell:    { color: '#8A93A3', glyph: '›_', name: 'Shell' },
  };
  // Agents drawn with a picture: loaded once, drawn only when complete (the glyph stands in until then
  // and if the file is missing).
  for (const ag of Object.values(AGENTS)) {
    if (!ag.img) continue;
    const im = new Image();
    im.src = ag.img;
    ag.image = im;
  }
  const STATES = {
    working:     { color: '#E5C07B', label: 'working' },
    blocked:     { color: '#E06C75', label: 'needs you' },
    done:        { color: '#61AFEF', label: 'done' },
    'done-seen': { color: '#98C379', label: 'done' },
    idle:        { color: '#7F848E', label: 'idle' },
    running:     { color: '#A06CFF', label: 'running' },
  };
  // Sub-agent types: a glyph each, the colour from the type's name so any custom type gets one too.
  const KIND_GLYPH = { explore: '⌕', plan: '▦', 'general-purpose': '✲', 'claude-code-guide': '?', 'statusline-setup': '▭' };
  const KIDS = {
    working: { color: '#E5C07B', label: 'working' },
    done:    { color: '#98C379', label: 'done' },
    stopped: { color: '#7F848E', label: 'stopped' },
  };
  const KEEP_MS = 120000;   // the host keeps a finished sub-agent this long; it fades over it

  // The garden harness (Claude only): its experts are hexagons, its tethers a vine, its mark ❀.
  const GARDEN = { color: '#7FD18B', gold: '#E8C66A', mark: '❀' };
  const GARDENER = 'tamer';
  const VIA = { read: 'read its definition', log: 'wrote its log', command: '/harness-creator', skill: 'harness-creator skill' };
  /** How the garden calls an expert, short: "연기자(노래·연주 퍼포먼스)" → "연기자". */
  function expertLabel(e) {
    const p = (e.persona || '').replace(/\s*[(（].*$/, '').trim();
    return (p || e.name).slice(0, 16);
  }
  function expertGlyph(e) {
    if ((e.name || '').toLowerCase() === GARDENER) return GARDEN.mark;
    const ch = [...expertLabel(e)][0] || '?';
    return /[a-z]/i.test(ch) ? ch.toUpperCase() : ch;
  }
  function expertColor(e) { return `hsl(${hueOf((e.name || '').toLowerCase())},50%,60%)`; }

  const CODE_CHARS = ['{', '}', '<', '/>', ';', '()', '=>', '01', '#', '*', '[]', 'fn', '∑', 'λ'];

  // ── DOM ──────────────────────────────────────────────────────────────────
  const $ = (id) => document.getElementById(id);
  const canvas = $('as-canvas');
  const ctx = canvas.getContext('2d');
  const stageEl = $('as-stage');
  const emptyEl = $('as-empty');
  const tipEl = $('as-tip');
  const allBox = $('as-all');
  const calmBox = $('as-calm');

  const store = {
    get(k, d) { try { const v = localStorage.getItem('agent-stage.' + k); return v === null ? d : v === '1'; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('agent-stage.' + k, v ? '1' : '0'); } catch { /* private window */ } },
  };
  allBox.checked = store.get('all', false);
  calmBox.checked = store.get('calm', false);
  allBox.addEventListener('change', () => { store.set('all', allBox.checked); layout(); });
  calmBox.addEventListener('change', () => store.set('calm', calmBox.checked));
  if (DEMO) $('as-mode').textContent = 'demo';

  // ── model ────────────────────────────────────────────────────────────────
  let data = { supported: true, workspaces: [] };
  const avatars = new Map();   // key → avatar (kept across polls so motion is continuous)
  let zones = [];
  let particles = [];
  let W = 0, H = 0, dpr = 1;
  let hover = null;
  let shake = 0;

  const keyOf = (ws, s) => `${ws.path}|${s.group}:${s.tab}|${s.title}`;

  function hueOf(text) {
    let h = 2166136261;
    for (let i = 0; i < text.length; i++) { h ^= text.charCodeAt(i); h = Math.imul(h, 16777619); }
    return Math.abs(h) % 360;
  }

  function apply(next) {
    data = next || { supported: false, workspaces: [] };
    const seen = new Set();
    for (const ws of data.workspaces || []) {
      for (const s of ws.sessions || []) {
        const key = keyOf(ws, s);
        seen.add(key);
        let a = avatars.get(key);
        if (!a) {
          a = { key, x: NaN, y: NaN, tx: 0, ty: 0, r: 22, born: now(), phase: Math.random() * 6.28,
                sparkT: 0, ringT: 0, state: s.state };
          avatars.set(key, a);
        } else if (a.state !== s.state) {
          transition(a, a.state, s.state);
        }
        Object.assign(a, { ws, session: s, agent: AGENTS[s.agent] ? s.agent : 'shell',
                           state: s.state, title: s.title, focused: !!s.focused, attention: !!s.needsAttention,
                           garden: s.harness || null });
        const shown = capExperts(s.subagents || [], (s.harness && s.harness.onDuty) || []);
        a.moreExperts = shown.hidden;
        syncKids(a, shown.subs);
        syncDuties(a, shown.duties);
      }
    }
    for (const k of [...avatars.keys()]) if (!seen.has(k)) avatars.delete(k);
    updateStats();
    layout();
  }

  // At most MAX_EXPERTS experts per avatar — expert sub-agents and the experts its conversation wears,
  // together. A garden has 10+ experts and a review can raise them all; past the cap the finished ones
  // leave first (longest finished first), then the longest-running working ones. Who was left out is
  // kept for the "+N" chip and its tooltip. Sub-agents that are not experts (Explore, Plan…) are untouched.
  const MAX_EXPERTS = 5;
  function capExperts(subs, duties) {
    const all = [
      ...subs.filter(k => k.expert).map(k => ({ kind: 'sub', v: k, name: expertLabel(k.expert) })),
      ...duties.map(d => ({ kind: 'duty', v: d, name: expertLabel(d) })),
    ];
    if (all.length <= MAX_EXPERTS) return { subs, duties, hidden: [] };
    const rank = (e) => e.v.state === 'working' ? [0, -(e.v.startedAt || 0)] : [1, -(e.v.endedAt || e.v.startedAt || 0)];
    all.sort((p, q) => { const a = rank(p), b = rank(q); return a[0] - b[0] || a[1] - b[1]; });
    const keep = new Set(all.slice(0, MAX_EXPERTS).map(e => e.v));
    return {
      subs: subs.filter(k => !k.expert || keep.has(k)),
      duties: duties.filter(d => keep.has(d)),
      hidden: all.slice(MAX_EXPERTS).map(e => ({ name: e.name, state: e.v.state })),
    };
  }

  // The crew of one avatar, kept by id across polls so a sub-agent keeps its place and motion.
  function syncKids(a, list) {
    const kids = a.kids || (a.kids = new Map());
    const seen = new Set();
    for (const sub of list) {
      seen.add(sub.id);
      let k = kids.get(sub.id);
      if (!k) {
        k = { id: sub.id, x: NaN, y: NaN, tx: 0, ty: 0, born: now(), spin: Math.random() * 6.28, state: sub.state };
        kids.set(sub.id, k);   // no position yet: drawCrew starts it at its parent, so it flies out
      } else if (k.state !== sub.state && isFinite(k.x)) {
        if (sub.state === 'done') {
          burst(k.x, k.y, [KIDS.done.color, '#ffffff'], 16, 2.2);
          k.handback = true;   // drawn next frame: a comet back to whoever started it
        } else if (sub.state === 'stopped') {
          burst(k.x, k.y, [KIDS.stopped.color], 8, 1.4);
        }
      }
      Object.assign(k, { sub, state: sub.state, parentId: sub.parentId || null });
    }
    for (const id of [...kids.keys()]) if (!seen.has(id)) kids.delete(id);
  }

  // Experts the conversation itself works as — kept by name, so a duty keeps its seat and motion.
  function syncDuties(a, list) {
    const duties = a.duties || (a.duties = new Map());
    const seen = new Set();
    for (const d of list) {
      seen.add(d.name);
      let e = duties.get(d.name);
      if (!e) {
        e = { name: d.name, x: NaN, y: NaN, born: now(), spin: Math.random() * 6.28, state: d.state, fresh: true };
        duties.set(d.name, e);   // fresh: drawn first at the avatar, so it is seen being put on
      } else if (e.state !== d.state && isFinite(e.x)) {
        if (d.state === 'done') { burst(e.x, e.y, [GARDEN.color, GARDEN.gold], 14, 2); e.handback = true; }
        else if (d.state === 'working') { e.fresh = true; e.born = now(); }   // back on for a new turn
      }
      Object.assign(e, { duty: d, state: d.state });
    }
    for (const n of [...duties.keys()]) if (!seen.has(n)) duties.delete(n);
  }

  // What tells one crew member from another. A typed sub-agent (Explore, Plan, …) is its type;
  // general-purpose ones are told apart by the task's first word — measured: a harness spawned
  // eight general-purpose reviewers ("lyricist review of …", "genre-scout review of …"), and by
  // type alone they were eight identical dots.
  function kidRole(sub) {
    if (sub.expert) return expertLabel(sub.expert);
    const type = sub.type || 'agent';
    if (type.toLowerCase() !== 'general-purpose') return type;
    const m = /^[\p{L}\p{N}][\p{L}\p{N}_-]*/u.exec((sub.description || '').trim());
    return m ? m[0].slice(0, 18) : type;
  }
  function kidColor(sub) { return sub.expert ? expertColor(sub.expert) : `hsl(${hueOf(kidRole(sub).toLowerCase())},62%,62%)`; }
  function kidGlyph(sub) {
    if (sub.expert) return expertGlyph(sub.expert);
    const known = KIND_GLYPH[(sub.type || '').toLowerCase()];
    return known && sub.type.toLowerCase() !== 'general-purpose' ? known : kidRole(sub).charAt(0).toUpperCase();
  }

  function transition(a, from, to) {
    if (!isFinite(a.x)) return;
    const ag = AGENTS[a.agent] || AGENTS.shell;
    if (to === 'done' || (from === 'working' && to === 'done-seen')) {
      burst(a.x, a.y, [STATES.done.color, ag.color, '#ffffff'], 46, 4.2);
      a.ringT = 0;
    } else if (to === 'blocked') {
      burst(a.x, a.y, [STATES.blocked.color, '#ffb4b4'], 28, 3.2);
      if (!calmBox.checked) shake = 8;
    } else if (to === 'working') {
      burst(a.x, a.y, [ag.color, STATES.working.color], 18, 2.4);
    }
  }

  function updateStats() {
    let working = 0, waiting = 0, done = 0, total = 0, wsWith = 0, crew = 0, experts = 0;
    for (const ws of data.workspaces || []) {
      if ((ws.sessions || []).length) wsWith++;
      for (const s of ws.sessions || []) {
        total++;
        crew += (s.subagents || []).filter(k => k.state === 'working').length;
        experts += (s.subagents || []).filter(k => k.expert && k.state === 'working').length
                 + ((s.harness && s.harness.onDuty) || []).filter(d => d.state === 'working').length;
        if (s.state === 'working') working++;
        else if (s.state === 'blocked') waiting++;
        else if (s.state === 'done') done++;
      }
    }
    $('as-n-working').textContent = working;
    $('as-n-waiting').textContent = waiting;
    $('as-n-done').textContent = done;
    $('as-n-total').textContent = total;
    $('as-n-ws').textContent = wsWith;
    $('as-n-crew').textContent = crew;
    $('as-crew').hidden = crew === 0;
    $('as-n-experts').textContent = experts;
    $('as-garden').hidden = experts === 0;
  }

  // ── layout: a grid of zones, avatars in rows inside each ─────────────────
  function layout() {
    const shown = (data.workspaces || []).filter(ws => allBox.checked || ws.active || (ws.sessions || []).length);
    emptyEl.hidden = true;
    if (!data.supported) return showEmpty('This host does not share its sessions with plugins yet.<br>Agent Stage runs in the cross-platform (Avalonia) edition.');
    if (!shown.length) return showEmpty('No terminal is running.<br>Open one in a workspace — its agent appears here.');

    const pad = 14, gap = 14;
    const n = shown.length;
    const aspect = W / Math.max(1, H);
    let cols = Math.max(1, Math.round(Math.sqrt(n * aspect / 1.3)));
    cols = Math.min(cols, n);
    const rows = Math.ceil(n / cols);
    const zw = (W - pad * 2 - gap * (cols - 1)) / cols;
    const zh = (H - pad * 2 - gap * (rows - 1)) / rows;

    zones = shown.map((ws, i) => {
      const c = i % cols, r = Math.floor(i / cols);
      const z = { ws, x: pad + c * (zw + gap), y: pad + r * (zh + gap), w: zw, h: zh, hue: hueOf(ws.path || ws.name) };
      const list = (ws.sessions || []).map(s => avatars.get(keyOf(ws, s))).filter(Boolean);
      const innerTop = z.y + 40, innerH = z.h - 52;
      // An avatar with a crew needs a wider seat and room above it for the halo; one wearing
      // experts at its sides needs the width too.
      const crewed = list.some(a => (a.kids && a.kids.size) || (a.duties && a.duties.size));
      const per = Math.max(1, Math.floor(z.w / (crewed ? 190 : 120)));
      const lines = Math.max(1, Math.ceil(list.length / per));
      const cell = Math.min(z.w / Math.min(per, Math.max(1, list.length)), innerH / lines);
      const r0 = Math.max(12, Math.min(30, cell * 0.24));
      list.forEach((a, j) => {
        const row = Math.floor(j / per), inRow = Math.min(per, list.length - row * per);
        const cw = z.w / inRow;
        a.tx = z.x + cw * (j % per + 0.5);
        a.ty = innerTop + (innerH / lines) * (row + (crewed ? 0.6 : 0.42));
        a.r = crewed ? Math.min(r0, 24) : r0;
        a.zoneTop = z.y;
        if (!isFinite(a.x)) { a.x = a.tx; a.y = a.ty + 30; }
      });
      z.counts = {
        working: list.filter(a => a.state === 'working').length,
        need: list.filter(a => a.state === 'blocked').length,
      };
      return z;
    });
  }

  function showEmpty(html) { zones = []; emptyEl.innerHTML = html; emptyEl.hidden = false; }

  // ── particles ────────────────────────────────────────────────────────────
  function burst(x, y, colors, n, speed) {
    if (calmBox.checked) n = Math.ceil(n / 3);
    for (let i = 0; i < n; i++) {
      const ang = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random());
      particles.push({ x, y, vx: Math.cos(ang) * v, vy: Math.sin(ang) * v - 1, g: 0.06, life: 0,
                       max: 50 + Math.random() * 40, color: colors[i % colors.length], size: 1.5 + Math.random() * 2.5 });
    }
  }

  function spark(a, color) {
    const ang = -Math.PI / 2 + (Math.random() - 0.5) * 1.4;
    const v = 0.8 + Math.random() * 1.2;
    particles.push({ x: a.x + Math.cos(ang) * a.r, y: a.y + Math.sin(ang) * a.r, vx: Math.cos(ang) * v, vy: Math.sin(ang) * v,
                     g: -0.004, life: 0, max: 60 + Math.random() * 30, color,
                     text: CODE_CHARS[(Math.random() * CODE_CHARS.length) | 0], size: 9 + Math.random() * 4 });
  }

  // ── drawing ──────────────────────────────────────────────────────────────
  const now = () => performance.now() / 1000;
  const stars = Array.from({ length: 90 }, () => ({ x: Math.random(), y: Math.random(), z: 0.2 + Math.random() * 0.8 }));

  function background(t) {
    const g = ctx.createLinearGradient(0, 0, 0, H);
    g.addColorStop(0, '#0d0f14'); g.addColorStop(1, '#0a0c11');
    ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
    for (const s of stars) {
      const x = (s.x * W + t * 6 * s.z) % W, y = s.y * H;
      ctx.globalAlpha = 0.15 + 0.25 * s.z * (0.6 + 0.4 * Math.sin(t * 1.3 + s.x * 40));
      ctx.fillStyle = '#9fb3d1';
      ctx.fillRect(x, y, s.z * 1.6, s.z * 1.6);
    }
    ctx.globalAlpha = 1;
  }

  function roundRect(x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
  }

  function drawZone(z, t) {
    const busy = z.counts.working > 0;
    const hsl = (s, l, a) => `hsla(${z.hue},${s}%,${l}%,${a})`;
    // body
    const g = ctx.createLinearGradient(z.x, z.y, z.x, z.y + z.h);
    g.addColorStop(0, hsl(45, 16, busy ? 0.62 : 0.45)); g.addColorStop(1, hsl(40, 9, 0.35));
    roundRect(z.x, z.y, z.w, z.h, 12); ctx.fillStyle = g; ctx.fill();
    // energy waves while someone works
    if (busy) {
      ctx.save(); roundRect(z.x, z.y, z.w, z.h, 12); ctx.clip();
      for (let i = 0; i < 3; i++) {
        const p = ((t * 0.35 + i / 3) % 1);
        ctx.strokeStyle = hsl(80, 60, 0.18 * (1 - p));
        ctx.lineWidth = 2;
        ctx.beginPath();
        for (let x = 0; x <= z.w; x += 8) {
          const y = z.y + z.h * (1 - p) + Math.sin(x / 38 + t * 3 + i) * 6;
          x === 0 ? ctx.moveTo(z.x + x, y) : ctx.lineTo(z.x + x, y);
        }
        ctx.stroke();
      }
      ctx.restore();
    }
    // frame: teal for the active workspace (the sidebar's mark), the zone hue otherwise
    roundRect(z.x + 0.5, z.y + 0.5, z.w - 1, z.h - 1, 12);
    if (z.ws.active) {
      ctx.strokeStyle = '#4ec9b0'; ctx.lineWidth = 2.2;
      ctx.shadowColor = '#4ec9b0'; ctx.shadowBlur = 14;
    } else {
      ctx.strokeStyle = hsl(55, 45, busy ? 0.75 : 0.4); ctx.lineWidth = 1.2;
    }
    if (busy) { ctx.setLineDash([10, 8]); ctx.lineDashOffset = -t * 30; }
    ctx.stroke(); ctx.setLineDash([]); ctx.shadowBlur = 0;
    // header
    ctx.fillStyle = hsl(70, 70, 1); ctx.beginPath(); ctx.arc(z.x + 16, z.y + 19, 4.5, 0, 6.29); ctx.fill();
    ctx.font = '600 13px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = z.ws.active ? '#4ec9b0' : '#d8dee9';
    ctx.textBaseline = 'middle'; ctx.textAlign = 'left';
    const title = clip(z.ws.name, z.w - 130);
    ctx.fillText(title, z.x + 28, z.y + 19);
    // the garden this workspace's Claude works in, beside its name
    const garden = (z.ws.sessions || []).map(s => s.harness).find(Boolean);
    if (garden) {
      const gx = z.x + 28 + ctx.measureText(title).width + 10, room = z.x + z.w - 120 - gx;
      ctx.font = '600 11px "Segoe UI", system-ui, sans-serif';
      if (room > 40) { ctx.fillStyle = GARDEN.color; ctx.fillText(clip(`${GARDEN.mark} ${garden.name}`, room), gx, z.y + 19); }
    }
    ctx.font = '11px "Segoe UI", system-ui, sans-serif'; ctx.textAlign = 'right';
    const meta = [];
    if (z.counts.working) meta.push(`⚡ ${z.counts.working}`);
    if (z.counts.need) meta.push(`● ${z.counts.need}`);
    meta.push(`${(z.ws.sessions || []).length} tab${(z.ws.sessions || []).length === 1 ? '' : 's'}`);
    ctx.fillStyle = z.counts.need ? STATES.blocked.color : '#8a93a3';
    ctx.fillText(meta.join('  '), z.x + z.w - 12, z.y + 19);
  }

  /** A flat-topped hexagon — the garden expert's shape (a plain sub-agent is a circle). */
  function hexPath(x, y, r) {
    ctx.beginPath();
    for (let i = 0; i < 6; i++) {
      const ang = Math.PI / 3 * i;
      i ? ctx.lineTo(x + Math.cos(ang) * r, y + Math.sin(ang) * r) : ctx.moveTo(x + r, y);
    }
    ctx.closePath();
  }

  function clip(text, max) {
    if (ctx.measureText(text).width <= max) return text;
    while (text.length > 1 && ctx.measureText(text + '…').width > max) text = text.slice(0, -1);
    return text + '…';
  }

  function drawAvatar(a, t, dt) {
    a.x += (a.tx - a.x) * Math.min(1, dt * 6);
    a.y += (a.ty - a.y) * Math.min(1, dt * 6);
    const ag = AGENTS[a.agent] || AGENTS.shell;
    const st = STATES[a.state] || STATES.running;
    const working = a.state === 'working', blocked = a.state === 'blocked';
    const pulse = working ? 1 + 0.07 * Math.sin(t * 6 + a.phase) : blocked ? 1 + 0.05 * Math.sin(t * 10) : 1 + 0.025 * Math.sin(t * 1.6 + a.phase);
    const r = a.r * pulse;
    let x = a.x, y = a.y;
    if (blocked && !calmBox.checked) x += Math.sin(t * 40) * 1.2;

    // glow
    const glowR = r * (working ? 3.0 : blocked ? 2.6 : 2.1);
    const glow = ctx.createRadialGradient(x, y, r * 0.3, x, y, glowR);
    glow.addColorStop(0, hexA(working ? ag.color : st.color, working ? 0.45 : 0.28));
    glow.addColorStop(1, hexA(ag.color, 0));
    ctx.fillStyle = glow; ctx.beginPath(); ctx.arc(x, y, glowR, 0, 6.29); ctx.fill();

    if (a.kids && a.kids.size) drawCrew(a, x, y, r, t, dt);
    if (a.duties && a.duties.size) drawDuties(a, x, y, r, t, dt);

    if (working) {
      // thinking ring: two arcs counter-rotating
      ctx.lineWidth = 2.4; ctx.lineCap = 'round';
      ctx.strokeStyle = hexA(ag.color, 0.9);
      ctx.beginPath(); ctx.arc(x, y, r + 8, t * 3.2 + a.phase, t * 3.2 + a.phase + 1.9); ctx.stroke();
      ctx.strokeStyle = hexA(st.color, 0.75);
      ctx.beginPath(); ctx.arc(x, y, r + 13, -t * 2.1, -t * 2.1 + 1.2); ctx.stroke();
      // orbiting sparks
      for (let i = 0; i < 3; i++) {
        const ang = t * (2.2 + i * 0.6) + i * 2.09 + a.phase, rr = r + 18 + i * 3;
        ctx.fillStyle = i === 1 ? st.color : ag.color;
        ctx.beginPath(); ctx.arc(x + Math.cos(ang) * rr, y + Math.sin(ang) * rr * 0.62, 2.4, 0, 6.29); ctx.fill();
      }
      // code sparks rising
      a.sparkT += dt;
      const every = calmBox.checked ? 0.45 : 0.16;
      while (a.sparkT > every) { a.sparkT -= every; spark(a, Math.random() < 0.5 ? ag.color : st.color); }
      // typing bars
      const bw = 4, gap = 3, n = 6, bx = x - (n * (bw + gap) - gap) / 2, by = y + r + 30;
      for (let i = 0; i < n; i++) {
        const h = 3 + 9 * Math.abs(Math.sin(t * 9 + i * 1.3 + a.phase));
        ctx.fillStyle = hexA(st.color, 0.85);
        ctx.fillRect(bx + i * (bw + gap), by - h, bw, h);
      }
    } else if (blocked) {
      for (let i = 0; i < 2; i++) {
        const p = (t * 1.2 + i * 0.5) % 1;
        ctx.strokeStyle = hexA(st.color, 0.8 * (1 - p)); ctx.lineWidth = 2;
        ctx.beginPath(); ctx.arc(x, y, r + 4 + p * 26, 0, 6.29); ctx.stroke();
      }
    } else if (a.state === 'done') {
      a.ringT += dt;
      const p = (a.ringT * 0.7) % 1;
      ctx.strokeStyle = hexA(st.color, 0.75 * (1 - p)); ctx.lineWidth = 2.2;
      ctx.beginPath(); ctx.arc(x, y, r + 4 + p * 30, 0, 6.29); ctx.stroke();
    }

    // core
    const core = ctx.createRadialGradient(x - r * 0.35, y - r * 0.4, r * 0.1, x, y, r);
    core.addColorStop(0, lighten(ag.color, 0.35)); core.addColorStop(1, darken(ag.color, a.state === 'idle' ? 0.55 : 0.25));
    ctx.fillStyle = core; ctx.beginPath(); ctx.arc(x, y, r, 0, 6.29); ctx.fill();
    ctx.lineWidth = 1.5; ctx.strokeStyle = hexA('#ffffff', a.state === 'idle' ? 0.15 : 0.35); ctx.stroke();
    ctx.fillStyle = '#ffffff'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.globalAlpha = a.state === 'idle' ? 0.6 : 1;
    if (ag.image && ag.image.complete && ag.image.naturalWidth > 0) {
      ctx.save();
      ctx.beginPath(); ctx.arc(x, y, r - 1, 0, 6.29); ctx.clip();
      ctx.drawImage(ag.image, x - r, y - r, r * 2, r * 2);
      ctx.restore();
    } else {
      ctx.fillStyle = ag.image ? '#2B2440' : '#ffffff';   // the glyph on a pale orb needs dark ink
      ctx.font = `700 ${Math.round(r * (ag.glyph.length > 1 ? 0.7 : 0.95))}px "Segoe UI Symbol", "Segoe UI", system-ui, sans-serif`;
      ctx.fillText(ag.glyph, x, y + 1);
    }
    ctx.globalAlpha = 1;

    // badge
    if (blocked || a.state === 'done' || a.state === 'done-seen') {
      const bx = x + r * 0.78, by = y - r * 0.78, br = Math.max(7, r * 0.34);
      ctx.fillStyle = st.color; ctx.beginPath(); ctx.arc(bx, by, br, 0, 6.29); ctx.fill();
      ctx.fillStyle = '#0d0f14'; ctx.font = `800 ${Math.round(br * 1.3)}px "Segoe UI", sans-serif`;
      ctx.fillText(blocked ? '!' : '✓', bx, by + (blocked ? 1 : 0.5));
    }

    // the garden's mark: this Claude works in a garden harness
    if (a.garden) {
      const gx = x - r * 0.78, gy = y + r * 0.78, gr = Math.max(6, r * 0.32);
      hexPath(gx, gy, gr); ctx.fillStyle = '#16301d'; ctx.fill();
      ctx.lineWidth = 1.2; ctx.strokeStyle = GARDEN.color; ctx.stroke();
      ctx.fillStyle = GARDEN.color; ctx.font = `700 ${Math.round(gr * 1.25)}px "Segoe UI Symbol", "Segoe UI", sans-serif`;
      ctx.fillText(GARDEN.mark, gx, gy + 0.5);

      // experts past the cap: a "+N" chip above the mark, listed in the avatar's tooltip
      const more = (a.moreExperts || []).length;
      if (more) {
        const label = `+${more}`, cy = y - r * 0.9;
        ctx.font = '700 9px "Segoe UI", system-ui, sans-serif';
        const w = ctx.measureText(label).width + 8, cx = x - r * 0.95;
        ctx.fillStyle = '#16301d'; ctx.strokeStyle = GARDEN.gold; ctx.lineWidth = 1;
        ctx.beginPath(); ctx.roundRect(cx - w / 2, cy - 6.5, w, 13, 6.5); ctx.fill(); ctx.stroke();
        ctx.fillStyle = GARDEN.gold; ctx.fillText(label, cx, cy + 0.5);
      }
    }

    // focused terminal: dashed ring in the sidebar's blue
    if (a.focused) {
      ctx.setLineDash([5, 5]); ctx.lineDashOffset = -t * 18;
      ctx.strokeStyle = '#3794ff'; ctx.lineWidth = 1.8;
      ctx.beginPath(); ctx.arc(x, y, r + (working ? 24 : 10), 0, 6.29); ctx.stroke();
      ctx.setLineDash([]);
    }

    // label
    const ly = y + r + (working ? 44 : 18);
    ctx.font = '600 12px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = a.focused ? '#7fb8ff' : '#d8dee9';
    ctx.fillText(clip(labelOf(a), 120), x, ly);
    ctx.font = '10px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = st.color;
    ctx.fillText(st.label, x, ly + 14);
    a.hit = { x, y, r: Math.max(r + 10, 26), top: y - r - 14, bottom: ly + 20 };
  }

  // ── the crew: a Claude session's sub-agents ──────────────────────────────
  function seatCrew(a, x, y, r) {
    const kids = a.kids;
    const cr = Math.max(7, Math.min(11, r * 0.42));
    const top = [...kids.values()].filter(k => !k.parentId || !kids.has(k.parentId));
    const n = top.length, ring2 = n > 5;
    const spread = Math.min(Math.PI * 0.9, (n - 1) * (ring2 ? 0.42 : 0.78));
    // Fit the halo under the zone header: when the space above the avatar is short, flatten it and
    // spread it sideways instead of running into the header; with room to spare, open it up a
    // little so a big crew is not packed around the avatar.
    const outer = r + 62 + (ring2 ? cr * 3 : 0);
    const room = y - ((a.zoneTop ?? 0) + 34) - cr - 16;
    const squeeze = Math.max(0.45, Math.min(ring2 ? 1.45 : 1.15, room / (outer * 0.8)));
    const vk = 0.8 * squeeze, hk = squeeze >= 1 ? squeeze : 1 + (1 - squeeze) * 0.7;
    top.forEach((k, i) => {
      const ang = -Math.PI / 2 - spread / 2 + (n > 1 ? spread * i / (n - 1) : 0);
      const R = r + 62 + (ring2 && i % 2 ? cr * 3 : 0);
      Object.assign(k, { ang, R, cr, depth: 0, tx: x + Math.cos(ang) * R * hk, ty: y + Math.sin(ang) * R * vk });
    });
    // Nested ones fan out beyond the sub-agent that started them (a depth or two at most in practice).
    for (let pass = 0; pass < 3; pass++)
      for (const p of kids.values()) {
        if (p.depth !== pass || !isFinite(p.ang)) continue;
        const brood = [...kids.values()].filter(k => k.parentId === p.id);
        brood.forEach((k, i) => {
          const ang = p.ang + (i - (brood.length - 1) / 2) * 0.36;
          const R = p.R + cr * 4.2;
          Object.assign(k, { ang, R, cr: cr * 0.85, depth: pass + 1, tx: x + Math.cos(ang) * R * hk, ty: y + Math.sin(ang) * R * vk });
        });
      }
  }

  function drawCrew(a, x, y, r, t, dt) {
    seatCrew(a, x, y, r);
    const kids = a.kids, wall = Date.now();
    const sourceOf = (k) => {
      const p = k.parentId && kids.get(k.parentId);
      return p && isFinite(p.x) ? { x: p.x, y: p.y } : { x, y };
    };
    // Tethers first, so the nodes sit on top of them.
    for (const k of kids.values()) {
      if (!isFinite(k.ang)) continue;
      const src = sourceOf(k);
      if (!isFinite(k.x)) { k.x = src.x; k.y = src.y; }          // launched from its parent: flies out to its seat
      k.x += (k.tx - k.x) * Math.min(1, dt * 4);
      k.y += (k.ty - k.y) * Math.min(1, dt * 4);
      // an expert hangs on a vine: the garden's green, a little stronger than a plain tether
      const color = k.sub.expert ? GARDEN.color : kidColor(k.sub), fade = kidFade(k, wall);
      const mx = (src.x + k.x) / 2 + (k.y - src.y) * 0.15, my = (src.y + k.y) / 2 - (k.x - src.x) * 0.15;
      ctx.globalAlpha = (k.sub.expert ? 0.55 : 0.35) * fade;
      ctx.strokeStyle = color; ctx.lineWidth = k.sub.expert ? 1.8 : 1.3;
      if (k.state !== 'working') ctx.setLineDash([3, 4]);
      ctx.beginPath(); ctx.moveTo(src.x, src.y); ctx.quadraticCurveTo(mx, my, k.x, k.y); ctx.stroke();
      ctx.setLineDash([]);
      if (k.state === 'working') {
        // work flowing out along the tether
        for (let i = 0; i < 2; i++) {
          const u = (t * 0.9 + i / 2 + k.spin) % 1, v = 1 - u;
          const px = v * v * src.x + 2 * v * u * mx + u * u * k.x, py = v * v * src.y + 2 * v * u * my + u * u * k.y;
          ctx.globalAlpha = 0.9 * fade; ctx.fillStyle = color;
          ctx.beginPath(); ctx.arc(px, py, 1.8, 0, 6.29); ctx.fill();
        }
      }
      if (k.handback) { k.handback = false; comet(k.x, k.y, src.x, src.y, KIDS.done.color); }
    }
    ctx.globalAlpha = 1;

    // Labels sit above each node (the crew is above its parent, so below would run into it):
    // the role always (smaller in a big crew), the whole task too when it is alone (two collide).
    const showRole = kids.size <= 14, small = kids.size > 5, showDesc = kids.size === 1;
    for (const k of kids.values()) {
      if (!isFinite(k.ang)) continue;
      const sub = k.sub, color = kidColor(sub), fade = kidFade(k, wall);
      const working = k.state === 'working';
      const grow = Math.min(1, (now() - k.born) * 3);
      const cr = k.cr * (0.4 + 0.6 * grow) * (working ? 1 + 0.06 * Math.sin(t * 7 + k.spin) : 1);
      ctx.globalAlpha = fade;
      if (working) {
        const g = ctx.createRadialGradient(k.x, k.y, cr * 0.4, k.x, k.y, cr * 2.4);
        g.addColorStop(0, 'rgba(229,192,123,0.35)'); g.addColorStop(1, 'rgba(229,192,123,0)');
        ctx.fillStyle = g; ctx.beginPath(); ctx.arc(k.x, k.y, cr * 2.4, 0, 6.29); ctx.fill();
        ctx.strokeStyle = KIDS.working.color; ctx.lineWidth = 1.6; ctx.lineCap = 'round';
        ctx.beginPath(); ctx.arc(k.x, k.y, cr + 4, t * 4 + k.spin, t * 4 + k.spin + 2.2); ctx.stroke();
      }
      ctx.fillStyle = k.state === 'stopped' ? '#4a4f5a' : color;
      if (sub.expert) {
        // a garden expert: a hexagon one size up, rimmed in gold
        hexPath(k.x, k.y, cr * 1.18); ctx.fill();
        ctx.lineWidth = 1.6; ctx.strokeStyle = hexA(GARDEN.gold, 0.9); ctx.stroke();
      } else {
        ctx.beginPath(); ctx.arc(k.x, k.y, cr, 0, 6.29); ctx.fill();
        ctx.lineWidth = 1; ctx.strokeStyle = 'rgba(255,255,255,0.35)'; ctx.stroke();
      }
      ctx.fillStyle = '#0d0f14'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.font = `700 ${Math.round(cr * 1.1)}px "Segoe UI Symbol", "Segoe UI", system-ui, sans-serif`;
      ctx.fillText(kidGlyph(sub), k.x, k.y + 0.5);
      if (!working) {
        const bx = k.x + cr * 0.75, by = k.y - cr * 0.75, br = Math.max(4, cr * 0.42);
        ctx.fillStyle = (KIDS[k.state] || KIDS.stopped).color;
        ctx.beginPath(); ctx.arc(bx, by, br, 0, 6.29); ctx.fill();
        ctx.fillStyle = '#0d0f14'; ctx.font = `800 ${Math.round(br * 1.5)}px "Segoe UI", sans-serif`;
        ctx.fillText(k.state === 'done' ? '✓' : '×', bx, by + 0.5);
      }
      if (showRole) {
        const above = k.y - cr - (small ? 6 : 8);
        ctx.font = `600 ${small ? 9 : 10}px "Segoe UI", system-ui, sans-serif`;
        ctx.fillStyle = sub.expert ? (working ? '#b9ecc0' : '#8fb596') : working ? '#e8d3a6' : '#9aa3b2';
        ctx.fillText(clip(kidRole(sub), small ? 64 : 110), k.x, above);
        if (showDesc && sub.description) {
          ctx.font = '10px "Segoe UI", system-ui, sans-serif'; ctx.fillStyle = '#8a93a3';
          ctx.fillText(clip(sub.description, 130), k.x, above - 12);
        }
      }
      k.hit = { x: k.x, y: k.y, r: cr + 5 };
    }
    ctx.globalAlpha = 1;

    // the parent's working-crew count, top-left of its avatar
    const busy = [...kids.values()].filter(k => k.state === 'working').length;
    if (busy) {
      const label = `⑂ ${busy}`;
      ctx.font = '700 10px "Segoe UI", system-ui, sans-serif';
      const w = ctx.measureText(label).width + 10, bx = x - r * 0.9, by = y - r * 0.85;
      roundRect(bx - w / 2, by - 8, w, 16, 8); ctx.fillStyle = 'rgba(229,192,123,0.92)'; ctx.fill();
      ctx.fillStyle = '#0d0f14'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText(label, bx, by + 0.5);
    }
  }

  // ── the experts a Claude wears: the garden's experts its own conversation took on ──
  // No tether — it is the same agent in another role — but an emblem at its side, linked by a short
  // gold strap, right then left, then a row lower and higher. Put on → it slides out from the
  // avatar; the turn ends → a comet back in, and it fades like a finished sub-agent.
  function drawDuties(a, x, y, r, t, dt) {
    const wall = Date.now(), list = [...a.duties.values()];
    const er = Math.max(8, Math.min(12, r * 0.45)), R = r + 32, labels = list.length <= 4;
    list.forEach((e, i) => {
      const side = i % 2 ? -1 : 1, row = Math.floor(i / 2);
      const tilt = row === 0 ? 0 : (row % 2 ? 1 : -1) * Math.ceil(row / 2) * 0.62;
      const ang = side > 0 ? tilt : Math.PI - tilt;
      const tx = x + Math.cos(ang) * R, ty = y + Math.sin(ang) * R * 0.9 + Math.sin(t * 1.5 + e.spin) * 2;
      if (e.fresh || !isFinite(e.x)) { e.x = x; e.y = y; e.fresh = false; }
      e.x += (tx - e.x) * Math.min(1, dt * 5);
      e.y += (ty - e.y) * Math.min(1, dt * 5);
      const d = e.duty, working = e.state === 'working';
      const fade = working || !d.endedAt ? 1 : Math.max(0.3, 1 - (wall - d.endedAt) / KEEP_MS);
      const color = d.name === GARDENER ? GARDEN.color : expertColor(d);
      ctx.globalAlpha = fade;

      // the strap: avatar edge → emblem edge
      const dx = e.x - x, dy = e.y - y, len = Math.hypot(dx, dy) || 1;
      ctx.strokeStyle = hexA(GARDEN.gold, working ? 0.7 : 0.35); ctx.lineWidth = 1.4;
      if (!working) ctx.setLineDash([2, 3]);
      ctx.beginPath(); ctx.moveTo(x + dx / len * r, y + dy / len * r); ctx.lineTo(e.x - dx / len * er, e.y - dy / len * er); ctx.stroke();
      ctx.setLineDash([]);

      if (working) {
        const g = ctx.createRadialGradient(e.x, e.y, er * 0.4, e.x, e.y, er * 2.6);
        g.addColorStop(0, hexA(GARDEN.color, 0.35)); g.addColorStop(1, hexA(GARDEN.color, 0));
        ctx.fillStyle = g; ctx.beginPath(); ctx.arc(e.x, e.y, er * 2.6, 0, 6.29); ctx.fill();
        ctx.strokeStyle = GARDEN.gold; ctx.lineWidth = 1.5; ctx.lineCap = 'round';
        ctx.beginPath(); ctx.arc(e.x, e.y, er + 4, -t * 3 + e.spin, -t * 3 + e.spin + 2); ctx.stroke();
      }
      hexPath(e.x, e.y, er); ctx.fillStyle = e.state === 'stopped' ? '#4a4f5a' : color; ctx.fill();
      ctx.lineWidth = 1.6; ctx.strokeStyle = hexA(GARDEN.gold, 0.9); ctx.stroke();
      ctx.fillStyle = '#0d0f14'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.font = `700 ${Math.round(er * 1.05)}px "Segoe UI Symbol", "Segoe UI", system-ui, sans-serif`;
      ctx.fillText(expertGlyph(d), e.x, e.y + 0.5);
      if (labels) {
        ctx.font = '600 9px "Segoe UI", system-ui, sans-serif';
        ctx.fillStyle = working ? '#b9ecc0' : '#8fb596';
        ctx.fillText(clip(expertLabel(d), 70), e.x, e.y + er + 9);
      }
      if (e.handback) { e.handback = false; comet(e.x, e.y, x, y, GARDEN.color); }
      e.hit = { x: e.x, y: e.y, r: er + 5 };
    });
    ctx.globalAlpha = 1;
  }

  /** A finished one fades over the time the host keeps it; a working one is solid. */
  function kidFade(k, wall) {
    if (k.state === 'working' || !k.sub.endedAt) return 1;
    return Math.max(0.3, 1 - (wall - k.sub.endedAt) / KEEP_MS);
  }

  function comet(x0, y0, x1, y1, color) {
    particles.push({ comet: true, x0, y0, x1, y1, x: x0, y: y0, life: 0, max: 40, color, size: 3 });
  }

  function fmtAge(ms) {
    const sec = Math.max(0, Math.round(ms / 1000));
    return sec < 60 ? `${sec}s` : `${Math.floor(sec / 60)}m${String(sec % 60).padStart(2, '0')}s`;
  }

  function drawParticles(dt) {
    const step = dt * 60;
    particles = particles.filter(p => (p.life += step) < p.max);
    for (const p of particles) {
      if (p.comet) {
        // eased flight with a short trail
        const u = p.life / p.max, e = u * u * (3 - 2 * u);
        const px = p.x, py = p.y;
        p.x = p.x0 + (p.x1 - p.x0) * e; p.y = p.y0 + (p.y1 - p.y0) * e;
        ctx.globalAlpha = 0.9; ctx.strokeStyle = p.color; ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(px, py); ctx.lineTo(p.x, p.y); ctx.stroke();
        ctx.fillStyle = p.color; ctx.beginPath(); ctx.arc(p.x, p.y, p.size, 0, 6.29); ctx.fill();
        continue;
      }
      p.vy += p.g * step; p.x += p.vx * step; p.y += p.vy * step;
      const a = 1 - p.life / p.max;
      ctx.globalAlpha = a;
      ctx.fillStyle = p.color;
      if (p.text) {
        ctx.font = `600 ${p.size | 0}px "Cascadia Mono", Consolas, monospace`;
        ctx.textAlign = 'center'; ctx.fillText(p.text, p.x, p.y);
      } else {
        ctx.beginPath(); ctx.arc(p.x, p.y, p.size * (0.5 + a * 0.5), 0, 6.29); ctx.fill();
      }
    }
    ctx.globalAlpha = 1;
    if (particles.length > 900) particles.splice(0, particles.length - 900);
  }

  // ── colour helpers ───────────────────────────────────────────────────────
  function rgb(hex) { const n = parseInt(hex.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
  function hexA(hex, a) { const [r, g, b] = rgb(hex); return `rgba(${r},${g},${b},${a})`; }
  function lighten(hex, k) { const c = rgb(hex).map(v => Math.round(v + (255 - v) * k)); return `rgb(${c})`; }
  function darken(hex, k) { const c = rgb(hex).map(v => Math.round(v * (1 - k))); return `rgb(${c})`; }

  // ── loop ─────────────────────────────────────────────────────────────────
  let last = now(), raf = 0;
  function frame() {
    raf = requestAnimationFrame(frame);
    const t = now(), dt = Math.min(0.05, t - last); last = t;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    background(t);
    ctx.save();
    if (shake > 0) { ctx.translate((Math.random() - 0.5) * shake, (Math.random() - 0.5) * shake); shake *= 0.86; if (shake < 0.3) shake = 0; }
    for (const z of zones) drawZone(z, t);
    for (const z of zones)
      for (const s of z.ws.sessions || []) { const a = avatars.get(keyOf(z.ws, s)); if (a) drawAvatar(a, t, dt); }
    drawParticles(dt);
    ctx.restore();
  }

  function resize() {
    const rect = stageEl.getBoundingClientRect();
    dpr = window.devicePixelRatio || 1;
    W = Math.max(1, rect.width); H = Math.max(1, rect.height);
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    layout();
  }
  new ResizeObserver(resize).observe(stageEl);

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { cancelAnimationFrame(raf); raf = 0; }
    else if (!raf) { last = now(); frame(); }
  });

  // ── interaction ──────────────────────────────────────────────────────────
  function pick(ev) {
    const rect = canvas.getBoundingClientRect();
    const mx = ev.clientX - rect.left, my = ev.clientY - rect.top;
    // a crew member first — it sits over its parent's hit area
    for (const a of avatars.values())
      for (const k of (a.kids ? a.kids.values() : [])) {
        const h = k.hit; if (!h || !isFinite(k.ang)) continue;
        if (Math.hypot(mx - h.x, my - h.y) <= h.r) return { ...a, kid: k, hit: { ...a.hit, x: h.x, y: h.y, top: h.y - h.r - 4 } };
      }
    for (const a of avatars.values())
      for (const e of (a.duties ? a.duties.values() : [])) {
        const h = e.hit; if (!h) continue;
        if (Math.hypot(mx - h.x, my - h.y) <= h.r) return { ...a, duty: e.duty, hit: { ...a.hit, x: h.x, y: h.y, top: h.y - h.r - 4 } };
      }
    for (const a of avatars.values()) {
      const h = a.hit; if (!h) continue;
      if (Math.hypot(mx - h.x, my - h.y) <= h.r || (Math.abs(mx - h.x) < 60 && my >= h.top && my <= h.bottom)) return a;
    }
    return null;
  }
  // The tab's title, led by the agent's name when the title does not say it: hermes typed into a
  // "PW5" tab, or a Claude tab renamed "agent개선", read as a shell by their titles alone.
  function labelOf(a) {
    const name = a.agent !== 'shell' && AGENTS[a.agent] ? AGENTS[a.agent].name : '';
    return name && !(a.title || '').toLowerCase().includes(name.toLowerCase()) ? `${name} · ${a.title}` : a.title;
  }
  canvas.addEventListener('mousemove', (ev) => {
    hover = pick(ev);
    canvas.style.cursor = hover ? 'pointer' : 'default';
    if (!hover) { tipEl.hidden = true; return; }
    const ag = AGENTS[hover.agent] || AGENTS.shell, st = STATES[hover.state] || STATES.running;
    tipEl.innerHTML = '';
    const line = document.createElement('div');
    const sub = document.createElement('small');
    const extra = [];
    const gardenLine = (g, text) => {
      const el = document.createElement('small');
      el.className = 'garden';
      el.textContent = `${GARDEN.mark} ${g ? g.name + (g.version ? ` v${g.version}` : '') + ' · ' : ''}${text}`;
      extra.push(el);
    };
    if (hover.kid) {
      const k = hover.kid.sub;
      const tool = k.tool ? ` · ${k.tool}` : '';
      line.textContent = k.expert
        ? `${expertLabel(k.expert)} · ${k.expert.name} — ${k.description || 'expert'}`
        : `${k.type} · ${k.description || 'sub-agent'}`;
      sub.textContent = `${(KIDS[k.state] || KIDS.stopped).label} ${fmtAge((k.endedAt || Date.now()) - k.startedAt)}`
        + ` · ${k.tools} tool${k.tools === 1 ? '' : 's'}${tool} — ${hover.title} in ${hover.ws.name}${k.parentId ? ' (nested)' : ''}`;
      if (k.expert) gardenLine(hover.garden, `expert sub-agent${k.expert.type ? ' · ' + k.expert.type : ''}`
        + (k.expert.description ? ` — ${k.expert.description.slice(0, 80)}` : ''));
    } else if (hover.duty) {
      const d = hover.duty;
      line.textContent = `${expertLabel(d)} · ${d.name} — worn by ${hover.title}`;
      sub.textContent = `${d.state === 'working' ? 'on duty' : (KIDS[d.state] || KIDS.stopped).label} `
        + `${fmtAge((d.endedAt || Date.now()) - d.startedAt)} · via ${VIA[d.via] || d.via} — in ${hover.ws.name}`;
      gardenLine(hover.garden, `the conversation works as this expert itself${d.type ? ' · ' + d.type : ''}`);
    } else {
      line.textContent = `${hover.ws.name} / ${hover.title}`;
      sub.textContent = `${ag.name} · ${st.label}${hover.focused ? ' · focused' : ''} — click to open`;
      if (hover.garden) {
        const on = [...(hover.duties ? hover.duties.values() : [])].filter(e => e.state === 'working').map(e => expertLabel(e.duty));
        gardenLine(hover.garden, `${hover.garden.experts} experts` + (on.length ? ` · on duty: ${on.join(', ')}` : ''));
        const more = hover.moreExperts || [];
        if (more.length) gardenLine(null, `+${more.length} not shown (cap ${MAX_EXPERTS}): `
          + more.map(m => m.state === 'working' ? `${m.name} (working)` : m.name).join(', '));
      }
    }
    tipEl.append(line, sub, ...extra);
    tipEl.style.left = `${hover.hit.x}px`; tipEl.style.top = `${hover.hit.top}px`;
    tipEl.hidden = false;
  });
  canvas.addEventListener('mouseleave', () => { tipEl.hidden = true; hover = null; });
  canvas.addEventListener('click', async (ev) => {
    const a = pick(ev); if (!a) return;
    burst(a.hit.x, a.hit.y, ['#3794ff', '#ffffff'], 14, 2.2);
    if (DEMO) { demoFocus(a); return; }
    try { await window.zero.invoke('sessions.focus', { group: a.session.group, tab: a.session.tab }); }
    catch (e) { console.warn('sessions.focus', e); }
  });

  // ── data source ──────────────────────────────────────────────────────────
  async function poll() {
    if (!document.hidden) {
      try { apply(DEMO ? demoTick() : await window.zero.invoke('sessions.list')); }
      catch (e) {
        console.warn('sessions.list', e);
        apply({ supported: false, workspaces: [] });
      }
    }
    setTimeout(poll, POLL_MS);
  }

  // Simulated sessions for previewing the stage outside the app.
  let demo = null;
  function demoTick() {
    if (!demo) {
      demo = [
        { name: 'AgentZeroLite', path: '/demo/azl', tabs: [['Claude', 'claude'], ['Codex', 'codex'], ['bash', 'shell']] },
        { name: 'web-shop', path: '/demo/shop', tabs: [['Claude-2', 'claude'], ['AgentOne', 'agentone']] },
        { name: 'infra', path: '/demo/infra', tabs: [['Codex-2', 'codex']] },
        { name: 'research', path: '/demo/research', tabs: [['Netclaw', 'netclaw'], ['Hermes', 'hermes'], ['Claude-3', 'claude']] },
      ].map((w, g) => ({ group: g, name: w.name, path: w.path, active: g === 0,
        sessions: w.tabs.map(([title, agent], t) => ({ group: g, tab: t, title, agent, state: agent === 'shell' ? 'running' : 'idle', focused: g === 0 && t === 0, needsAttention: false })) }));
      // The first Claude opens at work, so the preview shows a crew from the start.
      demo[0].sessions[0].state = 'working';
      // Two Claudes work in gardens (the harness-kakashi harness): this repository's and a music one.
      demo[0].sessions[0].harness = { name: 'AgentZero Lite Harness', version: '1.15.0', experts: 11, onDuty: [] };
      demo[3].sessions[2].harness = { name: 'Music Harness', version: '1.14.0', experts: 14, onDuty: [] };
    }
    const pool = ['working', 'working', 'working', 'blocked', 'done', 'done-seen', 'idle'];
    for (const w of demo)
      for (const s of w.sessions)
        if (s.agent !== 'shell' && Math.random() < 0.18) {
          s.state = pool[(Math.random() * pool.length) | 0];
          s.needsAttention = s.state === 'blocked' || s.state === 'done';
        }
    for (const w of demo) for (const s of w.sessions) if (s.agent === 'claude') { demoCrew(s); demoDuties(s); }
    return { supported: true, workspaces: JSON.parse(JSON.stringify(demo)) };
  }
  // Each garden's experts as their frontmatter names them.
  const DEMO_GARDENS = {
    'AgentZero Lite Harness': [['security-guard', 'Security Guard'], ['build-doctor', 'Build Doctor'],
      ['test-sentinel', 'Test Sentinel'], ['code-coach', 'Code Coach'], ['voice-curator', 'Voice Curator']],
    'Music Harness': [['lyricist', '작사가'], ['genre-scout', '장르 정찰병'], ['stage-director', '무대감독'],
      ['choreographer', '안무가'], ['lore-scout', '소재 정찰병']],
  };
  const demoExpert = (s) => {
    const list = DEMO_GARDENS[s.harness.name], [name, persona] = list[(Math.random() * list.length) | 0];
    return { name, persona, type: 'specialist' };
  };
  // A garden Claude at work reads an expert's definition now and then (or /harness-creator puts the
  // gardener on); the duty lasts until its turn ends.
  function demoDuties(s) {
    if (!s.harness) return;
    const t = Date.now(), h = s.harness;
    h.onDuty = h.onDuty.filter(d => !d.endedAt || t - d.endedAt < KEEP_MS);
    const on = h.onDuty.filter(d => d.state === 'working');
    if (s.state !== 'working') { for (const d of on) { d.state = 'done'; d.endedAt = t; } return; }
    if (on.length < 3 && Math.random() < 0.15) {
      const gardener = !h.onDuty.some(d => d.name === GARDENER) && Math.random() < 0.3;
      const e = gardener ? { name: GARDENER, persona: '정원지기' } : demoExpert(s);
      if (h.onDuty.some(d => d.name === e.name && d.state === 'working')) return;
      h.onDuty = h.onDuty.filter(d => d.name !== e.name);
      h.onDuty.push({ name: e.name, persona: e.persona, type: e.type, via: gardener ? 'command' : 'read', state: 'working', startedAt: t });
    }
  }
  // A working Claude launches sub-agents now and then; they call tools, sometimes start one of
  // their own, and hand back. The host's keep window applies, so finished ones fade and leave.
  const DEMO_TYPES = [['Explore', 'Survey the voice settings'], ['Explore', 'Find every caller of Publish'],
    ['Plan', 'Design the crew layout'], ['general-purpose', 'Run the test suite and report'], ['code-reviewer', 'Review the diff'],
    ['general-purpose', 'lyricist review of Comma'], ['general-purpose', 'genre-scout review of Comma'],
    ['general-purpose', 'choreographer review of Comma stage'], ['general-purpose', 'lore-scout review of Comma']];
  const DEMO_TOOLS = ['Read · Program.cs', 'Grep · WorkspaceView', 'Glob · **/*.cs', 'Bash · dotnet test', 'Read · stage.js', 'WebFetch · docs'];
  let demoSeq = 0;
  function demoCrew(s) {
    const t = Date.now();
    const crew = (s.subagents ||= []);
    s.subagents = crew.filter(k => !k.endedAt || t - k.endedAt < KEEP_MS);
    const live = s.subagents.filter(k => k.state === 'working');
    if (s.state === 'working' && live.length < 4 && Math.random() < 0.22) {
      let [type, description] = DEMO_TYPES[(Math.random() * DEMO_TYPES.length) | 0];
      // In a garden, half the launches are its experts ("lyricist review of …", as a harness writes them).
      const expert = s.harness && Math.random() < 0.5 ? demoExpert(s) : undefined;
      if (expert) { type = 'general-purpose'; description = `${expert.name} review of the latest change`; }
      const parent = !expert && live.length && Math.random() < 0.3 ? live[(Math.random() * live.length) | 0] : null;
      s.subagents.push({ id: 'demo' + ++demoSeq, parentId: parent ? parent.id : undefined, type, description,
        depth: parent ? parent.depth + 1 : 1, background: true, state: 'working', tool: undefined, tools: 0, startedAt: t, expert });
    }
    for (const k of live) {
      if (Math.random() < 0.5) { k.tools++; k.tool = DEMO_TOOLS[(Math.random() * DEMO_TOOLS.length) | 0]; }
      const kidsWorking = s.subagents.some(c => c.parentId === k.id && c.state === 'working');
      if (!kidsWorking && k.tools > 2 && Math.random() < 0.12) { k.state = Math.random() < 0.9 ? 'done' : 'stopped'; k.endedAt = t; }
    }
  }

  function demoFocus(a) {
    for (const w of demo) { w.active = w.group === a.session.group; for (const s of w.sessions) s.focused = w.active && s.tab === a.session.tab; }
    apply({ supported: true, workspaces: JSON.parse(JSON.stringify(demo)) });
  }

  resize();
  frame();
  poll();
})();
