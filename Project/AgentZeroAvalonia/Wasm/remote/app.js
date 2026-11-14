// AgentZero Remote — the browser side of the Remote page (Avalonia host).
//
// Pair once with a one-time code (POST /api/pair → a key kept in this browser), then open a
// WebSocket with the key as its subprotocol. The server sends the terminal's size, its current
// screen and then its output; keystrokes go back as they are typed. Wire format: the shared
// RemoteProtocol ({"t":"snapshot|output|info|error|terminals"}) plus this host's
// {"t":"attached"} and {"t":"size"}.

"use strict";

const TOKEN_KEY = "azr_token";
const $ = (id) => document.getElementById(id);
const els = {
  status: $("status"), select: $("termSelect"), wrap: $("termWrap"), term: $("term"),
  keys: $("keys"), compose: $("compose"), line: $("line"), toast: $("toast"),
  pair: $("pair"), pin: $("pin"), pairBtn: $("pairBtn"), pairErr: $("pairErr"),
};

let term, fit, ws;
let ptySize = null;        // {cols, rows} of the PC's terminal, when the server knows it
let attached = null;       // "group:tab"
let retry = 0, retryTimer = null;

function setStatus(text, cls) {
  els.status.textContent = text;
  els.status.className = "status" + (cls ? " " + cls : "");
}

function toast(msg) {
  els.toast.textContent = msg;
  els.toast.classList.add("show");
  clearTimeout(toast.t);
  toast.t = setTimeout(() => els.toast.classList.remove("show"), 3200);
}

// ── terminal ────────────────────────────────────────────────────────

function initTerminal() {
  term = new Terminal({
    cursorBlink: true,
    fontFamily: "'JetBrains Mono', 'Cascadia Mono', Consolas, Menlo, monospace",
    fontSize: 14,
    theme: { background: "#0c0c0c" },
    scrollback: 5000,
  });
  const FitCtor = window.FitAddon && (window.FitAddon.FitAddon || window.FitAddon);
  if (FitCtor) { fit = new FitCtor(); term.loadAddon(fit); }
  term.open(els.term);
  applySize();
  window.addEventListener("resize", applySize);

  els.term.addEventListener("mousedown", () => term.focus());
  // Keyboard only: when the program turns on mouse reporting (Claude Code, vim) every pointer
  // move would otherwise be typed into the terminal.
  term.onData((d) => { if (!isMouseOrFocusReport(d)) send({ t: "input", d }); });
}

function isMouseOrFocusReport(d) {
  if (d === "\x1b[I" || d === "\x1b[O") return true;
  if (d.startsWith("\x1b[M")) return true;
  if (/^\x1b\[<[0-9;]*[Mm]$/.test(d)) return true;
  if (/^\x1b\[[0-9;]+[Mm]$/.test(d)) return true;
  return false;
}

// Draw at the PC's columns and rows — an agent's screen is laid out for that width, and
// rewrapping it to a phone's width scrambles it. The font shrinks to fit the width (down to a
// readable minimum; past that the area scrolls sideways).
function applySize() {
  if (!term) return;
  if (!ptySize) { try { fit && fit.fit(); } catch (_) {} return; }
  const width = els.wrap.clientWidth - 12;
  const size = Math.max(8, Math.min(15, Math.floor(width / (ptySize.cols * 0.6))));
  if (term.options.fontSize !== size) term.options.fontSize = size;
  if (term.cols !== ptySize.cols || term.rows !== ptySize.rows) term.resize(ptySize.cols, ptySize.rows);
}

// ── keys bar + compose line (phones have no Esc, Tab, Ctrl or arrows; IMEs prefer a field) ──

els.keys.addEventListener("pointerdown", (e) => { if (e.target.closest("button")) e.preventDefault(); });
els.keys.addEventListener("click", (e) => {
  const b = e.target.closest("button[data-key]");
  if (b) send({ t: "key", d: b.dataset.key });
});

els.compose.addEventListener("submit", (e) => {
  e.preventDefault();
  const text = els.line.value;
  if (!text) { send({ t: "key", d: "cr" }); return; }
  send({ t: "input", d: text });
  // A separate, slightly later Enter: an agent's TUI reads text and Enter arriving together
  // as a paste and does not submit it.
  setTimeout(() => send({ t: "key", d: "cr" }), 150);
  els.line.value = "";
});

// ── pairing ─────────────────────────────────────────────────────────

const getToken = () => { try { return localStorage.getItem(TOKEN_KEY); } catch (_) { return null; } };
const setToken = (t) => { try { localStorage.setItem(TOKEN_KEY, t); } catch (_) {} };
const clearToken = () => { try { localStorage.removeItem(TOKEN_KEY); } catch (_) {} };

function showPair(show) {
  els.pair.classList.toggle("show", show);
  if (show) { setStatus("not paired"); setTimeout(() => els.pin.focus(), 50); }
}

async function pair() {
  const pin = els.pin.value.trim();
  els.pairErr.textContent = "";
  if (!/^\d{8}$/.test(pin)) { els.pairErr.textContent = "The code is 8 digits."; return; }
  els.pairBtn.disabled = true;
  try {
    const resp = await fetch("/api/pair", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin }),
    });
    const data = await resp.json().catch(() => ({}));
    if (resp.ok && data.ok && data.token) {
      setToken(data.token);
      els.pin.value = "";
      showPair(false);
      connect();
    } else {
      els.pairErr.textContent =
        data.error === "LockedOut" ? "Too many tries — wait five minutes, then issue a new code." :
        data.error === "Expired" ? "That code expired. Issue a new one in AgentZero." :
        data.error === "NoActivePin" ? "No code is active. Issue one in AgentZero." :
        "That code is not right.";
    }
  } catch (_) {
    els.pairErr.textContent = "Cannot reach AgentZero.";
  } finally {
    els.pairBtn.disabled = false;
  }
}
els.pairBtn.addEventListener("click", pair);
els.pin.addEventListener("keydown", (e) => { if (e.key === "Enter") pair(); });

