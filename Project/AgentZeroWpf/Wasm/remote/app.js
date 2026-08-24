// AgentZero Remote — web terminal client.
//
// Flow: ensure xterm is loaded → obtain a bearer token (stored, or paired via one-time
// PIN) → open a WebSocket (token carried as the WS subprotocol) → render raw ANSI from the
// server with xterm and forward keystrokes back. The server is the viewer's mirror of the
// live GUI terminal; input is shared with whoever sits at the desktop.

const TOKEN_KEY = "azr_token";

const els = {
  status: document.getElementById("status"),
  term: document.getElementById("term"),
  select: document.getElementById("termSelect"),
  toast: document.getElementById("toast"),
  pair: document.getElementById("pair"),
  pin: document.getElementById("pin"),
  pairBtn: document.getElementById("pairBtn"),
  pairErr: document.getElementById("pairErr"),
};

let term, fitAddon, ws;

function setStatus(text, cls) {
  els.status.textContent = text;
  els.status.className = "status" + (cls ? " " + cls : "");
}

function toast(msg) {
  els.toast.textContent = msg;
  els.toast.classList.add("show");
  clearTimeout(toast._t);
  toast._t = setTimeout(() => els.toast.classList.remove("show"), 2600);
}

// ── xterm loader (local vendor preferred, CDN fallback) ─────────────
function ensureXterm() {
  if (window.Terminal) return Promise.resolve();
  // Local vendor missing → pull from CDN (fine for a LAN tool; vendor locally for air-gapped).
  const CDN = "https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.js";
  const CSS = "https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.css";
  const FIT = "https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.js";
  const load = (src) => new Promise((res, rej) => {
    const s = document.createElement("script"); s.src = src; s.onload = res; s.onerror = rej;
    document.head.appendChild(s);
  });
  const link = document.createElement("link"); link.rel = "stylesheet"; link.href = CSS;
  document.head.appendChild(link);
  return load(CDN).then(() => load(FIT).catch(() => {}));
}

function initTerminal() {
  term = new Terminal({
    cursorBlink: true,
    fontFamily: "Cascadia Mono, Consolas, monospace",
    fontSize: 14,
    theme: { background: "#0c0c0c" },
    scrollback: 10000,
    convertEol: false,
  });
  try {
    const FitCtor = (window.FitAddon && window.FitAddon.FitAddon) || window.FitAddon;
    if (FitCtor) { fitAddon = new FitCtor(); term.loadAddon(fitAddon); }
  } catch (_) { /* fit addon optional */ }

  term.open(els.term);
  try { fitAddon && fitAddon.fit(); } catch (_) {}
  window.addEventListener("resize", () => { try { fitAddon && fitAddon.fit(); } catch (_) {} });

  // Keystrokes only reach onData when the xterm textarea is focused. Focus on load and
  // whenever the user clicks anywhere in the terminal area.
  term.focus();
  els.term.addEventListener("mousedown", () => term.focus());

  // Forward keystrokes only. When the hosted app enables mouse reporting (Claude Code,
  // vim, etc.) xterm would otherwise stream every mouse MOVE as an input escape sequence,
  // flooding the PTY and drowning out real typing. Remote control is keyboard-driven, so
  // drop mouse-report and focus-event sequences before sending.
  term.onData((d) => {
    if (isMouseOrFocusReport(d)) return;
    send({ t: "input", d });
  });
}

