// chat-test.js — TextLLM chatbot test for the WebDev sandbox.
// Single persistent native session via window.zero.chat.*; supports both
// single-shot send and token streaming.

(function () {
  const $ = (id) => document.getElementById(id);
  const log = (line) => {
    const pre = $('log');
    const ts = new Date().toLocaleTimeString('en-GB', { hour12: false });
    pre.textContent = `[${ts}] ${line}\n` + pre.textContent;
  };

  const setStatus = (kind, msg) => {
    const s = $('status');
    s.className = 'status ' + kind;
    s.textContent = msg;
  };

  const setStatusBar = (kind, msg) => {
    const b = $('statusBar');
    b.className = 'hint mono ' + kind;
    b.textContent = msg;
  };

  let turn = 0;

  function appendBubble(who, text, opts = {}) {
    const el = document.createElement('div');
    el.className = 'bubble ' + (who === 'You' ? 'user' : 'ai') + (opts.err ? ' err' : '');
    if (opts.streaming) el.classList.add('streaming');
    const head = document.createElement('span');
    head.className = 'who';
    head.textContent = who;
    el.appendChild(head);
    const body = document.createElement('span');
    body.className = 'body';
    body.textContent = text;
    el.appendChild(body);
    $('thread').appendChild(el);
    el.scrollIntoView({ block: 'end' });
    return body;
  }

  function bumpTurn(n) {
    if (typeof n === 'number') turn = n; else turn += 1;
    $('turn').textContent = String(turn);
  }

  async function refreshMeta() {
    if (!window.zero) {
      log('window.zero unavailable — bridge not loaded');
      setStatusBar('err', 'bridge not loaded');
      return;
    }
    try {
      const v = await window.zero.version();
      $('ver').textContent = v.version || '?';
    } catch (e) { $('ver').textContent = 'err'; log('version failed: ' + e.message); }

    try {
      const s = await window.zero.chat.status();
      $('backend').textContent = s.backend || '?';
      $('model').textContent = s.model || '?';
      const kind = s.available ? 'ok' : 'err';
      setStatusBar(kind, s.detail || (s.available ? 'ready' : 'not ready'));
      $('sendBtn').disabled = !s.available;
      log(`status: backend=${s.backend} model=${s.model} avail=${s.available}`);
    } catch (e) {
      $('backend').textContent = $('model').textContent = 'err';
      setStatusBar('err', 'status failed: ' + e.message);
      log('status failed: ' + e.message);
    }
  }

  async function sendOnce(text) {
    setStatus('busy', 'thinking…');
    log(`chat.send ${text.length} chars`);
    try {
      const r = await window.zero.chat.send(text);
      if (r && r.ok) {
        appendBubble('AI', r.reply || '');
        if (typeof r.turn === 'number') bumpTurn(r.turn);
        setStatus('ok', `turn ${r.turn ?? '?'} · ${(r.reply || '').length} chars`);
        log(`chat ok: ${(r.reply || '').length} chars`);
      } else {
        const why = (r && r.error) || 'unknown';
        appendBubble('AI', why, { err: true });
        setStatus('err', why);
        log('chat failed: ' + why);
      }
    } catch (e) {
      appendBubble('AI', e.message, { err: true });
      setStatus('err', e.message);
      log('chat threw: ' + e.message);
    }
  }

  async function sendStreaming(text) {
    setStatus('busy', 'streaming…');
    log(`chat.stream ${text.length} chars`);
    const body = appendBubble('AI', '', { streaming: true });
    const bubble = body.parentElement;
    let chars = 0;
    try {
      await window.zero.chat.stream(text, (tok) => {
        body.textContent += tok;
        chars += tok.length;
        bubble.scrollIntoView({ block: 'end' });
      });
      bubble.classList.remove('streaming');
      bumpTurn();
      setStatus('ok', `turn ${turn} · ${chars} chars (streamed)`);
      log(`chat stream ok: ${chars} chars`);
    } catch (e) {
      bubble.classList.remove('streaming');
      bubble.classList.add('err');
      body.textContent += `\n[error] ${e.message}`;
      setStatus('err', e.message);
      log('chat stream threw: ' + e.message);
    }
  }

  async function send() {
    const ta = $('input');
    const text = ta.value.trim();
    if (!text) { setStatus('err', 'empty'); return; }
    ta.value = '';
    appendBubble('You', text);
    $('sendBtn').disabled = true;
    try {
      if ($('streamMode').checked) await sendStreaming(text);
      else await sendOnce(text);
    } finally {
      $('sendBtn').disabled = false;
      ta.focus();
    }
  }

  async function reset() {
    try {
      await window.zero.chat.reset();
      $('thread').innerHTML = '';
      bumpTurn(0);
      setStatus('', 'session reset');
      log('chat.reset');
    } catch (e) {
      setStatus('err', e.message);
      log('reset threw: ' + e.message);
    }
  }

  document.addEventListener('DOMContentLoaded', () => {
    $('sendBtn').addEventListener('click', send);
    $('resetBtn').addEventListener('click', reset);
    $('input').addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    });
    refreshMeta();
  });
})();
