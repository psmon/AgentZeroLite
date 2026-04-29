'use strict';
(function () {

// ───────────────────────────────────────────────────────────
// AgentZero Lite — voice → terminal CHT mode demo (PixiJS).
//
// Mirrors the actual flow:
//   • Mic toolbar visible on the AskBot/AgentBot panel (toggle, mute,
//     volume slider, waveform strip — see AgentBotWindow.xaml).
//   • NAudio captures 16k mono → VoiceCaptureService VAD detects an
//     utterance → frames flow through the legacy or stream pipeline →
//     WhisperLocalStt.TranscribeAsync produces text.
//   • The transcript auto-fills `txtInput` and SendCurrentInput()
//     fires immediately — no manual Send press. In CHT mode, the text
//     is forwarded to the active terminal via session.WriteAndSubmit.
//
// Demo loops three sample utterances so visitors see the full
// "speak → text appears in input → text appears in terminal → AI
// replies" round-trip without any user interaction.
// ───────────────────────────────────────────────────────────

const mount = document.getElementById('azd-voice');

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

const utterances = [
  { ko: '오늘 작업한 PR 요약해줘',     en: 'summarize today\'s PRs',
    response: ['Summarising open PRs in this branch…',
               '  #142  feat(voice): Vulkan + multi-GPU picker · merged',
               '  #143  docs: voice section in README · pending review',
               '  #144  demo: pixel-art interactive shell · WIP',
               'Total: 3 PRs · 2 merged · 1 in review.'] },
  { ko: '테스트 돌리고 결과 알려줘',    en: 'run tests + report',
    response: ['$ dotnet test ZeroCommon.Tests',
               '  Passed!  - Failed: 0, Passed: 12, Skipped: 0',
               '$ dotnet test AgentTest',
               '  Passed!  - Failed: 0, Passed: 38, Skipped: 0',
               '50/50 green · avg 0.8x realtime.'] },
  { ko: 'git status 확인',              en: 'check git status',
    response: ['$ git status',
               'On branch main · clean working tree',
               'ahead of origin/main by 2 commits.',
               'Latest: 5c3dff7 docs: voice feature in README + Home'] },
];

const state = {
  step: 0,
  timer: 0,
  inputText: '',
  termLines: [],
  micActive: false,
  micMuted: false,
  micVolume: 78,
  voiceLevel: 0.04,
  vadActive: false,         // VAD currently inside an utterance
  whisperBusy: false,
  whisperProgress: 0,
  utteranceIdx: 0,
  // The animated bars in the waveform strip
  waveBars: new Array(28).fill(0).map(() => Math.random() * 0.1),
  // Indicator for the "other tab being typed in by keyboard" — visual only
  typingActivity: 0,
};

let app, bg, wave, ui, textLayer, overlay;
const textRefs = {};
const stars = [];

function snap(v) { return Math.round(v / 2) * 2; }
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

// ── Background ─────────────────────────────────────────────
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
    const c = i % 7 === 0 ? palette.cyan : i % 9 === 0 ? palette.mint : palette.dim;
    rect(bg, x, y, 2, 2, c, clamp(pulse, 0.1, 0.6));
  });
}

// ── Layout ────────────────────────────────────────────────
function drawTitleBar(activeKeys) {
  const w = app.renderer.width;
  pixelPanel(ui, 12, 12, w - 24, 34, palette.mint);
  rect(ui, 24, 22, 10, 10, palette.cyan, 0.95);
  addText('lg1', 'AGENT', 40, 22, 11, palette.cyan, '800');
  addText('lg2', 'ZERO', 80, 22, 11, palette.magenta, '800');
  addText('lg3', 'LITE', 116, 22, 11, palette.mint, '800');
  addText('hd-mode', '// VOICE → TERMINAL · CHT MODE · auto-send', 162, 24, 10, palette.mint, '700');
  addText('hd-tag', 'DEMO', w - 60, 24, 10, palette.dim, '800');
  activeKeys.push('lg1', 'lg2', 'lg3', 'hd-mode', 'hd-tag');
}

