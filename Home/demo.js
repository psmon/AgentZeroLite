'use strict';
(function () {

// ───────────────────────────────────────────────────────────
// AgentZero Lite — interactive pixel-art shell demo (PixiJS).
//
// Reproduces the actual app layout — workspaces sidebar, multi-tab
// ConPTY terminal, AgentBot chat panel — in pixel-block style, then
// auto-plays four scenes that mirror real usage:
//   1. WORKSPACE — idle exploration, tab/workspace clicks
//   2. AI ↔ AI    — Claude in tab 0 sends to Codex in tab 1
//   3. VOICE       — mic capture, radial wave, transcribed text drops
//                    into the chat input
//   4. AIMODE      — on-device LocalLLM emits tool-call JSON
//
// User can click the mode buttons on the left to jump scenes, or just
// watch the auto-play loop. Style cues taken from
// D:\pencil-creator\design\xaml\output\sample09 (palette, pixel panels,
// scanlines, retro mono font, gauge ticks).
// ───────────────────────────────────────────────────────────

const mount = document.getElementById('azd-mount');

const palette = {
  ink:     0x0A0A14,
  void:    0x14143A,
  panel:   0x252526,
  panel2:  0x1A1A2E,
  panel3:  0x2D2D30,
  line:    0x3E3E42,
  lineHi:  0x2A2A5E,
  text:    0xD4D4D4,
  textHi:  0xFFFFFF,
  dim:     0x858585,
  soft:    0xC8C8D8,
  cyan:    0x3794FF,   // AgentZero brand
  magenta: 0xC586C0,
  mint:    0x7CFDB0,
  yellow:  0xDCDCAA,
  green:   0x4EC9B0,
  red:     0xE4324F,
  amber:   0xF59E0B,
  white:   0xFFFFFF,
};

const modes = [
  { id: 'workspace', label: 'WORKSPACE', accent: palette.cyan },
  { id: 'aitalk',    label: 'AI ↔ AI',    accent: palette.magenta },
  { id: 'voice',     label: 'VOICE',      accent: palette.mint },
  { id: 'aimode',    label: 'AIMODE',     accent: palette.yellow },
];

const tabs = [
  { name: 'Claude1', kind: 'claude', accent: palette.cyan },
  { name: 'Codex1',  kind: 'codex',  accent: palette.magenta },
  { name: 'pwsh1',   kind: 'shell',  accent: palette.green },
];

const workspaces = [
  { name: 'monorepo', children: ['web', 'api', 'shared'] },
  { name: 'blog',     children: [] },
  { name: 'agentzero', children: ['Project'] },
];

const state = {
  mode: 0,
  activeTab: 0,
  activeWs: 0,
  scan: true,           // CRT scanline overlay
  autoPlay: true,
  autoStep: 0,
  autoTimer: 0,
  pointer: { x: 0, y: 0, active: false },
  // per-mode runtime state
  terminalLines: [],     // { text, color, age }
  chatBubbles: [],       // { side: 'user'|'ai', text, age }
  chatInput: '',
  voiceLevel: 0.55,
  voiceActive: false,
  toolCallText: '',
  toolCallStep: 0,
};

const hitAreas = [];
const stars = [];
const textRefs = {};

let app, bg, wave, ui, textLayer, overlay;
const fontBase = {
  fontFamily: '"Cascadia Mono","Consolas","Courier New",monospace',
  fill: palette.text,
};

function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
function lerp(a, b, t)   { return a + (b - a) * t; }
function snap(v)         { return Math.round(v / 2) * 2; }

function colorMix(c1, c2, t) {
  const r = Math.round(lerp((c1 >> 16) & 255, (c2 >> 16) & 255, t));
  const g = Math.round(lerp((c1 >> 8) & 255,  (c2 >> 8) & 255,  t));
  const b = Math.round(lerp( c1       & 255,   c2       & 255,  t));
  return (r << 16) + (g << 8) + b;
}

function addText(key, text, x, y, size = 11, color = palette.text, weight = '500') {
  let node = textRefs[key];
  if (!node) {
    node = new PIXI.Text({
      text,
      style: { ...fontBase, fontSize: size, fill: color, fontWeight: weight, letterSpacing: 0 },
    });
    node.roundPixels = true;
    textLayer.addChild(node);
    textRefs[key] = node;
  }
  node.text = text;
  node.style.fontSize = size;
  node.style.fill = color;
  node.style.fontWeight = weight;
  node.position.set(snap(x), snap(y));
  node.visible = true;
  return node;
}

function hideUnusedText(activeKeys) {
  const set = new Set(activeKeys);
  for (const [key, node] of Object.entries(textRefs)) {
    if (!set.has(key)) node.visible = false;
  }
}

function rect(g, x, y, w, h, color, alpha = 1) {
  g.rect(snap(x), snap(y), snap(w), snap(h)).fill({ color, alpha });
}

function strokeRect(g, x, y, w, h, color, width = 2, alpha = 1) {
  g.rect(snap(x), snap(y), snap(w), snap(h)).stroke({ color, width, alpha });
}

function pixelPanel(g, x, y, w, h, accent = palette.cyan) {
  rect(g, x, y, w, h, palette.panel, 0.96);
  rect(g, x + 3, y + 3, w - 6, h - 6, palette.panel2, 0.7);
  strokeRect(g, x, y, w, h, palette.line, 2, 0.95);
  rect(g, x, y, w, 3, accent, 0.85);
  rect(g, x + w - 10, y + 6, 3, 3, accent, 0.95);
  rect(g, x + w - 16, y + 6, 3, 3, accent, 0.55);
}

function pixelButton(g, id, x, y, w, h, label, active, accent, activeKeys, payload) {
  const fill = active ? accent : palette.panel3;
  const fillAlpha = active ? 0.92 : 0.85;
  rect(g, x, y, w, h, fill, fillAlpha);
  strokeRect(g, x, y, w, h, active ? palette.white : palette.line, 2, active ? 0.6 : 0.92);
  rect(g, x + 3, y + h - 4, w - 6, 2, 0x000000, 0.3);
  const key = `btn-${id}`;
  addText(key, label, x + 10, y + Math.floor(h / 2) - 6, 10, active ? palette.ink : palette.text, '700');
  activeKeys.push(key);
  hitAreas.push({ id, x, y, w, h, payload });
}

function gauge(g, x, y, w, h, value, color) {
  rect(g, x, y, w, h, palette.void, 1);
  strokeRect(g, x, y, w, h, palette.line, 2, 0.85);
  const blocks = Math.floor((w - 6) / 8);
  const lit = Math.round(blocks * clamp(value, 0, 1));
  for (let i = 0; i < blocks; i += 1) {
    rect(g, x + 3 + i * 8, y + 3, 6, h - 6, i < lit ? color : palette.panel3, i < lit ? 0.95 : 0.55);
  }
}

// ── Scene plumbing ─────────────────────────────────────────
function setMode(idx) {
  state.mode = idx;
  state.autoStep = 0;
  state.autoTimer = 0;
  state.terminalLines = [];
  state.chatBubbles = [];
  state.chatInput = '';
  state.voiceActive = false;
  state.toolCallText = '';
  state.toolCallStep = 0;
}

function addTerminalLine(text, color = palette.soft) {
  state.terminalLines.push({ text, color, age: 0 });
  if (state.terminalLines.length > 14) state.terminalLines.shift();
}

function addBubble(side, text) {
  state.chatBubbles.push({ side, text, age: 0 });
  if (state.chatBubbles.length > 5) state.chatBubbles.shift();
}

// ── Background ─────────────────────────────────────────────
function seedField() {
  stars.length = 0;
  for (let i = 0; i < 90; i += 1) {
    stars.push({ x: Math.random(), y: Math.random(), z: Math.random() * 1.0 + 0.3, tw: Math.random() * Math.PI * 2 });
  }
}

function drawBackground(time) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  bg.clear();
  rect(bg, 0, 0, w, h, palette.ink, 1);
  for (let y = 0; y < h; y += 22) rect(bg, 0, y, w, 1, palette.lineHi, 0.28);
  for (let x = 0; x < w; x += 22) rect(bg, x, 0, 1, h, palette.lineHi, 0.18);
  stars.forEach((s, i) => {
    const drift = time * 0.000015 * s.z;
    const x = ((s.x + drift) % 1) * w;
    const y = s.y * h;
    const pulse = 0.3 + Math.sin(time * 0.0028 + s.tw) * 0.22;
    const c = i % 7 === 0 ? palette.cyan : i % 11 === 0 ? palette.magenta : palette.dim;
    rect(bg, x, y, s.z > 0.9 ? 3 : 2, s.z > 0.9 ? 3 : 2, c, clamp(pulse, 0.1, 0.65));
  });
}

// ── App-shell chrome (always drawn) ────────────────────────
function drawChrome(time, activeKeys) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  const accent = modes[state.mode].accent;

  // Title bar (window chrome)
  pixelPanel(ui, 12, 12, w - 24, 38, accent);
  // Wordmark dot
  rect(ui, 24, 24, 12, 12, palette.cyan, 0.95);
  addText('logo', 'AGENT', 42, 24, 12, palette.cyan, '800');
  addText('logo2', 'ZERO', 86, 24, 12, palette.magenta, '800');
  addText('logo3', 'LITE', 124, 24, 12, palette.mint, '800');
  addText('chrome-mode', `// ${modes[state.mode].label}`, 170, 26, 10, accent, '700');
  addText('chrome-tick', `tick ${String(Math.floor(time / 100) % 100000).padStart(5, '0')}`, w - 178, 26, 10, palette.dim, '600');
  // Window control glyphs (decorative)
  rect(ui, w - 60, 22, 12, 12, palette.dim, 0.65);
  rect(ui, w - 42, 22, 12, 12, palette.dim, 0.65);
  rect(ui, w - 24, 22, 12, 12, palette.red, 0.78);
  activeKeys.push('logo', 'logo2', 'logo3', 'chrome-mode', 'chrome-tick');

  // Activity rail (left edge, ~36 wide)
  const railX = 12, railY = 60, railW = 36, railH = h - 72;
  pixelPanel(ui, railX, railY, railW, railH, accent);
  const icons = [
    { ch: 'WS', tip: 'workspaces' },
    { ch: 'AI', tip: 'agentbot' },
    { ch: 'MIC', tip: 'voice' },
    { ch: 'CFG', tip: 'settings' },
  ];
  icons.forEach((ic, i) => {
    const iy = railY + 16 + i * 44;
    rect(ui, railX + 6, iy, railW - 12, 30, i === state.mode ? accent : palette.panel3, i === state.mode ? 0.85 : 0.6);
    strokeRect(ui, railX + 6, iy, railW - 12, 30, palette.line, 1, 0.7);
    addText(`ic-${i}`, ic.ch, railX + 9, iy + 9, 9, i === state.mode ? palette.ink : palette.dim, '800');
    activeKeys.push(`ic-${i}`);
  });

  // Mode nav (right edge, four buttons stacked vertically)
  const navW = 132, navX = w - navW - 12, navY = 60;
  modes.forEach((m, i) => {
    pixelButton(ui, `mode-${i}`, navX, navY + i * 42, navW, 36,
      `${String(i + 1).padStart(2, '0')} ${m.label}`,
      state.mode === i, m.accent, activeKeys, { mode: i });
  });

  // Auto-play indicator
  pixelPanel(ui, navX, navY + 4 * 42 + 10, navW, 80, palette.dim);
  addText('ap-title', 'AUTOPLAY', navX + 10, navY + 4 * 42 + 22, 10, palette.dim, '800');
  addText('ap-state', state.autoPlay ? '▶ ON' : '⏸ OFF', navX + 10, navY + 4 * 42 + 40, 10, state.autoPlay ? palette.mint : palette.amber, '800');
  pixelButton(ui, 'autoplay', navX + 10, navY + 4 * 42 + 54, navW - 20, 22,
    state.autoPlay ? 'PAUSE' : 'RESUME', false,
    state.autoPlay ? palette.amber : palette.mint, activeKeys, { toggle: 'autoplay' });
  activeKeys.push('ap-title', 'ap-state');
}

