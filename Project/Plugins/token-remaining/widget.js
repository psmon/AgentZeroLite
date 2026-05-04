// Token Remaining widget — M0011
//
// Reads per-account / per-model rate-limit telemetry from the host DB
// (populated by the AgentZero statusLine wrapper + collector). Renders a
// minimal HUD with bars, plus a Settings overlay for theme / per-account
// install / per-model toggles.
//
// State persists in localStorage (origin = https://plugin.local/token-remaining/).
//
// Defensive contract:
//   • No native alert() / confirm() / prompt() anywhere — only the in-page
//     banner (#banner) + the in-page #confirm overlay. WebView2 in some
//     environments turns unhandled promise rejections into native dialogs,
//     so every async path is wrapped.
//   • RPC failures degrade gracefully (banner + empty-state UI), never
//     bubble up as uncaught rejections.

(function () {
  const $ = (id) => document.getElementById(id);
  const log = (...a) => { try { console.log('[token-remaining]', ...a); } catch {} };
  const warn = (...a) => { try { console.warn('[token-remaining]', ...a); } catch {} };

  // Catch every unhandled rejection so WebView2 never sees one and never
  // pops a native dialog on us.
  window.addEventListener('unhandledrejection', (e) => {
    warn('unhandled rejection caught', e.reason);
    banner('ERR', 'Unhandled rejection: ' + (e.reason && e.reason.message || e.reason));
    try { e.preventDefault(); } catch {}
  });
  window.addEventListener('error', (e) => {
    warn('window error', e.message, e.error);
    banner('ERR', 'Script error: ' + (e.message || 'unknown'));
  });

  function banner(level, msg) {
    try {
      const el = $('banner');
      if (!el) { warn(level, msg); return; }
      el.className = 'banner' + (level === 'INFO' ? ' info' : level === 'WARN' ? ' warn' : '');
      el.textContent = '[' + level + '] ' + msg;
      el.hidden = false;
    } catch (e) { warn('banner failed', e); }
  }
  function clearBanner() {
    try { const el = $('banner'); if (el) el.hidden = true; } catch {}
  }

  // ─── Boot guards ─────────────────────────────────────────────
  log('boot', { url: location.href, hasZero: !!window.zero });

  if (!window.zero) {
    try { $('noHost').hidden = false; } catch {}
    return;
  }
  // RPC namespace must include `tokens.remaining` — older bridges won't.
  if (!(window.zero.tokens && window.zero.tokens.remaining)) {
    try { $('app').hidden = false; } catch {}
    banner('ERR', 'window.zero.tokens.remaining is missing — host bridge is older than this widget. Rebuild AgentZeroLite.exe and reload.');
    return;
  }
  $('app').hidden = false;

  // Promise wrapper that swallows + reports failures instead of throwing.
  async function safe(label, fn, fallback) {
    try { return await fn(); }
    catch (e) {
      warn(label, 'failed', e);
      banner('ERR', label + ' failed: ' + (e && e.message || e));
      return fallback;
    }
  }

  // ─── Persistent settings (per origin) ─────────────────────────
  const STORAGE_KEY = 'token-remaining.v1';
  const defaults = {
    activeAccount: null,
    theme: 'dark',
    syncMin: 1,
    textColor: '#FDE68A',
    textBgColor: '#0F1115',
    textOpacity: 55,
    hiddenModels: {},
    // M0012 — Active Session panel
    showActivePanel:   true,
    activeWindowMin:   5,
  };
  function loadPrefs() {
    try { return Object.assign({}, defaults, JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}')); }
    catch { return Object.assign({}, defaults); }
  }
  function savePrefs(p) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(p)); } catch {}
  }
  let prefs = loadPrefs();
  log('prefs loaded', prefs);

  // ─── Helpers ──────────────────────────────────────────────────
  function thresholdClass(pct) {
    if (pct >= 85) return 'bad';
    if (pct >= 60) return 'warn';
    return 'ok';
  }
  function asciiBar(pct, width) {
    const filled = Math.max(0, Math.min(width, Math.round((pct / 100) * width)));
    return '█'.repeat(filled) + '░'.repeat(width - filled);
  }
  function fmtReset(epochOrIso) {
    if (!epochOrIso) return '';
    let when;
    if (typeof epochOrIso === 'string') when = new Date(epochOrIso);
    else when = new Date(epochOrIso * 1000);
    const ms = when.getTime() - Date.now();
    if (ms <= 0 || isNaN(ms)) return '';
    const s = Math.round(ms / 1000);
    const d = Math.floor(s / 86400);
    if (d > 0) {
      const h = Math.floor((s % 86400) / 3600);
      return h > 0 ? d + 'd ' + h + 'h' : d + 'd';
    }
    const h = Math.floor(s / 3600);
    if (h > 0) {
      const m = Math.floor((s % 3600) / 60);
      return m > 0 ? h + 'h ' + m + 'm' : h + 'h';
    }
    const m = Math.ceil(s / 60);
    return m + 'm';
  }
  function fmtAge(iso) {
    if (!iso) return 'never';
    const ms = Date.now() - new Date(iso).getTime();
    if (isNaN(ms)) return 'never';
    if (ms < 60000) return Math.round(ms / 1000) + 's ago';
    if (ms < 3600000) return Math.round(ms / 60000) + 'm ago';
    if (ms < 86400000) return Math.round(ms / 3600000) + 'h ago';
    return Math.round(ms / 86400000) + 'd ago';
  }
  // Compact age for the active-session table — "12s", "2m 14s", "4m 32s".
  // No "ago" suffix to keep the column tight (it's already labeled AGE).
  function fmtAgeShort(iso) {
    if (!iso) return '—';
    const ms = Date.now() - new Date(iso).getTime();
    if (isNaN(ms) || ms < 0) return '—';
    const s = Math.floor(ms / 1000);
    if (s < 60) return s + 's';
    const m = Math.floor(s / 60);
    const remS = s % 60;
    if (m < 60) return remS ? m + 'm ' + remS + 's' : m + 'm';
    const h = Math.floor(m / 60);
    return h + 'h ' + (m % 60) + 'm';
  }
  function ageThresholdClass(iso) {
    if (!iso) return '';
    const ms = Date.now() - new Date(iso).getTime();
    if (isNaN(ms) || ms < 0) return '';
    if (ms < 60000) return 'ok';
    if (ms < 180000) return 'warn';
    return 'bad';
  }
  function basenameOf(p) {
    if (!p) return '';
    const trimmed = String(p).replace(/[\\\/]+$/, '');
    const m = trimmed.match(/[^\\\/]+$/);
    return m ? m[0] : trimmed;
  }
  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>'"]/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[c]);
  }
  function hexToRgba(hex, alpha) {
    if (!hex || hex[0] !== '#' || (hex.length !== 4 && hex.length !== 7)) return 'rgba(15,17,21,' + alpha + ')';
    let r, g, b;
    if (hex.length === 4) { r = parseInt(hex[1] + hex[1], 16); g = parseInt(hex[2] + hex[2], 16); b = parseInt(hex[3] + hex[3], 16); }
    else { r = parseInt(hex.slice(1, 3), 16); g = parseInt(hex.slice(3, 5), 16); b = parseInt(hex.slice(5, 7), 16); }
    return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')';
  }
  function applyThemeClass(el) {
    el.className = 'w';
    let t = prefs.theme;
    if (t === 'auto') {
      try { t = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'; }
      catch { t = 'dark'; }
    }
    if (t === 'text') {
      el.classList.add('w--text');
      const o = Math.max(0, Math.min(100, +prefs.textOpacity || 0));
      el.style.background = hexToRgba(prefs.textBgColor, o / 100);
      el.style.color = prefs.textColor;
      el.style.border = '1px solid ' + hexToRgba(prefs.textColor, 0.22);
    } else {
      el.classList.add('w--' + t);
      el.style.background = '';
      el.style.color = '';
      el.style.border = '';
    }
  }

  // ─── Data layer ───────────────────────────────────────────────
  async function loadAccounts() {
    const profiles = await safe('profiles()', () => window.zero.tokens.remaining.profiles(), []);
    const accts    = await safe('accounts()', () => window.zero.tokens.remaining.accounts(), []);
    const seen = new Map();
    for (const p of (profiles || [])) seen.set(p.accountKey, { ...p, observations: 0, lastSeenUtc: null, modelCount: 0 });
    for (const a of (accts || [])) {
      const k = a.accountKey;
      if (!seen.has(k)) seen.set(k, { accountKey: k, ourWrapperInstalled: false, observations: 0, lastSeenUtc: null, modelCount: 0 });
      Object.assign(seen.get(k), { observations: a.observations, lastSeenUtc: a.lastSeenUtc, modelCount: a.modelCount });
    }
    return Array.from(seen.values());
  }

  // ─── Render the picker ────────────────────────────────────────
  async function rebuildAcctPicker() {
    const accts = await loadAccounts();
    log('accounts', accts);
    const sel = $('acctPicker');
    sel.innerHTML = '';
    for (const a of accts) {
      const opt = document.createElement('option');
      opt.value = a.accountKey;
      opt.textContent = a.accountKey + '  —  ' + (a.ourWrapperInstalled ? 'installed' : 'not installed');
      sel.appendChild(opt);
    }
    if (!prefs.activeAccount || !accts.some(a => a.accountKey === prefs.activeAccount)) {
      const best = accts.find(a => a.ourWrapperInstalled && a.observations > 0)
        || accts.find(a => a.observations > 0)
        || accts[0];
      prefs.activeAccount = best ? best.accountKey : null;
      savePrefs(prefs);
    }
    if (prefs.activeAccount) sel.value = prefs.activeAccount;
    updateAcctSub(accts);
  }

  function updateAcctSub(accts) {
    const a = accts.find(x => x.accountKey === prefs.activeAccount);
    if (!a) { $('acctSub').textContent = '—'; return; }
    $('acctSub').textContent = a.ourWrapperInstalled ? 'wrapper installed' : 'wrapper NOT installed';
  }

  // ─── Render the widget body ───────────────────────────────────
  async function renderWidget() {
    const w = $('widget');
    if (!prefs.activeAccount) {
      w.innerHTML = '';
      $('emptyHint').hidden = false;
      return;
    }
    const data = await safe('latest()', () => window.zero.tokens.remaining.latest(prefs.activeAccount),
      { models: [], collector: { totalRows: 0, lastTickUtc: null, lastError: null } });
    applyThemeClass(w);

    const models = data.models || [];
    if (!models.length) {
      w.innerHTML = '';
      $('emptyHint').hidden = false;
      return;
    }
    $('emptyHint').hidden = true;

    // rate_limits are account-scoped (M0011 empirical finding) — collapse
    // every observed model into a single account-level row. The query
    // service orders by ObservedAtUtc desc, so models[0] is the freshest
    // snapshot.
    const m = models[0];
    const isText = (effectiveTheme() === 'text');
    const rows = [];
    rows.push('<div class="w__hdr">'
      + '<div class="w__acct">acct: ' + escapeHtml(prefs.activeAccount) + '</div>'
      + '<div class="w__sub">last sync ' + fmtAge(data.collector && data.collector.lastTickUtc) + '</div>'
      + '</div>');
    if (!isText) rows.push('<div class="w__sep"></div>');

    const fhClass = thresholdClass(m.fiveHourPercent);
    const sdClass = thresholdClass(m.sevenDayPercent);
    const fhReset = fmtReset(m.fiveHourResetsAtUtc);
    const sdReset = fmtReset(m.sevenDayResetsAtUtc);
    if (isText) {
      rows.push(
        '<div class="w__row"><span class="w__lbl">Usage</span>'
        + '<span class="w__val">' + m.fiveHourPercent + '%' + (fhReset ? '  · resets in ' + fhReset : '') + '</span></div>');
      rows.push(
        '<div class="w__row"><span class="w__lbl">Weekly</span>'
        + '<span class="w__val">' + m.sevenDayPercent + '%' + (sdReset ? '  · resets in ' + sdReset : '') + '</span></div>');
    } else {
      rows.push(
        '<div class="w__row"><span class="w__lbl">Usage</span>'
        + '<span class="w__bar ' + fhClass + '">' + asciiBar(m.fiveHourPercent, 10) + '</span>'
        + '<span class="w__val">' + m.fiveHourPercent + '%' + (fhReset ? '  · resets in ' + fhReset : '') + '</span></div>');
      rows.push(
        '<div class="w__row"><span class="w__lbl">Weekly</span>'
        + '<span class="w__bar ' + sdClass + '">' + asciiBar(m.sevenDayPercent, 10) + '</span>'
        + '<span class="w__val">' + m.sevenDayPercent + '%' + (sdReset ? '  · resets in ' + sdReset : '') + '</span></div>');
    }
    const c = data.collector || {};
    rows.push('<div class="w__foot">'
      + '<span>DB rows: ' + (c.totalRows || 0) + ' · collector tick ' + fmtAge(c.lastTickUtc) + '</span>'
      + '<span>' + (c.lastError ? '⚠ ' + escapeHtml(c.lastError) : 'ok') + '</span>'
      + '</div>');
    w.innerHTML = rows.join('');
  }

  function effectiveTheme() {
    if (prefs.theme !== 'auto') return prefs.theme;
    try { return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'; }
    catch { return 'dark'; }
  }

  // ─── Active Session panel (M0012) ─────────────────────────────
  async function renderActivePanel() {
    const panel = $('activePanel');
    if (!panel) return;
    if (!prefs.showActivePanel) { panel.hidden = true; return; }
    panel.hidden = false;

    const eff = effectiveTheme();
    panel.className = 'as ' + (eff === 'light' ? 'as--light' : 'as--dark');

    const window_ = Math.max(1, Math.min(60, +prefs.activeWindowMin || 5));
    const data = await safe('activeSessions()',
      () => window.zero.tokens.remaining.activeSessions(window_),
      { windowMinutes: window_, sessions: [], collector: { totalRows: 0, lastTickUtc: null } });

    const sessions = data.sessions || [];
    const collector = data.collector || {};

    const winOptions = [1, 3, 5, 10, 15].map(v =>
      `<option value="${v}"${v === window_ ? ' selected' : ''}>${v} min</option>`).join('');

    const tickAge = fmtAge(collector.lastTickUtc);
    const totalRows = collector.totalRows || 0;

    if (!sessions.length) {
      panel.innerHTML =
        '<div class="as__hdr">'
        + '<div><div class="as__title">Active Sessions</div>'
        + '<div class="as__sub">window ' + window_ + ' min · tick ' + tickAge + '</div></div>'
        + '<div class="as__on">● ON</div>'
        + '</div>'
        + '<div class="as__sep"></div>'
        + '<div class="as__empty">No sessions in the last ' + window_ + ' min. Start a Claude Code session in any installed account to see it here.</div>'
        + '<div class="as__foot">'
        + '<span>DB rows: ' + totalRows + ' · heartbeat tick ' + tickAge + '</span>'
        + '<span><select class="as__win" id="asWindow" title="Active window">' + winOptions + '</select> '
        + '<button class="as__refresh" id="asRefresh" title="Force a heartbeat tick">⟳ refresh</button></span>'
        + '</div>';
    } else {
      const rows = sessions.map(s => {
        const ageCls = ageThresholdClass(s.lastSeenUtc);
        const sessShort = (s.sessionId || '').slice(0, 6) + '…';
        const proj = basenameOf(s.projectDir || s.cwd);
        const acctCls = (s.accountKey || '').toLowerCase().includes('qa') ? ' as--qa' : '';
        return '<tr>'
          + '<td><span class="as__cell-acct' + acctCls + '">' + escapeHtml(s.accountKey) + '</span></td>'
          + '<td class="as__cell-model">' + escapeHtml(s.model || '—') + '</td>'
          + '<td class="as__cell-proj" title="' + escapeHtml(s.projectDir || s.cwd || '') + '">' + escapeHtml(proj) + '</td>'
          + '<td class="as__cell-sess" data-full="' + escapeHtml(s.sessionId) + '" title="Click to copy full session id\n' + escapeHtml(s.sessionId) + '">' + escapeHtml(sessShort) + '</td>'
          + '<td class="as__cell-age ' + ageCls + '">' + fmtAgeShort(s.lastSeenUtc) + '</td>'
          + '</tr>';
      }).join('');

      panel.innerHTML =
        '<div class="as__hdr">'
        + '<div><div class="as__title">Active Sessions</div>'
        + '<div class="as__sub">' + sessions.length + ' active · window ' + window_ + ' min · tick ' + tickAge + '</div></div>'
        + '<div class="as__on">● ON</div>'
        + '</div>'
        + '<div class="as__sep"></div>'
        + '<table class="as__tbl">'
        + '<thead><tr><th>ACCT</th><th>MODEL</th><th>PROJECT</th><th>SESS</th><th style="text-align:right;">AGE</th></tr></thead>'
        + '<tbody>' + rows + '</tbody></table>'
        + '<div class="as__sep--soft" style="margin-top:8px;"></div>'
        + '<div class="as__foot">'
        + '<span>DB rows: ' + totalRows + ' · heartbeat tick ' + tickAge + '</span>'
        + '<span><select class="as__win" id="asWindow" title="Active window">' + winOptions + '</select> '
        + '<button class="as__refresh" id="asRefresh" title="Force a heartbeat tick">⟳ refresh</button></span>'
        + '</div>';

      // Click-to-copy session id.
      panel.querySelectorAll('.as__cell-sess').forEach(el => {
        el.onclick = async () => {
          const full = el.dataset.full;
          if (!full) return;
          try { await navigator.clipboard.writeText(full); banner('INFO', 'Copied session id: ' + full); }
          catch (e) { banner('WARN', 'Copy failed: ' + (e.message || e)); }
        };
      });
    }

    // Wire window selector + refresh.
    const winEl = $('asWindow');
    if (winEl) winEl.onchange = () => {
      prefs.activeWindowMin = +winEl.value || 5;
      savePrefs(prefs);
      renderActivePanel().catch(e => warn('window-change render', e));
    };
    const refEl = $('asRefresh');
    if (refEl) refEl.onclick = async () => {
      await safe('activeSessionsRefresh()',
        () => window.zero.tokens.remaining.activeSessionsRefresh(), null);
      await renderActivePanel();
    };
  }

  // ─── Settings dialog ──────────────────────────────────────────
  async function openSettings() {
    $('settings').hidden = false;
    syncSettingsControls();
    await renderProfilesList();
  }
  function closeSettings() {
    $('settings').hidden = true;
    renderWidget().catch(e => warn('renderWidget after close', e));
  }
  function syncSettingsControls() {
    document.querySelectorAll('#themeTabs button').forEach(b => {
      b.classList.toggle('active', b.dataset.theme === prefs.theme);
    });
    const showText = prefs.theme === 'text';
    $('textModeRow').hidden = !showText;
    $('textBgRow').hidden = !showText;
    $('textOpacityRow').hidden = !showText;
    $('textColor').value = prefs.textColor;
    $('textColorHex').value = prefs.textColor;
    $('textBgColor').value = prefs.textBgColor;
    $('textBgHex').value = prefs.textBgColor;
    $('textOpacity').value = prefs.textOpacity;
    $('syncMin').value = prefs.syncMin;
  }

  async function renderProfilesList() {
    const list = $('profilesList');
    const profiles = await safe('profiles() [settings]', () => window.zero.tokens.remaining.profiles(), []);
    if (!profiles || !profiles.length) {
      list.innerHTML = '<div class="hint">No Claude profiles found under your home dir (looked for <code>~/.claude*</code>).</div>';
      return;
    }
    const rows = profiles.map(p => {
      let pill = '';
      if (p.ourWrapperInstalled) pill = '<span class="prow__pill installed">installed</span>';
      else if (p.currentStatusLine) pill = '<span class="prow__pill thirdparty">3rd-party statusLine</span>';
      else pill = '<span class="prow__pill ready">ready</span>';

      const hudTag = p.claudeHudDetected
        ? 'claude-hud detected · wrapper will pipe through it'
        : (p.currentStatusLine
            ? 'existing statusLine will be wrapped: <code>' + escapeHtml(p.currentStatusLine) + '</code>'
            : 'no statusLine configured · standalone wrapper');

      const btns = p.ourWrapperInstalled
        ? '<button class="btn" data-action="reinstall" data-acct="' + escapeHtml(p.accountKey) + '">Reinstall</button>'
          + ' <button class="btn btn--danger" data-action="uninstall" data-acct="' + escapeHtml(p.accountKey) + '">Uninstall</button>'
        : '<button class="btn btn--primary" data-action="install" data-acct="' + escapeHtml(p.accountKey) + '">Install</button>';

      return '<div class="prow ' + (p.ourWrapperInstalled ? 'installed' : '') + '">'
        + '<div>'
        + '  <div class="prow__name">' + escapeHtml(p.accountKey) + pill + '</div>'
        + '  <div class="prow__path">' + escapeHtml(p.settingsJsonPath) + '</div>'
        + '  <div class="prow__path">' + hudTag + '</div>'
        + '</div>'
        + '<div>' + btns + '</div>'
        + '</div>';
    });
    list.innerHTML = rows.join('');
    list.querySelectorAll('button[data-action]').forEach(btn => {
      btn.onclick = () => onProfileAction(btn.dataset.action, btn.dataset.acct);
    });
  }

  async function onProfileAction(action, acct) {
    log('profile action', action, acct);
    try {
      if (action === 'install' || action === 'reinstall') {
        const r = await window.zero.tokens.remaining.install(acct);
        if (!r || !r.ok) { banner('ERR', 'Install failed: ' + ((r && r.error) || 'unknown')); return; }
        banner('INFO', 'Installed for "' + acct + '" — start a new Claude Code session to begin capture');
        await rebuildAcctPicker();
        await renderProfilesList();
        await renderWidget();
      } else if (action === 'uninstall') {
        const r = await window.zero.tokens.remaining.uninstall(acct, false);
        if (r && r.needsConfirm) {
          const body =
            'The current statusLine has been edited since install — confirm restore?\n\n'
            + 'CURRENT  : ' + (r.currentCommand || '<empty>') + '\n\n'
            + 'RESTORE  : ' + (r.originalCommandFromState || '<remove statusLine entirely>');
          showConfirm('Uninstall — confirm restore', body, async () => {
            const r2 = await window.zero.tokens.remaining.uninstall(acct, true);
            if (!r2 || !r2.ok) { banner('ERR', 'Uninstall failed: ' + ((r2 && r2.error) || 'unknown')); return; }
            banner('INFO', 'Uninstalled for "' + acct + '"');
            await rebuildAcctPicker();
            await renderProfilesList();
            await renderWidget();
          });
          return;
        }
        if (!r || !r.ok) { banner('ERR', 'Uninstall failed: ' + ((r && r.error) || 'unknown')); return; }
        banner('INFO', 'Uninstalled for "' + acct + '"');
        await rebuildAcctPicker();
        await renderProfilesList();
        await renderWidget();
      }
    } catch (e) {
      banner('ERR', action + ' failed: ' + (e && e.message || e));
    }
  }

  function showConfirm(title, body, onOk) {
    $('cfTitle').textContent = title;
    $('cfBody').textContent = body;
    $('confirm').hidden = false;
    $('cfOk').onclick = async () => {
      $('confirm').hidden = true;
      try { await onOk(); }
      catch (e) { banner('ERR', 'confirm action failed: ' + (e && e.message || e)); }
    };
    $('cfCancel').onclick = () => { $('confirm').hidden = true; };
  }

  // ─── Wire UI events ───────────────────────────────────────────
  $('acctPicker').onchange = (e) => {
    prefs.activeAccount = e.target.value;
    savePrefs(prefs);
    renderWidget().catch(err => warn('renderWidget after picker', err));
  };
  $('btnRefresh').onclick = async () => {
    await safe('refresh()', () => window.zero.tokens.remaining.refresh(), null);
    await safe('activeSessionsRefresh()', () => window.zero.tokens.remaining.activeSessionsRefresh(), null);
    await renderWidget();
    await renderActivePanel();
  };
  $('btnTogglePanel').onclick = () => {
    prefs.showActivePanel = !prefs.showActivePanel;
    savePrefs(prefs);
    $('btnTogglePanel').classList.toggle('on', prefs.showActivePanel);
    renderActivePanel().catch(e => warn('toggle render', e));
  };
  // Initial sync of toggle button state from prefs.
  $('btnTogglePanel').classList.toggle('on', prefs.showActivePanel);
  $('btnSettings').onclick = () => { openSettings().catch(e => banner('ERR', 'openSettings failed: ' + (e.message || e))); };
  $('settingsClose').onclick = closeSettings;

  document.querySelectorAll('#themeTabs button').forEach(b => {
    b.onclick = () => {
      prefs.theme = b.dataset.theme;
      savePrefs(prefs);
      syncSettingsControls();
      renderWidget().catch(e => warn('renderWidget after theme', e));
    };
  });

  function bindColor(picker, hex, key) {
    $(picker).oninput = () => { prefs[key] = $(picker).value; $(hex).value = $(picker).value; savePrefs(prefs); renderWidget().catch(()=>{}); };
    $(hex).oninput    = () => {
      const v = $(hex).value;
      if (/^#[0-9a-f]{6}$/i.test(v) || /^#[0-9a-f]{3}$/i.test(v)) {
        prefs[key] = v; $(picker).value = v; savePrefs(prefs); renderWidget().catch(()=>{});
      }
    };
  }
  bindColor('textColor',  'textColorHex', 'textColor');
  bindColor('textBgColor','textBgHex',    'textBgColor');
  $('textOpacity').oninput = () => { prefs.textOpacity = +$('textOpacity').value || 0; savePrefs(prefs); renderWidget().catch(()=>{}); };
  $('syncMin').oninput     = () => { prefs.syncMin = Math.max(1, +$('syncMin').value || 1); savePrefs(prefs); restartTimer(); };

  $('btnReset').onclick = () => {
    showConfirm('Reset DB',
      'This will delete ALL token-remaining observations and snapshot files. Next collector tick will rebuild from incoming snapshots.',
      async () => {
        await safe('reset()', () => window.zero.tokens.remaining.reset(), null);
        await renderWidget();
      });
  };

  // ─── Live refresh ─────────────────────────────────────────────
  let timer = null;
  function restartTimer() {
    if (timer) { clearInterval(timer); timer = null; }
    // Floor at 15s so the timer matches collector cadence (30s) closely
    // enough that latest DB rows surface within one UI tick of being
    // INSERTed, even if the host's tick event misses.
    const ms = Math.max(15, (+prefs.syncMin || 1) * 60) * 1000;
    log('restartTimer interval=' + (ms / 1000) + 's');
    timer = setInterval(() => {
      log('timer fire');
      renderWidget().catch(e => warn('timer renderWidget', e));
      renderActivePanel().catch(e => warn('timer renderActivePanel', e));
      rebuildAcctPicker().catch(e => warn('timer rebuildAcctPicker', e));
    }, ms);
  }

  try {
    window.zero.tokens.remaining.onTick((s) => {
      log('host tick event', s);
      renderWidget().catch(e => warn('tick renderWidget', e));
      rebuildAcctPicker().catch(e => warn('tick rebuildAcctPicker', e));
    });
    log('onTick subscribed');
  } catch (e) { warn('onTick subscribe failed', e); }
  try {
    window.zero.tokens.remaining.onActiveSessionsTick((s) => {
      log('host activeSessions tick event', s);
      renderActivePanel().catch(e => warn('tick renderActivePanel', e));
    });
    log('onActiveSessionsTick subscribed');
  } catch (e) { warn('onActiveSessionsTick subscribe failed', e); }

  // ─── Initial load ─────────────────────────────────────────────
  (async function boot() {
    try {
      await rebuildAcctPicker();
      await renderWidget();
      await renderActivePanel();
      restartTimer();
      log('boot complete');
    } catch (e) {
      warn('boot failed', e);
      banner('ERR', 'Boot failed: ' + (e && e.message || e));
    }
  })();

  // Convenience: clicking the banner clears it
  document.addEventListener('DOMContentLoaded', () => {
    const b = $('banner');
    if (b) b.onclick = clearBanner;
  });
  // (banner exists already at script run since this script is at end of body)
  try { const b = $('banner'); if (b && !b.onclick) b.onclick = clearBanner; } catch {}
})();