// Remote control is keyboard-only — drop every mouse-report encoding and focus event so
// nothing mouse-related ever reaches the PTY. Keyboard sequences (arrows ESC[A-D, F-keys
// ESC[..~, etc.) never end in M/m, so this can't swallow real input.
function isMouseOrFocusReport(d) {
  if (d === "\x1b[I" || d === "\x1b[O") return true;   // focus in/out (DECSET 1004)
  if (d.startsWith("\x1b[M")) return true;              // X10 mouse: ESC [ M + 3 bytes
  if (/^\x1b\[<[0-9;]*[Mm]$/.test(d)) return true;      // SGR (1006) / SGR-pixels (1016)
  if (/^\x1b\[[0-9;]+[Mm]$/.test(d)) return true;       // URXVT (1015)
  return false;
}

// ── auth ────────────────────────────────────────────────────────────
function getToken() { return localStorage.getItem(TOKEN_KEY); }
function setToken(t) { localStorage.setItem(TOKEN_KEY, t); }
function clearToken() { localStorage.removeItem(TOKEN_KEY); }

function showPair(show) { els.pair.classList.toggle("show", show); if (show) els.pin.focus(); }

async function pair() {
  const pin = els.pin.value.trim();
  els.pairErr.textContent = "";
  if (!pin) { els.pairErr.textContent = "코드를 입력하세요."; return; }
  try {
    const resp = await fetch("/api/pair", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin }),
    });
    const data = await resp.json();
    if (resp.ok && data.ok && data.token) {
      setToken(data.token);
      els.pin.value = "";
      showPair(false);
      connect();
    } else {
      els.pairErr.textContent =
        data.error === "LockedOut" ? "시도 초과 — 잠시 후 다시." :
        data.error === "Expired" ? "코드가 만료됨. 새 코드 발급 필요." :
        "코드가 올바르지 않습니다.";
    }
  } catch (e) {
    els.pairErr.textContent = "서버에 연결할 수 없습니다.";
  }
}

// ── websocket ───────────────────────────────────────────────────────
function connect() {
  const token = getToken();
  if (!token) { showPair(true); return; }

  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  setStatus("연결 중…");
  try {
    ws = new WebSocket(`${proto}//${location.host}/ws`, [token]);
  } catch (_) {
    setStatus("연결 실패", "bad"); return;
  }

  ws.onopen = () => { setStatus("연결됨", "ok"); try { term.focus(); } catch (_) {} };
  ws.onclose = (ev) => {
    setStatus("연결 종료", "bad");
    // 1006 / 401-ish: token likely rejected → re-pair.
    if (ev.code === 1006 || ev.code === 1008) {
      clearToken();
      showPair(true);
    }
  };
  ws.onerror = () => setStatus("연결 오류", "bad");
  ws.onmessage = (ev) => handleFrame(ev.data);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function handleFrame(raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch (_) { return; }
  switch (msg.t) {
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

let _lastTermSig = "";
function populateTerminals(list) {
  if (!list || !Array.isArray(list.groups)) return;

  const items = [];
  let activeValue = null;
  for (const g of list.groups) {
    for (const tab of g.tabs || []) {
      const value = `${g.group_index}:${tab.tab_index}`;
      if (tab.active) activeValue = value;
      items.push({
        value,
        label: `${g.group_name} / ${tab.title}${tab.active ? " ●" : ""}`,
      });
    }
  }

  // Diff against the last render so the periodic refresh doesn't reset the user's selection.
  const sig = items.map((i) => i.value + "=" + i.label).join("|");
  if (sig === _lastTermSig) return;
  _lastTermSig = sig;

  const prev = els.select.value;
  els.select.innerHTML = "";
  for (const it of items) {
    const opt = document.createElement("option");
    opt.value = it.value;
    opt.textContent = it.label;
    els.select.appendChild(opt);
  }
  // Until the user picks a terminal, mirror the GUI's active one (which is what the server
  // auto-attached us to) so the dropdown reflects reality.
  if (!_userPicked && activeValue && items.some((i) => i.value === activeValue)) {
    els.select.value = activeValue;
  } else if (items.some((i) => i.value === prev)) {
    els.select.value = prev;
  }
}

let _userPicked = false;
els.select.addEventListener("change", () => {
  _userPicked = true;
  const [group, tab] = els.select.value.split(":").map(Number);
  if (Number.isInteger(group) && Number.isInteger(tab)) send({ t: "attach", group, tab });
});

els.pairBtn.addEventListener("click", pair);
els.pin.addEventListener("keydown", (e) => { if (e.key === "Enter") pair(); });

// ── boot ────────────────────────────────────────────────────────────
ensureXterm()
  .then(() => {
    initTerminal();
    connect();
  })
  .catch(() => setStatus("xterm 로드 실패", "bad"));