// ── Mode 0: WORKSPACE ──────────────────────────────────────
function drawWorkspaceMode(time, activeKeys) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  const accent = palette.cyan;

  // Sidebar (workspaces + sessions)
  const sbX = 56, sbY = 60, sbW = 180, sbH = h - 72;
  pixelPanel(ui, sbX, sbY, sbW, sbH, accent);
  addText('sb-title', 'WORKSPACES', sbX + 12, sbY + 16, 11, accent, '800');
  activeKeys.push('sb-title');
  let row = 0;
  workspaces.forEach((ws, i) => {
    const ry = sbY + 38 + row * 22;
    const isActive = i === state.activeWs;
    if (isActive) rect(ui, sbX + 4, ry, sbW - 8, 20, accent, 0.18);
    addText(`ws-${i}`, `${isActive ? '▾' : '▸'} ${ws.name}`, sbX + 12, ry + 4, 11, isActive ? palette.textHi : palette.soft, isActive ? '800' : '600');
    activeKeys.push(`ws-${i}`);
    hitAreas.push({ id: `ws-${i}`, x: sbX + 4, y: ry, w: sbW - 8, h: 20, payload: { ws: i } });
    row += 1;
    if (isActive) {
      ws.children.forEach((child, j) => {
        const cy = sbY + 38 + row * 22;
        addText(`ws-${i}-${j}`, `   ${child}`, sbX + 12, cy + 4, 11, palette.dim, '600');
        activeKeys.push(`ws-${i}-${j}`);
        row += 1;
      });
    }
  });
  // Sessions header
  const sessY = sbY + 38 + row * 22 + 8;
  addText('sess-title', 'SESSIONS', sbX + 12, sessY, 11, palette.magenta, '800');
  activeKeys.push('sess-title');
  tabs.forEach((t, i) => {
    const ty = sessY + 22 + i * 20;
    rect(ui, sbX + 12, ty + 6, 8, 8, t.accent, 0.95);
    addText(`sess-${i}`, t.name, sbX + 26, ty + 4, 10, palette.soft, '600');
    activeKeys.push(`sess-${i}`);
  });

  // Tab strip
  const tabX = sbX + sbW + 12, tabY = 60, tabW = w - tabX - 156;
  pixelPanel(ui, tabX, tabY, tabW, 38, accent);
  let tx = tabX + 8;
  tabs.forEach((t, i) => {
    const tw = 96;
    pixelButton(ui, `tab-${i}`, tx, tabY + 4, tw, 30, t.name, i === state.activeTab, t.accent, activeKeys, { tab: i });
    tx += tw + 4;
  });
  pixelButton(ui, 'tab-plus', tx, tabY + 4, 26, 30, '+', false, palette.dim, activeKeys, { newTab: true });

  // Active terminal area
  const termX = tabX, termY = tabY + 50, termW = tabW, termH = h - termY - 178;
  pixelPanel(ui, termX, termY, termW, termH, tabs[state.activeTab].accent);
  // CRT-ish header label
  addText('term-hdr', `[${tabs[state.activeTab].name}] ConPTY · ${workspaces[state.activeWs].name}`, termX + 14, termY + 12, 10, tabs[state.activeTab].accent, '800');
  activeKeys.push('term-hdr');
  // Terminal lines
  state.terminalLines.forEach((ln, i) => {
    addText(`term-${i}`, ln.text, termX + 14, termY + 36 + i * 16, 11, ln.color, '600');
    activeKeys.push(`term-${i}`);
  });
  // Caret
  if (Math.floor(time / 400) % 2 === 0) {
    const cy = termY + 36 + state.terminalLines.length * 16;
    rect(ui, termX + 14, cy + 2, 8, 12, tabs[state.activeTab].accent, 0.9);
  }

  // Bottom panel (AgentBot chat)
  const botX = termX, botY = termY + termH + 6, botW = termW, botH = h - botY - 14;
  pixelPanel(ui, botX, botY, botW, botH, palette.magenta);
  // Bottom panel tabs
  ['AGENT BOT ▾', 'OUTPUT', 'LOG', 'NOTE'].forEach((label, i) => {
    const bx = botX + 8 + i * 100;
    rect(ui, bx, botY + 4, 92, 22, i === 0 ? palette.magenta : palette.panel3, i === 0 ? 0.9 : 0.7);
    strokeRect(ui, bx, botY + 4, 92, 22, palette.line, 2, 0.85);
    addText(`bp-${i}`, label, bx + 8, botY + 10, 10, i === 0 ? palette.ink : palette.dim, '800');
    activeKeys.push(`bp-${i}`);
  });
  // Chat content area
  state.chatBubbles.forEach((b, i) => {
    const by = botY + 36 + i * 22;
    const bx = b.side === 'user' ? botX + botW - 320 : botX + 16;
    const bw = 304;
    const col = b.side === 'user' ? palette.cyan : palette.magenta;
    rect(ui, bx, by, bw, 18, col, b.side === 'user' ? 0.32 : 0.22);
    strokeRect(ui, bx, by, bw, 18, col, 2, 0.85);
    addText(`bub-${i}`, (b.side === 'user' ? '▸ ' : '◂ ') + b.text, bx + 6, by + 4, 10, palette.text, '600');
    activeKeys.push(`bub-${i}`);
  });
  // Input box
  const inX = botX + 14, inY = botY + botH - 32, inW = botW - 100, inH = 22;
  rect(ui, inX, inY, inW, inH, palette.void, 1);
  strokeRect(ui, inX, inY, inW, inH, palette.line, 2, 0.92);
  const cursorOn = Math.floor(time / 400) % 2 === 0;
  addText('chat-in', state.chatInput + (cursorOn ? '_' : ' '), inX + 8, inY + 6, 10, palette.soft, '600');
  pixelButton(ui, 'send', inX + inW + 8, inY, 70, inH, 'SEND', false, palette.cyan, activeKeys, { send: true });
  activeKeys.push('chat-in');
}

