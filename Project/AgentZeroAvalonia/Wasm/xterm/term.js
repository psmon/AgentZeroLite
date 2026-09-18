// xterm.js glue for the AgentZero Avalonia terminal (M0035) — a copy of the WPF
// host's term.js with the transport swapped for NativeWebView's:
//   JS → host : one JSON string through invokeCSharpAction (WebMessageReceived),
//               falling back to chrome.webview.postMessage where that is what exists.
//   host → JS : window.zeroHost.recv(message), called through InvokeScript.
// Messages:
//   host → JS : { type: 'out', data }     write VT text to the screen
//               { type: 'out64', data }   write VT bytes (base64 of UTF-8) — the hot path
//               { type: 'clear' }         clear the viewport
//               { type: 'focus' }         focus the terminal
//               { type: 'config', fontFamily, fontSize, lineHeight, cursorBlink, theme, hotkeys }
//                                         appearance, owned by TerminalSettings in C#
//   JS → host : { type: 'ready',  cols, rows }  renderer initialised
//               { type: 'in',     data }        user keystrokes / paste
//               { type: 'resize', cols, rows }  viewport reflowed
//               { type: 'screen', data }        the visible viewport, debounced
//               { type: 'renderer', name, reason }  which renderer is live
//               { type: 'fontstatus', family, loaded, cellWidth }
//               { type: 'activate' }            the user clicked in here
//               { type: 'link', url }           Ctrl/Cmd+click on a URL
//               { type: 'hotkey', name }        a configured chord was pressed
(function () {
  // Appearance is the host's to decide (TerminalSettings), but it arrives as a
  // message and the renderer must be usable before it does. These are the same
  // values the C# defaults carry, so the first frame never flashes a different
  // look on its way to the configured one.
  var term = new window.Terminal({
    fontFamily: 'JetBrains Mono, Cascadia Mono, Consolas, Menlo, monospace',
    fontSize: 14,
    lineHeight: 1.0,
    cursorBlink: false,
    allowProposedApi: true,
    scrollback: 5000,
    theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
  });

  // ── transport ─────────────────────────────────────────────────────────────
  // The host-side function is injected by the WebView adapter; on some engines it
  // is not there yet when this script runs, so messages queue until it appears.
  var queue = [];
  function transport() {
    if (typeof window.invokeCSharpAction === 'function') return function (s) { window.invokeCSharpAction(s); };
    if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
      return function (s) { window.chrome.webview.postMessage(s); };
    if (window.webkit && window.webkit.messageHandlers) {
      var hs = window.webkit.messageHandlers;
      var h = hs.invokeCSharpAction || hs.avaloniaWebView || hs.webview;
      if (!h) { var keys = Object.keys(hs); if (keys.length) h = hs[keys[0]]; }
      if (h && h.postMessage) return function (s) { h.postMessage(s); };
    }
    return null;
  }
  function post(o) {
    var s = JSON.stringify(o);
    var t = transport();
    if (!t) { queue.push(s); return; }
    try { t(s); } catch (e) { queue.push(s); }
  }
  function drain() {
    var t = transport();
    if (!t) { setTimeout(drain, 50); return; }
    while (queue.length) { try { t(queue.shift()); } catch (e) { break; } }
  }
  setTimeout(drain, 0);

  // ── appearance ────────────────────────────────────────────────────────────
  var hotkeys = [];
  function applyConfig(cfg) {
    if (!cfg) return;
    try {
      if (cfg.fontFamily) term.options.fontFamily = cfg.fontFamily;
      if (cfg.fontSize) term.options.fontSize = cfg.fontSize;
      if (cfg.lineHeight) term.options.lineHeight = cfg.lineHeight;
      if (cfg.theme) term.options.theme = cfg.theme;
      if (typeof cfg.cursorBlink === 'boolean') term.options.cursorBlink = cfg.cursorBlink;
      if (Array.isArray(cfg.hotkeys)) hotkeys = cfg.hotkeys;
    } catch (e) {}
    afterFonts(function () { doFit(); reportFont(); });
  }

  function reportFont() {
    try {
      var first = String(term.options.fontFamily || '').split(',')[0].trim().replace(/^["']|["']$/g, '');
      var spec = term.options.fontSize + 'px "' + first + '"';
      post({
        type: 'fontstatus',
        family: first,
        loaded: !!(document.fonts && document.fonts.check(spec)),
        cellWidth: (term._core && term._core._renderService && term._core._renderService.dimensions
          && term._core._renderService.dimensions.css
          && term._core._renderService.dimensions.css.cell
          ? Math.round(term._core._renderService.dimensions.css.cell.width * 100) / 100
          : 0)
      });
    } catch (e) {}
  }

  function afterFonts(fn) {
    try {
      if (document.fonts && document.fonts.ready) { document.fonts.ready.then(fn); return; }
    } catch (e) {}
    fn();
  }

  var fit = new window.FitAddon.FitAddon();
  term.loadAddon(fit);

  // Hyperlinks: Ctrl+click (Cmd+click on macOS) asks the host to open the URL in the
  // default browser; the host validates the scheme. Plain click stays a focus click.
  function openLink(ev, uri) {
    if (ev && !(ev.ctrlKey || ev.metaKey)) return;
    post({ type: 'link', url: uri });
  }
  try {
    if (window.WebLinksAddon) {
      term.loadAddon(new window.WebLinksAddon.WebLinksAddon(openLink));
    }
    term.options.linkHandler = { activate: openLink, allowNonHttpProtocols: false };
  } catch (e) {}

  // Hotkeys. While the renderer has focus no Avalonia KeyBinding sees the keys, so
  // the chords the host configured are matched here and reported by name. Matched
  // chords are swallowed; everything else goes to the shell as usual.
  function matchHotkey(e) {
    for (var i = 0; i < hotkeys.length; i++) {
      var h = hotkeys[i];
      if (!h || !h.key) continue;
      if (!!h.ctrl !== e.ctrlKey || !!h.alt !== e.altKey || !!h.shift !== e.shiftKey || !!h.meta !== e.metaKey) continue;
      var k = String(h.key).toLowerCase();
      var ek = String(e.key || '').toLowerCase();
      var ec = String(e.code || '').toLowerCase();
      if (ek === k || ec === k || ec === 'key' + k || ec === 'arrow' + k || ('arrow' + ek) === k) return h.name;
    }
    return null;
  }
  term.attachCustomKeyEventHandler(function (e) {
    if (e.type !== 'keydown') return true;
    var name = matchHotkey(e);
    if (!name) return true;
    post({ type: 'hotkey', name: name });
    try { e.preventDefault(); } catch (x) {}
    return false;
  });

  var host = document.getElementById('term');
  term.open(host);

  // Renderer: DOM unless the host asked for WebGL (see the WPF copy for the why).
  var renderer = 'dom';
  try {
    if (window.__azUseWebgl && window.WebglAddon) {
      var webgl = new window.WebglAddon.WebglAddon();
      webgl.onContextLoss(function () {
        try { webgl.dispose(); } catch (e) {}
        post({ type: 'renderer', name: 'dom', reason: 'webgl context lost' });
      });
      term.loadAddon(webgl);
      renderer = 'webgl';
    }
  } catch (e) {
    renderer = 'dom';
  }
  try { fit.fit(); } catch (e) {}
  afterFonts(function () { doFit(); });

  // User input → host stdin. xterm.js handles IME composition natively, so
  // Korean / CJK input arrives here as committed strings.
  term.onData(function (d) { post({ type: 'in', data: d }); });

  // Clicking the terminal has to select its tab; the click never reaches Avalonia.
  window.addEventListener('mousedown', function () {
    post({ type: 'activate' });
  }, true);

  // The host keeps the raw VT stream but has no emulator; we are the emulator, so
  // we tell it what is on the screen, debounced (see the WPF copy).
  var NL = String.fromCharCode(10);
  var screenTimer = null;
  function postScreen() {
    screenTimer = null;
    try {
      var buf = term.buffer.active;
      var rows = [];
      for (var y = 0; y < term.rows; y++) {
        var line = buf.getLine(buf.viewportY + y);
        rows.push(line ? line.translateToString(true) : '');
      }
      while (rows.length && rows[rows.length - 1] === '') rows.pop();
      post({ type: 'screen', data: rows.join(NL) });
    } catch (e) {}
  }
  function scheduleScreen() {
    if (screenTimer) return;
    screenTimer = setTimeout(postScreen, 250);
  }

  function doFit() {
    try { fit.fit(); } catch (e) {}
    post({ type: 'resize', cols: term.cols, rows: term.rows });
    scheduleScreen();
  }
  if (window.ResizeObserver) { new ResizeObserver(doFit).observe(host); }
  window.addEventListener('resize', doFit);

  // ── host → JS ─────────────────────────────────────────────────────────────
  function b64ToBytes(s) {
    var bin = atob(s);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  function recv(m) {
    if (!m || !m.type) return;
    if (m.type === 'out64') { term.write(b64ToBytes(m.data), scheduleScreen); }
    else if (m.type === 'out') { term.write(m.data, scheduleScreen); }
    else if (m.type === 'clear') { term.clear(); scheduleScreen(); }
    else if (m.type === 'focus') { term.focus(); }
    else if (m.type === 'config') { applyConfig(m); }
  }
  window.zeroHost = { recv: recv };
  // The WPF-style channel, should an engine deliver messages that way.
  try {
    if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
      window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (typeof d === 'string') { try { d = JSON.parse(d); } catch (x) { return; } }
        recv(d);
      });
    }
  } catch (e) {}

  // Signal readiness + initial size so the host can flush any buffered output
  // and size the pseudo-console to match.
  post({ type: 'renderer', name: renderer });
  post({ type: 'ready', cols: term.cols, rows: term.rows });
})();
