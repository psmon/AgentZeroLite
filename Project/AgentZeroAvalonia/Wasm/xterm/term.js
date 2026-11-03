// xterm.js glue for the AgentZero Avalonia terminal (M0035) — a copy of the WPF
// host's term.js with the transport swapped for NativeWebView's:
//   JS → host : one JSON string through invokeCSharpAction (WebMessageReceived),
//               falling back to chrome.webview.postMessage where that is what exists.
//   host → JS : window.zeroHost.recv(message), called through InvokeScript.
// Messages:
//   host → JS : { type: 'out', data }     write VT text to the screen
//               { type: 'out64', data }   write VT bytes (base64 of UTF-8, string or array of chunks) — the hot path
//               { type: 'clear' }         clear the viewport
//               { type: 'focus' }         focus the terminal
//               { type: 'config', fontFamily, fontSize, lineHeight, cursorBlink, theme, hotkeys }
//               { type: 'find', query? }   open the find bar (seeded from the selection, or query)
//                                         appearance, owned by TerminalSettings in C#
//   JS → host : { type: 'ready',  cols, rows }  renderer initialised
//               { type: 'in',     data }        user keystrokes / paste
//               { type: 'resize', cols, rows }  viewport reflowed
//               { type: 'screen', data }        the visible viewport, debounced
//               { type: 'title', data }         the terminal title the program set (OSC 0/2)
//               { type: 'renderer', name, reason }  which renderer is live
//               { type: 'fontstatus', family, loaded, cellWidth }
//               { type: 'activate' }            the user clicked in here
//               { type: 'link', url }           Ctrl/Cmd+click on a URL
//               { type: 'hotkey', name }        a configured chord was pressed
//               { type: 'notify', osc, data }   OSC 9 / 777 / 99 notification request (raw)
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

  // Korean (and any composing) IME on macOS: WKWebView drops the composition once the
  // focused helper textarea has a non-zero box, and xterm.js gives it one (the cell size)
  // in _syncTextArea after every write — i.e. after every echoed keystroke. Measured: with
  // the shell echoing, 2-Set Korean committed bare jamo ("ㅎ", "ㄱ") and dropped the vowels;
  // keeping left/top and zeroing the size composes "한글". WebView2 (Windows) is unaffected,
  // and the composition itself still sizes the textarea while it runs.
  // Same engine, second trap: xterm.css gives the textarea `white-space: nowrap`, so WebKit
  // turns a space the IME commits ("글 ") into U+00A0 to keep it — a shell then reads
  // `git commit -m 한글 메시지` as one word. `pre` keeps it a plain space (measured: a0 → 20).
  try {
    var core = term._core;
    var isMacWebKit = /Mac/.test(navigator.platform || '') && !/Chrome\//.test(navigator.userAgent || '');
    if (isMacWebKit && core && core.textarea) core.textarea.style.whiteSpace = 'pre';
    if (isMacWebKit && core && typeof core._syncTextArea === 'function') {
      var syncTextArea = core._syncTextArea.bind(core);
      core._syncTextArea = function () {
        syncTextArea();
        var ta = core.textarea;
        if (ta && !(core._compositionHelper && core._compositionHelper.isComposing)) {
          ta.style.width = '0';
          ta.style.height = '0';
        }
      };
    }
  } catch (e) {}

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

  // ── find (Ctrl+Shift+F → host → { type: 'find' }) ──────────────────────────
  // The chord travels through the host's hotkey table like every other one, so it can be
  // rebound in Settings → Shortcuts and the palette can open the bar too. Search runs over
  // the whole buffer (scrollback included); matches are decorations, so allowProposedApi
  // above is what makes the highlights and the overview-ruler marks appear.
  var search = null;
  try {
    if (window.SearchAddon) {
      search = new window.SearchAddon.SearchAddon({ highlightLimit: 2000 });
      term.loadAddon(search);
    }
  } catch (e) { search = null; }

  var findBar = document.getElementById('find');
  var findInput = document.getElementById('find-q');
  var findCount = document.getElementById('find-count');
  var findCase = document.getElementById('find-case');
  var findRegex = document.getElementById('find-regex');
  var findState = { caseSensitive: false, regex: false };

  function findOptions(incremental) {
    return {
      caseSensitive: findState.caseSensitive,
      regex: findState.regex,
      incremental: !!incremental,
      decorations: {
        matchBackground: '#623315',
        matchBorder: '#623315',
        matchOverviewRuler: '#d18616',
        activeMatchBackground: '#a8ac94',
        activeMatchBorder: '#e0e0e0',
        activeMatchColorOverviewRuler: '#ffffff'
      }
    };
  }

  function setCount(text, none) {
    findCount.textContent = text;
    findInput.classList.toggle('none', !!none);
  }

  function runFind(direction, incremental) {
    if (!search) { setCount('unavailable', true); return; }
    var q = findInput.value;
    if (!q) { try { search.clearDecorations(); } catch (e) {} setCount('', false); return; }
    var found = false;
    try {
      found = direction < 0 ? search.findPrevious(q, findOptions(false)) : search.findNext(q, findOptions(incremental));
    } catch (e) {
      // An unfinished regular expression while typing — not an error worth more than a hint.
      setCount('bad regex', true);
      return;
    }
    if (!found) setCount('No results', true);
  }

  if (search && search.onDidChangeResults) {
    search.onDidChangeResults(function (r) {
      if (!r || !findInput.value) return;
      if (r.resultCount === 0) { setCount('No results', true); return; }
      // resultIndex is -1 past the highlight limit: the count is still right, the position is not known.
      var pos = r.resultIndex >= 0 ? (r.resultIndex + 1) : '?';
      setCount(pos + ' of ' + r.resultCount + (r.resultCount >= 2000 ? '+' : ''), false);
    });
  }

  function openFind(query) {
    findBar.hidden = false;
    if (typeof query === 'string') findInput.value = query;
    else {
      // Seed from the selection, as editors do; a multi-line selection is not a query.
      var sel = term.getSelection();
      if (sel && sel.indexOf(NL_FIND) < 0 && sel.length <= 200) findInput.value = sel;
    }
    findInput.focus();
    findInput.select();
    if (findInput.value) runFind(1, true);
  }

  function closeFind() {
    findBar.hidden = true;
    try { search && search.clearDecorations(); } catch (e) {}
    term.focus();
  }

  var NL_FIND = String.fromCharCode(10);
  findInput.addEventListener('input', function () { runFind(1, true); });
  findInput.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') { runFind(e.shiftKey ? -1 : 1, false); e.preventDefault(); }
    else if (e.key === 'Escape') { closeFind(); e.preventDefault(); }
    else if ((e.key === 'f' || e.key === 'F') && (e.ctrlKey || e.metaKey)) { findInput.select(); e.preventDefault(); }
    else if (e.key === 'F3') { runFind(e.shiftKey ? -1 : 1, false); e.preventDefault(); }
  });
  function toggle(btn, key) {
    btn.addEventListener('click', function () {
      findState[key] = !findState[key];
      btn.classList.toggle('on', findState[key]);
      findInput.focus();
      runFind(1, true);
    });
  }
  toggle(findCase, 'caseSensitive');
  toggle(findRegex, 'regex');
  document.getElementById('find-next').addEventListener('click', function () { runFind(1, false); findInput.focus(); });
  document.getElementById('find-prev').addEventListener('click', function () { runFind(-1, false); findInput.focus(); });
  document.getElementById('find-close').addEventListener('click', closeFind);

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
    if (m.type === 'out64') {
      // One batch = an array of base64 chunks (or a single string). Queue them all
      // before yielding so xterm.js renders the batch as one frame.
      var parts = Array.isArray(m.data) ? m.data : [m.data];
      for (var i = 0; i < parts.length; i++) {
        term.write(b64ToBytes(parts[i]), i === parts.length - 1 ? scheduleScreen : undefined);
      }
    }
    else if (m.type === 'out') { term.write(m.data, scheduleScreen); }
    else if (m.type === 'clear') { term.clear(); scheduleScreen(); }
    else if (m.type === 'focus') { term.focus(); }
    else if (m.type === 'config') { applyConfig(m); }
    else if (m.type === 'find') { openFind(m.query); }
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
  // The title the program sets (OSC 0/2). Agent CLIs animate it while busy (Claude Code
  // puts a braille spinner there), the strongest "working" signal agent-state detection has.
  try { term.onTitleChange(function (t) { post({ type: 'title', data: t || '' }); }); } catch (e) {}

  // Desktop notifications a program asks for: OSC 9 (iTerm2), OSC 777 (notify;title;body),
  // OSC 99 (kitty). Passed raw — the host parses (ZeroCommon TerminalNotificationParser), so
  // ConEmu's numbered OSC 9 commands such as the 9;4 progress bar are told apart in one
  // tested place. Returning true marks the sequence handled; nothing is drawn either way.
  try {
    [9, 777, 99].forEach(function (code) {
      term.parser.registerOscHandler(code, function (data) {
        post({ type: 'notify', osc: code, data: String(data || '') });
        return true;
      });
    });
  } catch (e) {}

  post({ type: 'renderer', name: renderer });
  post({ type: 'ready', cols: term.cols, rows: term.rows });
})();