// ── Mode 1: AI ↔ AI ─────────────────────────────────────────
function drawAiTalkMode(time, activeKeys) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  const accent = palette.magenta;

  // Two side-by-side terminal cards (Claude tab / Codex tab)
  const colY = 64, colH = h - 88;
  const colW = (w - 56 - 156 - 24) / 2;
  const claudeX = 56;
  const codexX  = claudeX + colW + 12;

  // Claude card (left)
  pixelPanel(ui, claudeX, colY, colW, colH, palette.cyan);
  addText('claude-hdr', '[Claude1] · monorepo', claudeX + 14, colY + 12, 11, palette.cyan, '800');
  activeKeys.push('claude-hdr');

  // Codex card (right)
  pixelPanel(ui, codexX, colY, colW, colH, palette.magenta);
  addText('codex-hdr', '[Codex1] · monorepo', codexX + 14, colY + 12, 11, palette.magenta, '800');
  activeKeys.push('codex-hdr');

  // Render scripted dialog
  state.terminalLines.forEach((ln, i) => {
    const targetX = ln.color === palette.cyan ? claudeX : codexX;
    addText(`tt-${i}`, ln.text, targetX + 14, colY + 36 + (i % 12) * 16, 11, ln.color, '600');
    activeKeys.push(`tt-${i}`);
  });

  // Animated arrow between columns showing the current direction
  const arrowY = colY + colH / 2;
  if (state.toolCallStep % 2 === 0) {
    drawArrow(claudeX + colW, arrowY, codexX, arrowY, palette.cyan, time);
  } else {
    drawArrow(codexX, arrowY + 18, claudeX + colW, arrowY + 18, palette.magenta, time);
  }

  addText('aitalk-cap', 'tab 0 (Claude) ↔ tab 1 (Codex) · WM_COPYDATA + MMF · no cloud relay',
    claudeX, h - 24, 10, accent, '700');
  activeKeys.push('aitalk-cap');
}

