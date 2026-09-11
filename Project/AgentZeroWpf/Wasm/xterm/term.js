// xterm.js glue for the AgentZero WebViewXterm terminal backend.
// Bridges the xterm.js renderer to the managed ConPTY host via the WebView2
// message channel (window.chrome.webview). Messages:
//   host → JS : { type: 'out', data }  write VT to the screen
//               { type: 'clear' }       clear the viewport
//               { type: 'focus' }       focus the terminal
//   JS → host : { type: 'ready', cols, rows }   renderer initialised
//               { type: 'in',    data }          user keystrokes / paste
//               { type: 'resize', cols, rows }   viewport reflowed
(function () {
  var term = new window.Terminal({
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    cursorBlink: true,
    allowProposedApi: true,
    // scrollback kept generous so screen-scrapers (approval parser, state
    // monitor) see enough history even though they read the host-side log.
    scrollback: 5000,
    theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
  });

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
  try { fit.fit(); } catch (e) {}

  var wv = window.chrome && window.chrome.webview;
  function post(o) { if (wv) { try { wv.postMessage(o); } catch (e) {} } }

  // User input → host stdin. xterm.js handles IME composition natively, so
  // Korean / CJK input arrives here as committed strings (no Win32InputMode
  // workaround needed).
  term.onData(function (d) { post({ type: 'in', data: d }); });

  function doFit() {
    try { fit.fit(); } catch (e) {}
    post({ type: 'resize', cols: term.cols, rows: term.rows });
  }
  if (window.ResizeObserver) { new ResizeObserver(doFit).observe(host); }
  window.addEventListener('resize', doFit);

  if (wv) {
    wv.addEventListener('message', function (e) {
      var m = e.data;
      if (!m || !m.type) return;
      if (m.type === 'out') { term.write(m.data); }
      else if (m.type === 'clear') { term.clear(); }
      else if (m.type === 'focus') { term.focus(); }
    });
  }

  // Signal readiness + initial size so the host can flush any buffered output
  // and size the pseudo-console to match.
  post({ type: 'ready', cols: term.cols, rows: term.rows });
})();
