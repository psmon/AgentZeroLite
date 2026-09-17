// xterm.js glue for the AgentZero WebViewXterm terminal backend.
// Bridges the xterm.js renderer to the managed ConPTY host via the WebView2
// message channel (window.chrome.webview). Messages:
//   host → JS : { type: 'out', data }  write VT to the screen
//               { type: 'clear' }       clear the viewport
//               { type: 'focus' }       focus the terminal
//               { type: 'config', fontFamily, fontSize, lineHeight, theme }
//                                       appearance, owned by TerminalSettings in C#
//   JS → host : { type: 'ready',  cols, rows }  renderer initialised
//               { type: 'in',     data }        user keystrokes / paste
//               { type: 'resize', cols, rows }  viewport reflowed
//               { type: 'screen', data }        the visible viewport, debounced
//               { type: 'renderer', name, reason }  which renderer is live
//               { type: 'fontstatus', family, loaded, cellWidth }
//                                       did the configured face actually load
(function () {
  // Appearance is the host's to decide (TerminalSettings), but it arrives as a
  // message and the renderer must be usable before it does. These are the same
  // values the C# defaults carry, so the first frame never flashes a different
  // look on its way to the configured one.
  var term = new window.Terminal({
    fontFamily: 'JetBrains Mono, Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    lineHeight: 1.0,
    // Not forced on: DECSCUSR from the running program decides, so a TUI that
    // asks for a steady cursor gets one. Overridable from TerminalSettings.
    cursorBlink: false,
    allowProposedApi: true,
    // scrollback kept generous so screen-scrapers (approval parser, state
    // monitor) see enough history even though they read the host-side log.
    scrollback: 5000,
    theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
  });

  // Cell size is measured from the font, so a face that arrives after the first
  // measurement leaves every column in the wrong place. Applying config and
  // re-fitting is cheap; doing it twice is cheaper than getting it wrong once.
  function applyConfig(cfg) {
    if (!cfg) return;
    try {
      if (cfg.fontFamily) term.options.fontFamily = cfg.fontFamily;
      if (cfg.fontSize) term.options.fontSize = cfg.fontSize;
      if (cfg.lineHeight) term.options.lineHeight = cfg.lineHeight;
      if (cfg.theme) term.options.theme = cfg.theme;
      if (typeof cfg.cursorBlink === 'boolean') term.options.cursorBlink = cfg.cursorBlink;
    } catch (e) {}
    afterFonts(function () { doFit(); reportFont(); });
  }

  // A web font that fails to load is silent: the stack falls through and the
  // terminal keeps working in the fallback, looking almost right. Ask the
  // renderer whether the first family in the stack is really there, and report
  // the measured cell width so a mis-measured grid is visible too.
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

  // document.fonts.ready resolves once the @font-face files in index.html are in.
  // Without this the first fit measures the fallback and the grid is off until
  // something else forces a reflow.
  function afterFonts(fn) {
    try {
      if (document.fonts && document.fonts.ready) { document.fonts.ready.then(fn); return; }
    } catch (e) {}
    fn();
  }

  var fit = new window.FitAddon.FitAddon();
  term.loadAddon(fit);

  // Hyperlinks: Ctrl+click (or Cmd+click) on a URL asks the host to open it
  // in the user's default browser. The host validates the scheme (http/https
  // only) before shelling out. Plain click stays a focus/selection click so
  // OAuth screens don't pop a browser on every stray click.
  //   JS → host : { type: 'link', url }
  function openLink(ev, uri) {
    if (ev && !(ev.ctrlKey || ev.metaKey)) return;
    post({ type: 'link', url: uri });
  }
  try {
    // Bare URLs in output (OAuth / device-login links). The addon walks
    // wrapped rows, so a URL broken across several lines is one link.
    if (window.WebLinksAddon) {
      term.loadAddon(new window.WebLinksAddon.WebLinksAddon(openLink));
    }
    // OSC 8 explicit hyperlinks (gh, cargo, modern CLIs).
    term.options.linkHandler = { activate: openLink, allowNonHttpProtocols: false };
  } catch (e) {}

  var host = document.getElementById('term');
  term.open(host);

  // Renderer. xterm.js draws to the DOM by default, which is fine for a shell and
  // struggles with a TUI that repaints the whole screen continuously — a starfield
  // behind a prompt, say. Under that load the DOM renderer leaves the cursor at
  // stale positions between frames, which reads as a cursor skittering around the
  // screen. WebGL is the library's answer to exactly this, and what VS Code uses.
  //
  // Opened first, then the addon: it needs a live element. If WebGL is unavailable —
  // no hardware acceleration, a lost context, an old WebView2 — the addon throws or
  // fires onContextLoss, and the DOM renderer carries on. Either way the host is
  // told which one is live, so "is it the renderer?" is answerable from a log.
  var renderer = 'dom';
  try {
    if (window.WebglAddon) {
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
  // Reported with 'ready' below: post() reads `wv`, which is not assigned until
  // after this point, so anything sent here goes nowhere.
  try { fit.fit(); } catch (e) {}
  afterFonts(function () { doFit(); });

  var wv = window.chrome && window.chrome.webview;
  function post(o) { if (wv) { try { wv.postMessage(o); } catch (e) {} } }

  // User input → host stdin. xterm.js handles IME composition natively, so
  // Korean / CJK input arrives here as committed strings (no Win32InputMode
  // workaround needed).
  term.onData(function (d) { post({ type: 'in', data: d }); });

  // The host keeps the raw VT stream but has no emulator, so it cannot answer
  // "what is on the screen" - which is what the approval parser, the agent-state
  // monitor and the bot's context all actually ask for. We are the emulator, so we
  // tell it. Debounced because output arrives in bursts and only the settled screen
  // is interesting; 250 ms is well inside the state monitor's poll interval.
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
    if (screenTimer) return;                 // coalesce the burst, do not restart it
    screenTimer = setTimeout(postScreen, 250);
  }

  function doFit() {
    try { fit.fit(); } catch (e) {}
    post({ type: 'resize', cols: term.cols, rows: term.rows });
    scheduleScreen();                        // a reflow changes what is visible
  }
  if (window.ResizeObserver) { new ResizeObserver(doFit).observe(host); }
  window.addEventListener('resize', doFit);

  if (wv) {
    wv.addEventListener('message', function (e) {
      var m = e.data;
      if (!m || !m.type) return;
      if (m.type === 'out') { term.write(m.data, scheduleScreen); }
      else if (m.type === 'clear') { term.clear(); scheduleScreen(); }
      else if (m.type === 'focus') { term.focus(); }
      else if (m.type === 'config') { applyConfig(m); }
    });
  }

  // Signal readiness + initial size so the host can flush any buffered output
  // and size the pseudo-console to match.
  post({ type: 'renderer', name: renderer });
  post({ type: 'ready', cols: term.cols, rows: term.rows });
})();