function drawArrow(x1, y1, x2, y2, color, time) {
  // Pixel-block arrow with an animated packet
  const segs = 16;
  for (let i = 0; i < segs; i += 1) {
    const t = i / segs;
    const x = lerp(x1, x2, t);
    const y = lerp(y1, y2, t);
    rect(wave, x - 2, y - 2, 4, 4, color, 0.35 + Math.sin(time * 0.005 + i * 0.4) * 0.25);
  }
  // Arrowhead
  const dir = x2 > x1 ? 1 : -1;
  for (let k = 0; k < 4; k += 1) {
    rect(wave, x2 - dir * (k + 1) * 4, y1 - k * 2, 4, 4, color, 0.92);
    rect(wave, x2 - dir * (k + 1) * 4, y1 + k * 2, 4, 4, color, 0.92);
  }
  // Animated packet
  const pt = (time * 0.0006) % 1;
  const px = lerp(x1, x2, pt);
  const py = lerp(y1, y2, pt);
  rect(wave, px - 4, py - 4, 8, 8, palette.white, 0.98);
}

// ── Mode 2: VOICE ──────────────────────────────────────────
function drawVoiceMode(time, activeKeys) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  const accent = palette.mint;

  const leftX = 56, leftY = 64, leftW = (w - 56 - 156 - 24) * 0.5, leftH = h - 88;
  pixelPanel(ui, leftX, leftY, leftW, leftH, accent);
  addText('voice-hdr', '🎙 VOICE INPUT · Whisper.net (Vulkan)', leftX + 14, leftY + 12, 11, accent, '800');
  activeKeys.push('voice-hdr');

  // Radial wave centered in left card
  const cx = leftX + leftW / 2;
  const cy = leftY + leftH / 2 - 20;
  drawRadialPixelWave(wave, cx, cy, 60, 40, time, accent, 5);

  // Voice level gauge
  const gx = leftX + 24, gy = leftY + leftH - 84;
  addText('vl-label', 'VOICE LEVEL', gx, gy, 10, palette.dim, '700');
  gauge(ui, gx, gy + 14, leftW - 48, 18, state.voiceActive ? state.voiceLevel : 0.06, accent);
  addText('vl-state', state.voiceActive ? 'CAPTURING…' : 'IDLE', gx, gy + 38, 10, state.voiceActive ? palette.red : palette.dim, '800');
  activeKeys.push('vl-label', 'vl-state');

  pixelButton(ui, 'mic-toggle', gx, gy + 56, 110, 22, state.voiceActive ? 'STOP MIC' : 'START MIC',
    state.voiceActive, state.voiceActive ? palette.red : palette.mint, activeKeys, { mic: true });

  // Right card: pipeline + transcription
  const rx = leftX + leftW + 12, ry = leftY, rw = w - rx - 156 - 12, rh = leftH;
  pixelPanel(ui, rx, ry, rw, rh, palette.cyan);
  addText('pipe-hdr', 'STT PIPELINE', rx + 14, ry + 12, 11, palette.cyan, '800');
  activeKeys.push('pipe-hdr');

  const stages = [
    { lbl: 'MIC', col: palette.green },
    { lbl: 'VAD', col: palette.amber },
    { lbl: 'WHISPER', col: palette.mint },
    { lbl: 'AGENTBOT', col: palette.cyan },
    { lbl: 'TERMINAL', col: palette.magenta },
  ];
  const sy = ry + 44;
  stages.forEach((s, i) => {
    const stx = rx + 16 + i * ((rw - 32) / stages.length);
    const stw = (rw - 32) / stages.length - 6;
    const lit = state.voiceActive && (Math.floor(time / 220) % stages.length) === i;
    rect(ui, stx, sy, stw, 30, lit ? s.col : palette.panel3, lit ? 0.92 : 0.7);
    strokeRect(ui, stx, sy, stw, 30, palette.line, 2, 0.85);
    addText(`stg-${i}`, s.lbl, stx + 6, sy + 9, 10, lit ? palette.ink : palette.dim, '800');
    activeKeys.push(`stg-${i}`);
  });

  // GPU pick info
  const gpuY = sy + 60;
  addText('gpu-h', 'GPU DEVICE', rx + 16, gpuY, 10, palette.dim, '700');
  addText('gpu-v', 'auto · NVIDIA GeForce RTX 4060 Laptop GPU', rx + 102, gpuY, 10, palette.mint, '700');
  addText('gpu-h2', 'BACKEND', rx + 16, gpuY + 22, 10, palette.dim, '700');
  addText('gpu-v2', 'Vulkan → Cpu (cross-vendor: AMD / Intel / NVIDIA)', rx + 102, gpuY + 22, 10, palette.cyan, '700');
  activeKeys.push('gpu-h', 'gpu-v', 'gpu-h2', 'gpu-v2');

  // Transcription display
  const trY = gpuY + 60;
  pixelPanel(ui, rx + 12, trY, rw - 24, rh - (trY - ry) - 16, palette.magenta);
  addText('tr-h', 'TRANSCRIBED → CHAT INPUT', rx + 26, trY + 10, 10, palette.magenta, '800');
  addText('tr-text', state.chatInput || (state.voiceActive ? '…' : '(speak into the mic)'),
    rx + 26, trY + 32, 12, palette.text, '700');
  activeKeys.push('tr-h', 'tr-text');
}

