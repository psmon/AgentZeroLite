// voice-test.js — first WebDev sandbox app.
// Demonstrates calling AgentZero's native IZeroBrowser host from JS.

(function () {
  const $ = (id) => document.getElementById(id);
  const log = (line) => {
    const pre = $('log');
    const ts = new Date().toLocaleTimeString('en-GB', { hour12: false });
    pre.textContent = `[${ts}] ${line}\n` + pre.textContent;
  };

  const setStatus = (kind, msg) => {
    const s = $('ttsStatus');
    s.className = 'status ' + kind;
    s.textContent = msg;
  };

  async function refreshMeta() {
    if (!window.zero) {
      log('window.zero unavailable — bridge not loaded');
      return;
    }
    try {
      const v = await window.zero.version();
      $('ver').textContent = v.version || '?';
    } catch (e) { $('ver').textContent = 'err'; log('version failed: ' + e.message); }

    try {
      const p = await window.zero.voice.providers();
      $('stt').textContent = p.stt;
      $('tts').textContent = p.tts;
      $('llm').textContent = p.llmBackend;
      log(`providers: stt=${p.stt} tts=${p.tts} llm=${p.llmBackend}`);
    } catch (e) {
      $('stt').textContent = $('tts').textContent = $('llm').textContent = 'err';
      log('providers failed: ' + e.message);
    }
  }

  async function speak() {
    const text = $('ttsText').value.trim();
    if (!text) { setStatus('err', 'empty'); return; }
    setStatus('busy', 'speaking…');
    log(`tts.speak ${text.length} chars`);
    try {
      const r = await window.zero.voice.speak(text);
      if (r && r.ok) {
        setStatus('ok', `${r.provider} · ${r.bytes}B · ${r.format}`);
        log(`tts ok: ${r.provider} ${r.bytes}B ${r.format}`);
      } else {
        const why = (r && r.error) || 'unknown';
        setStatus('err', why);
        log('tts failed: ' + why);
      }
    } catch (e) {
      setStatus('err', e.message);
      log('tts threw: ' + e.message);
    }
  }

  async function stop() {
    try {
      await window.zero.voice.stop();
      setStatus('', 'stopped');
      log('tts.stop');
    } catch (e) {
      log('stop threw: ' + e.message);
    }
  }

  document.addEventListener('DOMContentLoaded', () => {
    $('speakBtn').addEventListener('click', speak);
    $('stopBtn').addEventListener('click', stop);
    refreshMeta();
  });
})();
