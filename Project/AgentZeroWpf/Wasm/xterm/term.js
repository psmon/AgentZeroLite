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