function drawRadialPixelWave(g, cx, cy, radius, rays, time, accent, block = 5) {
  for (let i = 0; i < rays; i += 1) {
    const a = (i / rays) * Math.PI * 2;
    const phase = (time * 0.001 + i * 0.09) % (Math.PI * 2);
    const amp = (Math.sin(phase) * 0.5 + 0.5) * (state.voiceActive ? state.voiceLevel : 0.18);
    const len = 3 + Math.floor(amp * 6);
    for (let j = 0; j < len; j += 1) {
      const r = radius + j * (block + 2);
      const x = cx + Math.cos(a) * r;
      const y = cy + Math.sin(a) * r;
      const t = j / Math.max(1, len - 1);
      const c = colorMix(palette.dim, t > 0.5 ? accent : palette.cyan, amp);
      rect(g, x, y, block, block, c, 0.3 + amp * 0.6);
    }
  }
  rect(g, cx - 12, cy - 12, 24, 24, palette.void, 1);
  strokeRect(g, cx - 12, cy - 12, 24, 24, accent, 2, 0.92);
  rect(g, cx - 5, cy - 5, 10, 10, state.voiceActive ? palette.red : accent, 0.95);
}

// ── Mode 3: AIMODE ──────────────────────────────────────────
function drawAiModeMode(time, activeKeys) {
  const w = app.renderer.width;
  const h = app.renderer.height;
  const accent = palette.yellow;

  // Left: tool catalog
  const lx = 56, ly = 64, lw = 220, lh = h - 88;
  pixelPanel(ui, lx, ly, lw, lh, accent);
  addText('tc-hdr', 'TOOL CATALOG', lx + 12, ly + 12, 11, accent, '800');
  activeKeys.push('tc-hdr');
  const tools = [
    { name: 'list_terminals', col: palette.dim },
    { name: 'read_terminal',  col: palette.dim },
    { name: 'send_to_terminal', col: palette.cyan },   // currently active
    { name: 'send_key',       col: palette.dim },
    { name: 'wait',           col: palette.dim },
    { name: 'done',           col: palette.dim },
  ];
  tools.forEach((t, i) => {
    const ty = ly + 38 + i * 26;
    const isActive = state.toolCallStep === i || (state.toolCallStep === 2 && t.name === 'send_to_terminal');
    rect(ui, lx + 8, ty, lw - 16, 22, isActive ? accent : palette.panel3, isActive ? 0.4 : 0.7);
    strokeRect(ui, lx + 8, ty, lw - 16, 22, isActive ? accent : palette.line, 2, isActive ? 0.95 : 0.85);
    addText(`tool-${i}`, t.name, lx + 16, ty + 6, 10, isActive ? palette.text : palette.dim, isActive ? '800' : '600');
    activeKeys.push(`tool-${i}`);
  });

  addText('llm-hdr', 'LOCAL LLM', lx + 12, ly + lh - 100, 11, palette.cyan, '800');
  addText('llm-v', 'Gemma 4 E4B', lx + 12, ly + lh - 78, 10, palette.text, '700');
  addText('llm-c', 'GBNF · one tool/turn', lx + 12, ly + lh - 60, 10, palette.dim, '600');
  addText('llm-p', `STEP ${state.toolCallStep + 1} / 4`, lx + 12, ly + lh - 36, 10, palette.mint, '800');
  activeKeys.push('llm-hdr', 'llm-v', 'llm-c', 'llm-p');

  // Right: tool-call JSON + reactor FSM
  const rx = lx + lw + 12, ry = ly, rw = w - rx - 156 - 12, rh = lh;
  pixelPanel(ui, rx, ry, rw, rh, palette.cyan);
  addText('json-hdr', 'TOOL CALL · GBNF-CONSTRAINED OUTPUT', rx + 14, ry + 12, 11, palette.cyan, '800');
  activeKeys.push('json-hdr');

  // Render JSON typed-out by step
  const lines = state.toolCallText.split('\n');
  lines.forEach((ln, i) => {
    addText(`json-${i}`, ln, rx + 18, ry + 40 + i * 16, 11, i === lines.length - 1 ? palette.mint : palette.soft, '600');
    activeKeys.push(`json-${i}`);
  });

  // Reactor FSM at the bottom
  const fsmY = ry + rh - 110;
  addText('fsm-hdr', 'REACTOR FSM · Idle → Thinking → Generating → Acting → Done', rx + 14, fsmY, 10, palette.magenta, '800');
  activeKeys.push('fsm-hdr');
  const fsmStates = ['IDLE', 'THINK', 'GEN', 'ACT', 'DONE'];
  fsmStates.forEach((s, i) => {
    const sx = rx + 14 + i * ((rw - 28) / fsmStates.length);
    const sw = (rw - 28) / fsmStates.length - 6;
    const isActive = i === Math.min(fsmStates.length - 1, Math.floor(state.toolCallStep) + 1);
    rect(ui, sx, fsmY + 24, sw, 36, isActive ? palette.amber : palette.panel3, isActive ? 0.92 : 0.65);
    strokeRect(ui, sx, fsmY + 24, sw, 36, palette.line, 2, 0.85);
    addText(`fsm-${i}`, s, sx + 8, fsmY + 38, 10, isActive ? palette.ink : palette.dim, '800');
    activeKeys.push(`fsm-${i}`);
  });
}