function drawClaudeTerminal(x, y, w, h, time, activeKeys) {
  pixelPanel(ui, x, y, w, h, palette.cyan);

  // Tab strip — Claude active, second pwsh tab to suggest "the other one is being keyboarded"
  rect(ui, x + 8, y + 6, 100, 22, palette.cyan, 0.95);
  addText('cl-tab', 'Claude1', x + 14, y + 12, 10, palette.ink, '800');
  // Inactive pwsh tab with a small typing indicator (= user is keyboard-typing in pwsh tab)
  rect(ui, x + 114, y + 6, 92, 22, palette.panel3, 0.7);
  addText('cl-tab2', 'pwsh1', x + 122, y + 12, 10, palette.dim, '700');
  if (Math.floor(time / 380) % 2 === 0 && state.typingActivity > 0) {
    rect(ui, x + 196, y + 12, 4, 8, palette.green, 0.95);
  }
  rect(ui, x + 210, y + 6, 22, 22, palette.panel3, 0.5);
  addText('cl-plus', '+', x + 218, y + 12, 11, palette.dim, '700');
  activeKeys.push('cl-tab', 'cl-tab2', 'cl-plus');

  // Body
  const bx = x + 10, by = y + 36, bw = w - 20, bh = h - 46;
  rect(ui, bx, by, bw, bh, palette.ink, 1);
  strokeRect(ui, bx, by, bw, bh, palette.line, 1, 0.7);
  addText('cl-hd', '$ claude · monorepo/api', bx + 8, by + 6, 10, palette.cyan, '800');
  activeKeys.push('cl-hd');

  let line = 0;
  state.termLines.forEach((ln, i) => {
    if (line * 14 + 28 > bh - 14) return;
    addText(`cl-${i}`, ln.text.slice(0, Math.floor((bw - 24) / 7.2)),
      bx + 8, by + 28 + line * 14, 10, ln.color, '600');
    activeKeys.push(`cl-${i}`);
    line += 1;
  });
  if (Math.floor(time / 400) % 2 === 0) {
    rect(ui, bx + 8, by + 28 + line * 14 + 1, 7, 11, palette.cyan, 0.85);
  }

  // Side caption
  addText('cl-cap', 'voice → keyboard parallel · two channels, one supervisor',
    bx + 8, y + h - 18, 9, palette.dim, '700');
  activeKeys.push('cl-cap');
}