// ── connection ──────────────────────────────────────────────────────

function connect() {
  clearTimeout(retryTimer);
  const token = getToken();
  if (!token) { showPair(true); return; }

  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  const query = attached ? "?group=" + attached.split(":")[0] + "&tab=" + attached.split(":")[1] : "";
  setStatus("connecting…");
  try { ws = new WebSocket(proto + "//" + location.host + "/ws" + query, [token]); }
  catch (_) { scheduleRetry(); return; }

  ws.onopen = () => { retry = 0; setStatus("connected", "ok"); try { term.focus(); } catch (_) {} };
  ws.onmessage = (ev) => handleFrame(ev.data);
  ws.onclose = () => { ws = null; afterClose(); };
}

// A refused handshake and a dropped connection look alike here; ask the server which it was.
async function afterClose() {
  const token = getToken();
  if (!token) { showPair(true); return; }
  try {
    const r = await fetch("/api/session", { headers: { Authorization: "Bearer " + token }, cache: "no-store" });
    if (r.status === 401) { clearToken(); showPair(true); toast("This browser's key was revoked — pair again."); return; }
  } catch (_) { /* server unreachable — retry below */ }
  scheduleRetry();
}

function scheduleRetry() {
  const wait = Math.min(10000, 1000 * Math.pow(2, retry++));
  setStatus("disconnected — retrying in " + Math.round(wait / 1000) + "s", "bad");
  retryTimer = setTimeout(connect, wait);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function handleFrame(raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch (_) { return; }
  switch (msg.t) {
    case "attached":
      attached = msg.group + ":" + msg.tab;
      ptySize = msg.cols ? { cols: msg.cols, rows: msg.rows } : null;
      applySize();
      if ([...els.select.options].some((o) => o.value === attached)) els.select.value = attached;
      break;
    case "size":
      ptySize = { cols: msg.cols, rows: msg.rows };
      applySize();
      break;
    case "snapshot":
      term.reset();
      term.write(msg.d || "");
      try { term.focus(); } catch (_) {}
      break;
    case "output":
      term.write(msg.d || "");
      break;
    case "terminals":
      populateTerminals(msg.list);
      break;
    case "info":
      toast(msg.d || "");
      break;
    case "error":
      toast("⚠ " + (msg.d || "error"));
      break;
  }
}

// ── terminal picker ─────────────────────────────────────────────────

let lastSig = "";
function populateTerminals(list) {
  if (!list || !Array.isArray(list.groups)) return;
  const items = [];
  for (const g of list.groups)
    for (const tab of g.tabs || [])
      items.push({
        value: g.group_index + ":" + tab.tab_index,
        label: g.group_name + " / " + tab.title + (tab.running ? "" : " (not started)"),
        running: tab.running,
      });

  const sig = items.map((i) => i.value + "=" + i.label).join("|");
  if (sig === lastSig) return;
  lastSig = sig;

  els.select.innerHTML = "";
  for (const it of items) {
    const opt = document.createElement("option");
    opt.value = it.value;
    opt.textContent = it.label;
    opt.disabled = !it.running;
    els.select.appendChild(opt);
  }
  if (attached && items.some((i) => i.value === attached)) els.select.value = attached;
}

els.select.addEventListener("change", () => {
  const [group, tab] = els.select.value.split(":").map(Number);
  if (Number.isInteger(group) && Number.isInteger(tab)) send({ t: "attach", group, tab });
});

// ── boot ────────────────────────────────────────────────────────────

if (!window.Terminal) {
  setStatus("terminal renderer failed to load", "bad");
} else {
  initTerminal();
  connect();
}