// ── Auto-play scripts ──────────────────────────────────────
const scripts = {
  workspace: [
    { dt: 1500, run: () => addTerminalLine('$ claude', palette.cyan) },
    { dt: 1200, run: () => addTerminalLine('Welcome to Claude. Ask me anything.', palette.soft) },
    { dt: 1500, run: () => { state.chatInput = 'run tests and summarize'; } },
    { dt: 1500, run: () => { addBubble('user', 'run tests and summarize'); state.chatInput = ''; } },
    { dt: 1200, run: () => addTerminalLine('> run tests and summarize', palette.cyan) },
    { dt: 1500, run: () => addTerminalLine('Running headless suite…', palette.dim) },
    { dt: 1500, run: () => addTerminalLine('  ✓ ZeroCommon.Tests · 12/12', palette.green) },
    { dt: 1200, run: () => addTerminalLine('  ✓ AgentTest · 38/38', palette.green) },
    { dt: 1200, run: () => addBubble('ai', '50/50 green. RT factor 0.8x avg.') },
    { dt: 2400, run: () => { /* loop */ } },
  ],
  aitalk: [
    { dt: 1200, run: () => addTerminalLine('claude > greet Codex', palette.cyan) },
    { dt: 1200, run: () => { state.toolCallStep = 0; addTerminalLine('terminal-send 0 1 "hi Codex"', palette.cyan); } },
    { dt: 1500, run: () => { state.toolCallStep = 1; addTerminalLine('codex < "hi Codex"', palette.magenta); } },
    { dt: 1200, run: () => addTerminalLine('codex > "hey Claude, ready"', palette.magenta) },
    { dt: 1500, run: () => { state.toolCallStep = 0; addTerminalLine('claude < "hey Claude, ready"', palette.cyan); } },
    { dt: 1200, run: () => addTerminalLine('claude > propose REST design', palette.cyan) },
    { dt: 1500, run: () => { state.toolCallStep = 1; addTerminalLine('codex < REST design …', palette.magenta); } },
    { dt: 2400, run: () => { /* loop */ } },
  ],
  voice: [
    { dt: 1000, run: () => { state.voiceActive = true; } },
    { dt: 600,  run: () => { state.voiceLevel = 0.42; } },
    { dt: 600,  run: () => { state.voiceLevel = 0.78; } },
    { dt: 800,  run: () => { state.chatInput = '오늘 작업한'; } },
    { dt: 600,  run: () => { state.chatInput = '오늘 작업한 PR'; } },
    { dt: 600,  run: () => { state.chatInput = '오늘 작업한 PR 요약해줘'; } },
    { dt: 800,  run: () => { state.voiceLevel = 0.32; } },
    { dt: 800,  run: () => { state.voiceActive = false; state.voiceLevel = 0.06; } },
    { dt: 1500, run: () => { state.chatInput = ''; /* sent */ } },
    { dt: 2000, run: () => { /* loop */ } },
  ],
  aimode: [
    { dt: 800,  run: () => { state.toolCallStep = 0; state.toolCallText = '{'; } },
    { dt: 600,  run: () => { state.toolCallText = '{\n  "tool": "list_terminals",'; } },
    { dt: 600,  run: () => { state.toolCallText = '{\n  "tool": "list_terminals",\n  "args": {}'; } },
    { dt: 800,  run: () => { state.toolCallText = '{\n  "tool": "list_terminals",\n  "args": {}\n}'; } },
    { dt: 1000, run: () => { state.toolCallStep = 2; state.toolCallText = '{\n  "tool": "send_to_terminal",'; } },
    { dt: 600,  run: () => { state.toolCallText = '{\n  "tool": "send_to_terminal",\n  "args": {'; } },
    { dt: 600,  run: () => { state.toolCallText = '{\n  "tool": "send_to_terminal",\n  "args": {\n    "tab": "Claude1",'; } },
    { dt: 800,  run: () => { state.toolCallText = '{\n  "tool": "send_to_terminal",\n  "args": {\n    "tab": "Claude1",\n    "text": "summarize"\n  }\n}'; } },
    { dt: 1500, run: () => { state.toolCallStep = 5; state.toolCallText = '{\n  "tool": "done",\n  "args": {\n    "summary": "ok"\n  }\n}'; } },
    { dt: 2400, run: () => { /* loop */ } },
  ],
};