function drawAgentBotChtPanel(x, y, w, h, time, activeKeys) {
  pixelPanel(ui, x, y, w, h, palette.cyan);

  // Header row: AGENT BOT label + mode toggles
  addText('ab-hd', 'AGENT BOT', x + 14, y + 10, 11, palette.cyan, '800');
  ['CHT', 'KEY', 'AI'].forEach((mode, i) => {
    const bx = x + 96 + i * 36;
    const isActive = mode === 'CHT';
    rect(ui, bx, y + 8, 32, 18, isActive ? palette.cyan : palette.panel3, isActive ? 0.95 : 0.7);
    strokeRect(ui, bx, y + 8, 32, 18, palette.line, 2, 0.85);
    addText(`md-${i}`, mode, bx + 8, y + 12, 9, isActive ? palette.ink : palette.dim, '800');
    activeKeys.push(`md-${i}`);
  });
  addText('ab-tag', '// chat-mode → forwards text to active terminal',
    x + 220, y + 12, 9, palette.dim, '700');
  activeKeys.push('ab-hd', 'ab-tag');

  // Voice toolbar (this row mirrors AgentBotWindow.xaml's voice toolbar
  // visible-when-mic-on)
  const tbY = y + 36;
  // Mic toggle
  rect(ui, x + 14, tbY, 32, 24, state.micActive ? palette.green : palette.panel3, state.micActive ? 0.95 : 0.7);
  strokeRect(ui, x + 14, tbY, 32, 24, palette.line, 2, 0.85);
  addText('mic-icon', '🎤', x + 22, tbY + 6, 11, state.micActive ? palette.ink : palette.dim, '800');
  // Mute
  rect(ui, x + 50, tbY, 32, 24, state.micMuted ? palette.amber : palette.panel3, 0.85);
  strokeRect(ui, x + 50, tbY, 32, 24, palette.line, 2, 0.85);
  addText('mute-icon', state.micMuted ? '🔇' : '🔈', x + 58, tbY + 6, 11, state.micMuted ? palette.ink : palette.dim, '800');
  // Volume slider
  const slX = x + 90, slW = 110;
  rect(ui, slX, tbY + 8, slW, 8, palette.void, 1);
  strokeRect(ui, slX, tbY + 8, slW, 8, palette.line, 1.5, 0.85);
  rect(ui, slX, tbY + 8, slW * (state.micVolume / 100), 8, palette.cyan, 0.95);
  rect(ui, slX + slW * (state.micVolume / 100) - 3, tbY + 4, 6, 16, palette.cyan, 0.95);
  addText('vol-lbl', `${state.micVolume}%`, slX + slW + 6, tbY + 6, 10, palette.dim, '700');

  // Waveform strip (cyan bars, 4px wide)
  const wsX = slX + slW + 50, wsY = tbY + 2, wsW = 280, wsH = 22;
  rect(ui, wsX, wsY, wsW, wsH, 0x1A1F26, 1);
  strokeRect(ui, wsX, wsY, wsW, wsH, palette.line, 1, 0.7);
  state.waveBars.forEach((amp, i) => {
    const bw = 4, bh = Math.max(2, amp * (wsH - 4));
    rect(wave, wsX + 4 + i * (bw + 2), wsY + (wsH - bh) / 2, bw, bh, palette.cyan, 0.85);
  });
  // Status text in the strip
  addText('ws-st', state.vadActive ? 'CAPTURING…' : (state.whisperBusy ? 'WHISPER…' : (state.micActive ? 'Listening' : 'Idle')),
    wsX + 8, wsY + 2, 8, state.vadActive ? palette.red : (state.whisperBusy ? palette.amber : palette.dim), '800');

  activeKeys.push('mic-icon', 'mute-icon', 'vol-lbl', 'ws-st');

  // Pipeline mini-map (right side of toolbar)
  const pipeX = wsX + wsW + 16, pipeW = w - (pipeX - x) - 14;
  if (pipeW > 240) {
    const stages = [
      { lbl: 'MIC',     col: palette.green },
      { lbl: 'VAD',     col: palette.amber },
      { lbl: 'WHISPER', col: palette.mint },
      { lbl: 'CHT',     col: palette.cyan },
      { lbl: 'TERM',    col: palette.magenta },
    ];
    const sw = (pipeW - 8) / stages.length;
    stages.forEach((s, i) => {
      const lit = stagesLit(i);
      rect(ui, pipeX + i * sw + 2, tbY, sw - 4, 24, lit ? s.col : palette.panel3, lit ? 0.92 : 0.65);
      strokeRect(ui, pipeX + i * sw + 2, tbY, sw - 4, 24, palette.line, 1.5, 0.85);
      addText(`pl-${i}`, s.lbl, pipeX + i * sw + 8, tbY + 6, 9, lit ? palette.ink : palette.dim, '800');
      activeKeys.push(`pl-${i}`);
    });
  }

  // Chat history area
  const chY = tbY + 32;
  const chH = h - (chY - y) - 38;
  rect(ui, x + 14, chY, w - 28, chH, palette.ink, 1);
  strokeRect(ui, x + 14, chY, w - 28, chH, palette.line, 1, 0.7);
  // Recent transcripts as bubbles
  const u = utterances[state.utteranceIdx];
  if (state.step >= 6 && u) {
    rect(ui, x + 22, chY + 8, w - 60, 22, palette.cyan, 0.32);
    strokeRect(ui, x + 22, chY + 8, w - 60, 22, palette.cyan, 2, 0.85);
    addText('bub-u', `▸ ${u.ko}  (voice transcript)`, x + 30, chY + 14, 10, palette.text, '700');
    activeKeys.push('bub-u');
  }
  if (state.step >= 14) {
    rect(ui, x + 22, chY + 38, w - 60, 22, palette.mint, 0.22);
    strokeRect(ui, x + 22, chY + 38, w - 60, 22, palette.mint, 2, 0.85);
    addText('bub-b', '◂ → forwarded to Claude1 · session.WriteAndSubmit', x + 30, chY + 44, 10, palette.text, '700');
    activeKeys.push('bub-b');
  }

  // Input box with the auto-typed transcript
  const inY = y + h - 32, inX = x + 50, inW = w - 174, inH = 22;
  rect(ui, inX, inY, inW, inH, palette.void, 1);
  strokeRect(ui, inX, inY, inW, inH, palette.line, 2, 0.92);
  addText('plus-btn', '+', x + 18, inY + 4, 13, palette.green, '800');
  const cursorOn = Math.floor(time / 400) % 2 === 0;
  addText('in-text', state.inputText + (cursorOn && !state.inputText ? '_' : ''),
    inX + 8, inY + 4, 11, palette.soft, '600');
  // Send button (auto-flashes when SendCurrentInput fires)
  const sendActive = state.step === 13;
  rect(ui, inX + inW + 8, inY, 110, inH, sendActive ? palette.cyan : palette.panel3, sendActive ? 0.92 : 0.7);
  strokeRect(ui, inX + inW + 8, inY, 110, inH, palette.line, 2, 0.85);
  addText('send-lbl', '▶ AUTO-SEND', inX + inW + 18, inY + 4, 10, sendActive ? palette.ink : palette.cyan, '800');
  activeKeys.push('plus-btn', 'in-text', 'send-lbl');
}

