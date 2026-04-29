'use strict';
(function () {

// ───────────────────────────────────────────────────────────
// AgentZero Lite — AI ↔ AI autonomous-discussion demo (PixiJS).
//
// Reproduces the actual flow from the source:
//   • User types into AgentBot AIMODE: "클로드군 코덱스양과 자율토론 5턴 이내에 해"
//   • LocalLLM (Gemma 4) recognizes Mode 2 trigger → emits send_to_terminal
//   • WorkspaceTerminalToolHost.SendToTerminalAsync detects first contact and
//     AUTO-PREPENDS the handshake header (no manual "teach the AI" step
//     anymore — that was the old README recipe, now obsolete).
//   • Peer terminal acks via `bot-chat "DONE(...)" --from <name>`; the
//     reactor wakes and routes the reply to the other peer.
//   • 5 turns, then done() with a one-line consensus summary.
//
// Faithful to:
//   • WorkspaceTerminalToolHost.cs:164–187 (BuildFirstContactHeader)
//   • AgentToolGrammar.cs (Mode 2 trigger phrases, FSM states)
//   • AgentBotWindow.xaml palette (#1E1E1E / #3794FF / #C586C0 / #7CFDB0)
// ───────────────────────────────────────────────────────────

const mount = document.getElementById('azd-aitalk');

const palette = {
  ink: 0x0A0A14, void: 0x14143A, panel: 0x252526, panel2: 0x1A1A2E,
  panel3: 0x2D2D30, line: 0x3E3E42, lineHi: 0x2A2A5E,
  text: 0xD4D4D4, textHi: 0xFFFFFF, dim: 0x858585, soft: 0xC8C8D8,
  cyan: 0x3794FF, magenta: 0xC586C0, mint: 0x7CFDB0, yellow: 0xDCDCAA,
  green: 0x4EC9B0, red: 0xE4324F, amber: 0xF59E0B, white: 0xFFFFFF,
};

const fontBase = {
  fontFamily: '"Cascadia Mono","Consolas","Courier New",monospace',
  fill: palette.text,
};

// Real handshake header from WorkspaceTerminalToolHost.cs:164-187,
// trimmed to the lines that matter visually for the demo.
function handshakeHeaderLines(peerName) {
  return [
    '[AgentBot Handshake — first contact, please read carefully]',
    '',
    `You are ${peerName} and I am AgentBot, an on-device AI agent`,
    'running inside the AgentZero Lite shell.',
    '',
    'Step 1 — Verify the CLI channel exists.',
    '    AgentZeroLite.exe -cli help',
    '',
    'Step 2 — Acknowledge using the same CLI.',
    `    -cli bot-chat "DONE(handshake-ok)" --from ${peerName}`,
    '',
    'Ongoing replies use the same shape:',
    `    -cli bot-chat "DONE(your reply)" --from ${peerName}`,
    '─── original message follows ───',
  ];
}

const state = {
  // FSM: idle → typing → think → act → wait → read → relay → done
  scene: 'init',
  sceneTimer: 0,
  step: 0,
  // Top peer terminals
  claude: { lines: [], handshakeShown: false, busyTick: 0 },
  codex:  { lines: [], handshakeShown: false, busyTick: 0 },
  // AgentBot AIMODE panel
  inputText: '',
  history: [],   // { side: 'user'|'sys'|'tool', text, accent }
  fsm: 0,        // 0 idle / 1 think / 2 gen / 3 act / 4 done
  toolJson: '',
  // Visual
  arrowFrom: null, // 'bot'|'claude'|'codex'
  arrowTo:   null,
  arrowProgress: 0,
};

let app, bg, wave, ui, textLayer, overlay;
const textRefs = {};
const stars = [];

function snap(v)   { return Math.round(v / 2) * 2; }
function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
function lerp(a, b, t) { return a + (b - a) * t; }

function rect(g, x, y, w, h, color, alpha = 1) {
  g.rect(snap(x), snap(y), snap(w), snap(h)).fill({ color, alpha });
}
function strokeRect(g, x, y, w, h, color, width = 2, alpha = 1) {
  g.rect(snap(x), snap(y), snap(w), snap(h)).stroke({ color, width, alpha });
}

function addText(key, text, x, y, size = 11, color = palette.text, weight = '500') {
  let n = textRefs[key];
  if (!n) {
    n = new PIXI.Text({
      text, style: { ...fontBase, fontSize: size, fill: color, fontWeight: weight, letterSpacing: 0 },
    });
    n.roundPixels = true;
    textLayer.addChild(n);
    textRefs[key] = n;
  }
  n.text = text;
  n.style.fontSize = size;
  n.style.fill = color;
  n.style.fontWeight = weight;
  n.position.set(snap(x), snap(y));
  n.visible = true;
  return n;
}
function hideUnusedText(active) {
  const set = new Set(active);
  for (const [k, n] of Object.entries(textRefs)) if (!set.has(k)) n.visible = false;
}

function pixelPanel(g, x, y, w, h, accent) {
  rect(g, x, y, w, h, palette.panel, 0.96);
  rect(g, x + 3, y + 3, w - 6, h - 6, palette.panel2, 0.7);
  strokeRect(g, x, y, w, h, palette.line, 2, 0.95);
  rect(g, x, y, w, 3, accent, 0.85);
  rect(g, x + w - 10, y + 6, 3, 3, accent, 0.95);
  rect(g, x + w - 16, y + 6, 3, 3, accent, 0.55);
}

function pixelButton(g, x, y, w, h, label, active, accent, key) {
  const fill = active ? accent : palette.panel3;
  rect(g, x, y, w, h, fill, active ? 0.92 : 0.85);
  strokeRect(g, x, y, w, h, active ? palette.white : palette.line, 2, active ? 0.6 : 0.92);
  rect(g, x + 3, y + h - 4, w - 6, 2, 0x000000, 0.3);
  addText(key, label, x + 10, y + Math.floor(h / 2) - 6, 10, active ? palette.ink : palette.text, '700');
}

// Background — same starfield aesthetic as primary demo, lighter density
function seedField() {
  stars.length = 0;
  for (let i = 0; i < 60; i += 1) {
    stars.push({ x: Math.random(), y: Math.random(), z: Math.random() * 0.9 + 0.3, tw: Math.random() * Math.PI * 2 });
  }
}
function drawBg(time) {
  const w = app.renderer.width, h = app.renderer.height;
  bg.clear();
  rect(bg, 0, 0, w, h, palette.ink, 1);
  for (let y = 0; y < h; y += 22) rect(bg, 0, y, w, 1, palette.lineHi, 0.22);
  stars.forEach((s, i) => {
    const x = ((s.x + time * 0.000012 * s.z) % 1) * w;
    const y = s.y * h;
    const pulse = 0.3 + Math.sin(time * 0.0028 + s.tw) * 0.22;
    const c = i % 7 === 0 ? palette.cyan : i % 9 === 0 ? palette.magenta : palette.dim;
    rect(bg, x, y, 2, 2, c, clamp(pulse, 0.1, 0.6));
  });
}

// ── Drawing routines ───────────────────────────────────────
function drawTitleBar(activeKeys) {
  const w = app.renderer.width;
  pixelPanel(ui, 12, 12, w - 24, 34, palette.magenta);
  rect(ui, 24, 22, 10, 10, palette.cyan, 0.95);
  addText('lg1', 'AGENT', 40, 22, 11, palette.cyan, '800');
  addText('lg2', 'ZERO', 80, 22, 11, palette.magenta, '800');
  addText('lg3', 'LITE', 116, 22, 11, palette.mint, '800');
  addText('hd-mode', '// AI ↔ AI · AUTO-HANDSHAKE · 5-TURN AUTONOMOUS', 162, 24, 10, palette.mint, '700');
  addText('hd-tag', 'DEMO', w - 60, 24, 10, palette.dim, '800');
  activeKeys.push('lg1', 'lg2', 'lg3', 'hd-mode', 'hd-tag');
}

function drawClaudeTab(x, y, w, h, time, activeKeys) {
  pixelPanel(ui, x, y, w, h, palette.cyan);
  // Tab strip
  rect(ui, x + 8, y + 6, 96, 22, palette.cyan, 0.92);
  addText('cl-tab', 'Claude1', x + 14, y + 12, 10, palette.ink, '800');
  rect(ui, x + 110, y + 6, 78, 22, palette.panel3, 0.7);
  addText('cl-tab2', 'pwsh', x + 122, y + 12, 10, palette.dim, '700');
  activeKeys.push('cl-tab', 'cl-tab2');

  // Body
  const bx = x + 10, by = y + 36, bw = w - 20, bh = h - 46;
  rect(ui, bx, by, bw, bh, palette.ink, 1);
  strokeRect(ui, bx, by, bw, bh, palette.line, 1, 0.7);
  addText('cl-hd', '$ claude', bx + 8, by + 6, 10, palette.cyan, '800');
  activeKeys.push('cl-hd');

  let line = 0;
  state.claude.lines.forEach((ln, i) => {
    if (line * 14 + 28 > bh - 8) return; // avoid overflow
    addText(`cl-${i}`, ln.text.slice(0, Math.floor((bw - 24) / 7.2)),
      bx + 8, by + 28 + line * 14, 10, ln.color, '600');
    activeKeys.push(`cl-${i}`);
    line += 1;
  });

  // Cursor
  if (Math.floor(time / 400) % 2 === 0) {
    rect(ui, bx + 8, by + 28 + line * 14 + 1, 7, 11, palette.cyan, 0.85);
  }

  // Side badge if currently active speaker
  if (state.arrowTo === 'claude') {
    rect(wave, x + w - 16, y + 6, 6, 6, palette.cyan, 0.95);
  }
}

function drawCodexTab(x, y, w, h, time, activeKeys) {
  pixelPanel(ui, x, y, w, h, palette.magenta);
  rect(ui, x + 8, y + 6, 96, 22, palette.magenta, 0.92);
  addText('cx-tab', 'Codex1', x + 14, y + 12, 10, palette.ink, '800');
  rect(ui, x + 110, y + 6, 78, 22, palette.panel3, 0.7);
  addText('cx-tab2', 'shared', x + 122, y + 12, 10, palette.dim, '700');
  activeKeys.push('cx-tab', 'cx-tab2');

  const bx = x + 10, by = y + 36, bw = w - 20, bh = h - 46;
  rect(ui, bx, by, bw, bh, palette.ink, 1);
  strokeRect(ui, bx, by, bw, bh, palette.line, 1, 0.7);
  addText('cx-hd', '$ codex', bx + 8, by + 6, 10, palette.magenta, '800');
  activeKeys.push('cx-hd');

  let line = 0;
  state.codex.lines.forEach((ln, i) => {
    if (line * 14 + 28 > bh - 8) return;
    addText(`cx-${i}`, ln.text.slice(0, Math.floor((bw - 24) / 7.2)),
      bx + 8, by + 28 + line * 14, 10, ln.color, '600');
    activeKeys.push(`cx-${i}`);
    line += 1;
  });

  if (Math.floor(time / 400) % 2 === 0) {
    rect(ui, bx + 8, by + 28 + line * 14 + 1, 7, 11, palette.magenta, 0.85);
  }

  if (state.arrowTo === 'codex') {
    rect(wave, x + w - 16, y + 6, 6, 6, palette.magenta, 0.95);
  }
}

function drawAgentBotPanel(x, y, w, h, time, activeKeys) {
  pixelPanel(ui, x, y, w, h, palette.mint);

  // Header row
  addText('ab-hd', 'AGENT BOT', x + 14, y + 10, 11, palette.cyan, '800');
  // Mode toggle (CHT / KEY / AI) — AI active
  ['CHT', 'KEY', 'AI'].forEach((mode, i) => {
    const bx = x + 96 + i * 38;
    const isActive = mode === 'AI';
    rect(ui, bx, y + 8, 34, 18, isActive ? palette.mint : palette.panel3, isActive ? 0.95 : 0.7);
    strokeRect(ui, bx, y + 8, 34, 18, palette.line, 2, 0.85);
    addText(`md-${i}`, mode, bx + 8, y + 12, 9, isActive ? palette.ink : palette.dim, '800');
    activeKeys.push(`md-${i}`);
  });
  addText('ab-tag', '// LocalLLM Gemma 4 · GBNF · one tool/turn', x + 220, y + 12, 9, palette.dim, '700');
  activeKeys.push('ab-hd', 'ab-tag');

  // Two columns: history | tool/FSM
  const splitX = x + Math.floor(w * 0.48);
  // Left: chat history (user + bot summary)
  rect(ui, x + 10, y + 32, splitX - x - 14, h - 80, palette.ink, 1);
  strokeRect(ui, x + 10, y + 32, splitX - x - 14, h - 80, palette.line, 1, 0.7);
  state.history.forEach((h_, i) => {
    const hy = y + 40 + i * 24;
    if (hy > y + h - 60) return;
    const accent = h_.side === 'user' ? palette.cyan
                 : h_.side === 'sys'  ? palette.mint
                 : h_.side === 'tool' ? palette.amber
                 : palette.dim;
    rect(ui, x + 14, hy, 4, 18, accent, 0.95);
    addText(`hi-${i}`, h_.text.slice(0, 70), x + 24, hy + 2, 10, palette.soft, '600');
    activeKeys.push(`hi-${i}`);
  });

  // Right: FSM + tool JSON
  const rx = splitX, ry = y + 32, rw = w - (splitX - x) - 14, rh = h - 80;
  rect(ui, rx, ry, rw, rh, palette.ink, 1);
  strokeRect(ui, rx, ry, rw, rh, palette.line, 1, 0.7);

  // FSM bar
  const fsmStates = ['IDLE', 'THINK', 'GEN', 'ACT', 'DONE'];
  const fsmW = (rw - 16) / fsmStates.length;
  fsmStates.forEach((s, i) => {
    const sx = rx + 8 + i * fsmW;
    const isAct = i === state.fsm;
    rect(ui, sx + 2, ry + 6, fsmW - 6, 22, isAct ? palette.amber : palette.panel3, isAct ? 0.92 : 0.65);
    strokeRect(ui, sx + 2, ry + 6, fsmW - 6, 22, palette.line, 1.5, 0.8);
    addText(`fsm-${i}`, s, sx + 8, ry + 10, 9, isAct ? palette.ink : palette.dim, '800');
    activeKeys.push(`fsm-${i}`);
  });

  // Tool JSON area
  addText('tj-hd', 'TOOL CALL · GBNF', rx + 8, ry + 36, 9, palette.amber, '800');
  activeKeys.push('tj-hd');
  const lines = (state.toolJson || '{ }').split('\n');
  lines.forEach((ln, i) => {
    if (i > 6) return;
    addText(`tj-${i}`, ln, rx + 12, ry + 54 + i * 14, 10, palette.mint, '600');
    activeKeys.push(`tj-${i}`);
  });

  // Input row at bottom
  const inY = y + h - 38, inX = x + 14, inW = w - 110, inH = 24;
  rect(ui, inX, inY, inW, inH, palette.void, 1);
  strokeRect(ui, inX, inY, inW, inH, palette.line, 2, 0.92);
  const cursorOn = Math.floor(time / 400) % 2 === 0;
  addText('in-text', state.inputText + (cursorOn ? '_' : ' '), inX + 8, inY + 6, 11, palette.soft, '600');
  pixelButton(ui, inX + inW + 8, inY, 78, inH, '▶ SEND', false, palette.cyan, 'send-lbl');
  activeKeys.push('in-text', 'send-lbl');
}

function drawArrow(time) {
  if (!state.arrowFrom || !state.arrowTo || state.arrowProgress >= 1) return;
  const w = app.renderer.width, h = app.renderer.height;
  const bot = { x: w / 2, y: h - 130 };
  const claude = { x: 60 + ((w - 120) / 2 - 40), y: 200 };
  const codex  = { x: w - 60 - ((w - 120) / 2 - 40), y: 200 };
  const points = { bot, claude, codex };
  const a = points[state.arrowFrom];
  const b = points[state.arrowTo];
  if (!a || !b) return;
  const t = clamp(state.arrowProgress, 0, 1);
  // Trail
  for (let i = 0; i < 12; i += 1) {
    const ti = clamp(t - i * 0.05, 0, 1);
    const x = lerp(a.x, b.x, ti);
    const y = lerp(a.y, b.y, ti);
    const c = state.arrowTo === 'claude' ? palette.cyan
            : state.arrowTo === 'codex'  ? palette.magenta : palette.mint;
    rect(wave, x - 3, y - 3, 6, 6, c, 0.85 - i * 0.06);
  }
}

// ── Scene script ──────────────────────────────────────────
function pushBotHistory(side, text) {
  state.history.push({ side, text });
  if (state.history.length > 6) state.history.shift();
}
function pushTermLine(target, text, color) {
  const t = state[target];
  t.lines.push({ text, color });
  if (t.lines.length > 16) t.lines.shift();
}
function injectHandshake(target, peerName) {
  if (state[target].handshakeShown) return;
  handshakeHeaderLines(peerName).forEach(ln => pushTermLine(target, ln, palette.dim));
  state[target].handshakeShown = true;
}
function resetAll() {
  state.claude = { lines: [], handshakeShown: false, busyTick: 0 };
  state.codex  = { lines: [], handshakeShown: false, busyTick: 0 };
  state.history = [];
  state.toolJson = '';
  state.fsm = 0;
  state.inputText = '';
  state.arrowFrom = null;
  state.arrowTo = null;
  state.arrowProgress = 0;
}

const USER_PROMPT = '클로드군 코덱스양과 자율토론 5턴이내에 해 (주제: REST 인증 설계)';
const TOOL_LIST   = '{\n  "tool": "list_terminals",\n  "args": {}\n}';
const TOOL_TO_CL  = '{\n  "tool": "send_to_terminal",\n  "args": {\n    "tab": "Claude1",\n    "text": "Codex와 토론 시작. 주제 제시"\n  }\n}';
const TOOL_TO_CX  = '{\n  "tool": "send_to_terminal",\n  "args": {\n    "tab": "Codex1",\n    "text": "[from Claude] " + …\n  }\n}';
const TOOL_DONE   = '{\n  "tool": "done",\n  "args": {\n    "summary": "5턴 토론 완료"\n  }\n}';

const script = [
  // 0. Idle pause
  { dt: 800,  run: () => { resetAll(); } },
  // 1. User typing
  { dt: 200,  run: () => { state.inputText = '클로드군'; } },
  { dt: 200,  run: () => { state.inputText = '클로드군 코덱스양과'; } },
  { dt: 200,  run: () => { state.inputText = '클로드군 코덱스양과 자율토론'; } },
  { dt: 250,  run: () => { state.inputText = USER_PROMPT; } },
  // 2. Send
  { dt: 800,  run: () => { pushBotHistory('user', USER_PROMPT); state.inputText = ''; state.fsm = 1; } },
  // 3. AI plans
  { dt: 600,  run: () => { state.fsm = 2; state.toolJson = TOOL_LIST; pushBotHistory('tool', '→ list_terminals'); } },
  { dt: 700,  run: () => { state.toolJson = TOOL_TO_CL.replace('Codex와 토론 시작. 주제 제시', 'Codex1와 5턴 토론. 주제: REST 인증.'); state.fsm = 3; pushBotHistory('tool', '→ send_to_terminal Claude1'); } },
  // 4. Arrow Bot → Claude
  { dt: 100,  run: () => { state.arrowFrom = 'bot'; state.arrowTo = 'claude'; state.arrowProgress = 0; } },
  { dt: 600,  run: () => { state.arrowProgress = 1; } },
  // 5. Claude receives — handshake auto-prepended (the FIX from old recipe)
  { dt: 200,  run: () => { injectHandshake('claude', 'Claude1'); } },
  { dt: 600,  run: () => { pushTermLine('claude', 'Codex1와 5턴 토론. 주제: REST 인증.', palette.text); } },
  // 6. Claude acks via bot-chat (auto-handshake works — no user teaching needed)
  { dt: 1000, run: () => { pushTermLine('claude', '$ -cli bot-chat "DONE(handshake-ok)" --from Claude1', palette.green); } },
  { dt: 800,  run: () => { pushTermLine('claude', 'DONE(POST /api/v1/users 부터. 인증?)', palette.cyan); state.fsm = 1; } },
  // 7. Reactor wakes — peer signal received
  { dt: 600,  run: () => { pushBotHistory('sys', '[peer signal Claude1 → Bearer JWT?]'); state.fsm = 2; } },
  { dt: 600,  run: () => { state.toolJson = '{\n  "tool": "send_to_terminal",\n  "args": {\n    "tab": "Codex1",\n    "text": "[from Claude] Bearer JWT?"\n  }\n}'; state.fsm = 3; pushBotHistory('tool', '→ send_to_terminal Codex1'); } },
  { dt: 100,  run: () => { state.arrowFrom = 'bot'; state.arrowTo = 'codex'; state.arrowProgress = 0; } },
  { dt: 600,  run: () => { state.arrowProgress = 1; } },
  // 8. Codex receives handshake (first time) + question
  { dt: 200,  run: () => { injectHandshake('codex', 'Codex1'); } },
  { dt: 500,  run: () => { pushTermLine('codex', '[from Claude1] Bearer JWT?', palette.text); } },
  { dt: 1000, run: () => { pushTermLine('codex', '$ -cli bot-chat "DONE(handshake-ok)" --from Codex1', palette.green); } },
  { dt: 800,  run: () => { pushTermLine('codex', 'DONE(JWT + X-Idempotency-Key. PKCE는?)', palette.magenta); state.fsm = 1; } },
  // 9. Reactor → back to Claude (NO handshake this time, just topic)
  { dt: 500,  run: () => { pushBotHistory('sys', '[peer signal Codex1 → PKCE?]'); state.fsm = 3; } },
  { dt: 100,  run: () => { state.arrowFrom = 'bot'; state.arrowTo = 'claude'; state.arrowProgress = 0; } },
  { dt: 500,  run: () => { state.arrowProgress = 1; } },
  { dt: 500,  run: () => { pushTermLine('claude', '[from Codex1] PKCE 흐름은?', palette.text); } },
  { dt: 800,  run: () => { pushTermLine('claude', 'DONE(Authorization Code + PKCE 권장)', palette.cyan); } },
  // 10. Reactor → Codex final
  { dt: 500,  run: () => { state.fsm = 3; } },
  { dt: 100,  run: () => { state.arrowFrom = 'bot'; state.arrowTo = 'codex'; state.arrowProgress = 0; } },
  { dt: 500,  run: () => { state.arrowProgress = 1; } },
  { dt: 500,  run: () => { pushTermLine('codex', '[from Claude1] PKCE 권장', palette.text); } },
  { dt: 800,  run: () => { pushTermLine('codex', 'DONE(합의: JWT + PKCE + Idempotency)', palette.magenta); } },
  // 11. Done
  { dt: 600,  run: () => { state.fsm = 4; state.toolJson = TOOL_DONE; pushBotHistory('sys', '✓ 합의: JWT + PKCE + Idempotency'); } },
  // 12. Hold then loop
  { dt: 3000, run: () => { /* loop */ } },
];

function tick(dt) {
  state.sceneTimer += dt;
  // Smooth arrow advance
  if (state.arrowFrom && state.arrowTo && state.arrowProgress < 1) {
    state.arrowProgress = clamp(state.arrowProgress + dt / 600, 0, 1);
  }
  const step = script[state.step % script.length];
  if (state.sceneTimer >= step.dt) {
    step.run();
    state.sceneTimer = 0;
    state.step += 1;
    if (state.step >= script.length) state.step = 0;
  }
}

// ── Main paint ─────────────────────────────────────────────
function paint(time) {
  const w = app.renderer.width, h = app.renderer.height;
  const activeKeys = [];
  ui.clear();
  overlay.clear();

  drawTitleBar(activeKeys);

  const colW = (w - 36) / 2 - 6;
  const colY = 56;
  const colH = h - colY - 200;
  drawClaudeTab(12, colY, colW, colH, time, activeKeys);
  drawCodexTab(w - 12 - colW, colY, colW, colH, time, activeKeys);

  // AgentBot panel (full width bottom)
  drawAgentBotPanel(12, h - 188, w - 24, 176, time, activeKeys);

  drawArrow(time);

  // Scanlines
  for (let y = 0; y < h; y += 4) {
    rect(overlay, 0, y, w, 1, palette.white, y % 12 === 0 ? 0.04 : 0.014);
  }
  hideUnusedText(activeKeys);
}

let last = 0;
function frame() {
  const now = performance.now();
  const dt = Math.min(64, now - last || 16);
  last = now;
  wave.clear();
  drawBg(now);
  tick(dt);
  paint(now);
}

async function init() {
  if (!window.PIXI) { mount.textContent = 'PixiJS failed to load.'; return; }
  app = new PIXI.Application();
  await app.init({
    resizeTo: mount, backgroundColor: palette.ink, antialias: false,
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
  app.ticker.add(frame);
}

init();

})();