function tickAuto(dt) {
  if (!state.autoPlay) return;
  const modeId = modes[state.mode].id;
  const script = scripts[modeId];
  if (!script || !script.length) return;
  state.autoTimer += dt;
  const step = script[state.autoStep % script.length];
  if (state.autoTimer >= step.dt) {
    step.run();
    state.autoTimer = 0;
    state.autoStep += 1;
    if (state.autoStep >= script.length) {
      state.autoStep = 0;
      // soft reset between loops
      state.terminalLines = [];
      if (modeId === 'aimode') { state.toolCallText = ''; state.toolCallStep = 0; }
    }
  }
}

// ── Main draw ──────────────────────────────────────────────
function drawShell(time) {
  const activeKeys = [];
  hitAreas.length = 0;
  ui.clear();
  overlay.clear();
  drawChrome(time, activeKeys);
  if (state.mode === 0) drawWorkspaceMode(time, activeKeys);
  else if (state.mode === 1) drawAiTalkMode(time, activeKeys);
  else if (state.mode === 2) drawVoiceMode(time, activeKeys);
  else if (state.mode === 3) drawAiModeMode(time, activeKeys);

  if (state.scan) {
    const w = app.renderer.width, h = app.renderer.height;
    for (let y = 0; y < h; y += 4) {
      rect(overlay, 0, y, w, 1, palette.white, y % 12 === 0 ? 0.04 : 0.015);
    }
  }
  hideUnusedText(activeKeys);
}