function stagesLit(idx) {
  // step ranges → which pipeline stage glows
  if (state.step < 4)  return idx === 0 && state.micActive;          // MIC warming up
  if (state.step < 8)  return idx === 1 && state.vadActive;          // VAD detected utterance
  if (state.step < 11) return idx === 2 && state.whisperBusy;        // Whisper transcribing
  if (state.step < 14) return idx === 3;                             // CHT mode forwarding
  if (state.step < 18) return idx === 4;                             // Terminal
  return false;
}

// ── Animation loop ────────────────────────────────────────
function pushTerm(text, color = palette.soft) {
  state.termLines.push({ text, color });
  if (state.termLines.length > 14) state.termLines.shift();
}
function reset() {
  state.termLines = [];
  state.inputText = '';
  state.micActive = false;
  state.vadActive = false;
  state.whisperBusy = false;
  state.whisperProgress = 0;
  state.voiceLevel = 0.04;
  state.typingActivity = 0;
}

function updateWaveform(dt) {
  const target = state.vadActive ? state.voiceLevel : (state.micActive ? 0.06 : 0.02);
  state.waveBars = state.waveBars.map(amp => {
    const drive = state.vadActive ? Math.random() * target * 1.6 : target * (0.4 + Math.random() * 0.6);
    return clamp(lerp(amp, drive, 0.3), 0.02, 1);
  });
}

const script = [
  // 0-1: idle + mic activation
  { dt: 1200, run: () => { reset(); pushTerm('$ claude', palette.cyan); pushTerm('Welcome to Claude. Ask me anything.', palette.dim); } },
  { dt: 600,  run: () => { state.micActive = true; state.typingActivity = 1; } },
  { dt: 400,  run: () => { /* warming up */ } },
  // 3-4: VAD picks up an utterance
  { dt: 600,  run: () => { state.vadActive = true; state.voiceLevel = 0.55; } },
  { dt: 800,  run: () => { state.voiceLevel = 0.78; } },
  { dt: 800,  run: () => { state.voiceLevel = 0.62; } },
  { dt: 600,  run: () => { state.vadActive = false; state.voiceLevel = 0.08; state.whisperBusy = true; } },
  // 7: Whisper inference (~600ms)
  { dt: 200,  run: () => { state.whisperProgress = 0.3; } },
  { dt: 200,  run: () => { state.whisperProgress = 0.7; } },
  { dt: 200,  run: () => { state.whisperProgress = 1.0; } },
  // 10: transcript appears in input box
  { dt: 200,  run: () => { state.whisperBusy = false; } },
  { dt: 100,  run: () => { state.inputText = utterances[state.utteranceIdx].ko; } },
  // 12: brief flash, then auto-send
  { dt: 600,  run: () => { /* hold so user reads it */ } },
  { dt: 200,  run: () => { /* SEND lit */ } },
  // 14: text appears in terminal (CHT mode → session.WriteAndSubmit)
  { dt: 200,  run: () => { pushTerm('> ' + state.inputText, palette.cyan); state.inputText = ''; } },
  // 15-17: Claude responds
  { dt: 800,  run: () => { pushTerm(utterances[state.utteranceIdx].response[0], palette.dim); } },
  { dt: 700,  run: () => {
    const r = utterances[state.utteranceIdx].response;
    if (r.length > 1) pushTerm(r[1], palette.soft);
  } },
  { dt: 700,  run: () => {
    const r = utterances[state.utteranceIdx].response;
    if (r.length > 2) pushTerm(r[2], palette.soft);
  } },
  { dt: 700,  run: () => {
    const r = utterances[state.utteranceIdx].response;
    if (r.length > 3) pushTerm(r[3], palette.soft);
    if (r.length > 4) pushTerm(r[4], r.length > 4 ? palette.green : palette.soft);
  } },
  // 19: hold then loop with next utterance
  { dt: 2200, run: () => { /* read time */ } },
  { dt: 200,  run: () => {
    state.utteranceIdx = (state.utteranceIdx + 1) % utterances.length;
    state.step = -1; // will increment back to 0
  } },
];

function tick(dt) {
  state.timer += dt;
  updateWaveform(dt);
  const step = script[clamp(state.step, 0, script.length - 1) % script.length];
  if (state.timer >= step.dt) {
    step.run();
    state.timer = 0;
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

  // Top: Claude terminal occupies most of vertical space
  const termY = 56;
  const termH = h - termY - 220;
  drawClaudeTerminal(12, termY, w - 24, termH, time, activeKeys);

  // Bottom: AgentBot CHT panel with voice toolbar
  drawAgentBotChtPanel(12, h - 208, w - 24, 196, time, activeKeys);

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