// ── Input handling ─────────────────────────────────────────
function handlePointerDown(event) {
  const r = app.canvas.getBoundingClientRect();
  const x = (event.clientX - r.left) * (app.renderer.width / r.width);
  const y = (event.clientY - r.top)  * (app.renderer.height / r.height);
  const hit = hitAreas.find(h => x >= h.x && x <= h.x + h.w && y >= h.y && y <= h.y + h.h);
  if (!hit) return;
  if (hit.payload && hit.payload.mode !== undefined) {
    setMode(hit.payload.mode);
  } else if (hit.payload && hit.payload.tab !== undefined) {
    state.activeTab = hit.payload.tab;
  } else if (hit.payload && hit.payload.ws !== undefined) {
    state.activeWs = hit.payload.ws;
  } else if (hit.payload && hit.payload.toggle === 'autoplay') {
    state.autoPlay = !state.autoPlay;
  } else if (hit.payload && hit.payload.send) {
    if (state.chatInput) {
      addBubble('user', state.chatInput);
      addTerminalLine('> ' + state.chatInput, palette.cyan);
      state.chatInput = '';
    }
  } else if (hit.payload && hit.payload.mic) {
    state.voiceActive = !state.voiceActive;
    state.voiceLevel = state.voiceActive ? 0.6 : 0.06;
  }
}

let lastFrameTime = 0;
function frame() {
  const now = performance.now();
  const dt = Math.min(64, now - lastFrameTime || 16);
  lastFrameTime = now;
  wave.clear();
  drawBackground(now);
  tickAuto(dt);
  drawShell(now);
}

async function init() {
  if (!window.PIXI) {
    mount.textContent = 'PixiJS failed to load.';
    return;
  }
  app = new PIXI.Application();
  await app.init({
    resizeTo: mount,
    backgroundColor: palette.ink,
    antialias: false,
    autoDensity: true,
    resolution: Math.min(window.devicePixelRatio || 1, 2),
    preference: 'webgl',
  });
  app.stage.roundPixels = true;
  mount.appendChild(app.canvas);
  app.canvas.style.imageRendering = 'pixelated';

  bg = new PIXI.Graphics();
  wave = new PIXI.Graphics();
  ui = new PIXI.Graphics();
  textLayer = new PIXI.Container();
  overlay = new PIXI.Graphics();
  app.stage.addChild(bg, wave, ui, textLayer, overlay);

  seedField();
  app.canvas.addEventListener('pointerdown', handlePointerDown);
  window.addEventListener('keydown', (e) => {
    if (e.key >= '1' && e.key <= '4') setMode(Number(e.key) - 1);
    if (e.key.toLowerCase() === 'p') state.autoPlay = !state.autoPlay;
    if (e.key.toLowerCase() === 's') state.scan = !state.scan;
  });
  app.ticker.add(frame);
}

init();

})();
