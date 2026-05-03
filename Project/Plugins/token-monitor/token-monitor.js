/**
 * Token Monitor plugin (M0009)
 *
 * Read-only dashboard. The host runs an internal collector that polls
 * Claude Code (~/.claude/projects/**) and Codex CLI (~/.codex/sessions/**)
 * JSONL transcripts every minute and persists per-turn rows into the
 * AgentZero SQLite DB. This UI just queries that data via window.zero.tokens.*
 * and re-renders on every collector tick.
 */
(function () {
  if (!window.zero) {
    document.body.innerHTML =
      '<div style="padding:40px; color:#FFB454; font-family:Consolas;">window.zero bridge missing — open this from the WebDev menu inside AgentZero Lite.</div>';
    return;
  }

  const $ = (id) => document.getElementById(id);
  const els = {
    range: $('tm-range'),
    rangeLabel: $('tm-range-label'),
    refresh: $('tm-refresh'),
    reset: $('tm-reset'),
    cardTotal: $('tm-card-total'),
    cardRecords: $('tm-card-records'),
    cardClaude: $('tm-card-claude'),
    cardClaudeSub: $('tm-card-claude-sub'),
    cardCodex: $('tm-card-codex'),
    cardCodexSub: $('tm-card-codex-sub'),
    cardCollector: $('tm-card-collector'),
    cardCollectorSub: $('tm-card-collector-sub'),
    chart: $('tm-chart'),
    chartEmpty: $('tm-chart-empty'),
    accountBody: $('tm-account-body'),
    projectBody: $('tm-project-body'),
    sessionBody: $('tm-session-body'),
    status: $('tm-status'),
    tickInfo: $('tm-tick-info'),
  };

  // Vendor+accountKey → alias label. Re-fetched on every reload so alias
  // edits propagate to all tables that render the account name.
  const aliasMap = new Map();
  const aliasKey = (vendor, accountKey) => `${vendor}|${accountKey || ''}`;
  const lookupAlias = (vendor, accountKey) => aliasMap.get(aliasKey(vendor, accountKey)) || '';

  const VENDOR_LABEL = {
    anthropic: { name: 'Anthropic', pill: 'tm-pill-anthropic', color: '#d68a4a' },
    openai:    { name: 'OpenAI',    pill: 'tm-pill-openai',    color: '#10a37f' },
  };

  let currentRangeHours = 24;

  // ─── Formatters ───────────────────────────────────────────────────
  function fmtNum(n) {
    if (n == null || isNaN(n)) return '0';
    n = Number(n);
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(2).replace(/\.?0+$/, '') + 'M';
    if (n >= 10_000) return (n / 1_000).toFixed(1).replace(/\.?0+$/, '') + 'k';
    if (n >= 1_000) return (n / 1_000).toFixed(2).replace(/\.?0+$/, '') + 'k';
    return String(Math.round(n));
  }

  function fmtFull(n) {
    if (n == null || isNaN(n)) return '0';
    return Number(n).toLocaleString();
  }

  function fmtUtc(iso) {
    if (!iso) return '—';
    try {
      const d = new Date(iso);
      if (isNaN(d.getTime())) return '—';
      const pad = (x) => String(x).padStart(2, '0');
      return `${d.getUTCFullYear()}-${pad(d.getUTCMonth()+1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
    } catch { return '—'; }
  }

  function shortPath(p) {
    if (!p) return '—';
    p = String(p);
    if (p.length <= 40) return p;
    return '…' + p.slice(p.length - 38);
  }

  function shortSession(id) {
    if (!id) return '—';
    return String(id).slice(0, 8);
  }

  function rangeLabelText(hours) {
    if (!hours || hours <= 0) return 'all time';
    if (hours < 24) return `last ${hours}h`;
    if (hours < 168) return `last ${Math.round(hours / 24)}d`;
    if (hours <= 720) return `last ${Math.round(hours / 24)}d`;
    return `last ${Math.round(hours / 24)}d`;
  }

  function setStatus(text, kind) {
    els.status.textContent = text;
    els.status.classList.remove('tm-status-error', 'tm-status-busy');
    if (kind === 'error') els.status.classList.add('tm-status-error');
    else if (kind === 'busy') els.status.classList.add('tm-status-busy');
  }

  // ─── Data load ────────────────────────────────────────────────────
  async function loadAll() {
    setStatus('loading…', 'busy');
    const sinceHours = currentRangeHours > 0 ? currentRangeHours : null;
    try {
      const [summary, accounts, projects, sessions, series, aliases] = await Promise.all([
        window.zero.tokens.summary(sinceHours),
        window.zero.tokens.byAccount(sinceHours),
        window.zero.tokens.byProject(sinceHours, 50),
        window.zero.tokens.sessions(sinceHours, 12),
        window.zero.tokens.timeseries(currentRangeHours > 0 ? currentRangeHours : 24 * 30, pickBucketMinutes(currentRangeHours)),
        window.zero.tokens.aliases(),
      ]);
      // Refresh alias map first so subsequent renders see latest names.
      aliasMap.clear();
      (aliases || []).forEach((a) => aliasMap.set(aliasKey(a.vendor, a.accountKey), a.alias || ''));

      renderCards(summary);
      renderAccounts(accounts);
      renderProjects(projects);
      renderSessions(sessions);
      renderChart(series);
      setStatus('idle');
    } catch (err) {
      setStatus(`load failed: ${err.message || err}`, 'error');
    }
  }

  function pickBucketMinutes(hours) {
    if (!hours || hours <= 0) hours = 24 * 30;
    if (hours <= 6) return 5;
    if (hours <= 24) return 30;
    if (hours <= 168) return 60 * 6;     // 6h buckets for a week
    return 60 * 24;                       // 1d buckets for >= 30d
  }

  // ─── Render: cards ────────────────────────────────────────────────
  function renderCards(summary) {
    const totals = summary?.totals ?? {};
    const byVendor = summary?.byVendor ?? [];
    const collector = summary?.collector ?? {};

    els.cardTotal.textContent = fmtNum(totals.total ?? 0);
    els.cardTotal.title = `${fmtFull(totals.total ?? 0)} total tokens`;
    els.cardRecords.textContent = `${fmtFull(totals.records ?? 0)} records`;

    const claudeRow = byVendor.find((v) => v.vendor === 'anthropic');
    const codexRow = byVendor.find((v) => v.vendor === 'openai');

    els.cardClaude.textContent = fmtNum(claudeRow?.total ?? 0);
    els.cardClaude.title = claudeRow ? `${fmtFull(claudeRow.total)} tokens, ${fmtFull(claudeRow.records)} turns` : 'no data';
    els.cardClaudeSub.textContent = claudeRow ? `${fmtFull(claudeRow.records)} turns` : 'no data';

    els.cardCodex.textContent = fmtNum(codexRow?.total ?? 0);
    els.cardCodex.title = codexRow ? `${fmtFull(codexRow.total)} tokens, ${fmtFull(codexRow.records)} turns` : 'no data';
    els.cardCodexSub.textContent = codexRow ? `${fmtFull(codexRow.records)} turns` : 'no data';

    els.cardCollector.textContent = `${fmtFull(collector.filesTracked ?? 0)} files`;
    const lines = collector.totalLines ?? 0;
    const last = collector.lastUpdatedUtc ? `last ${fmtUtc(collector.lastUpdatedUtc)} UTC` : 'no scans yet';
    els.cardCollectorSub.textContent = `${fmtFull(lines)} lines · ${last}`;
  }

  // ─── Render: tables ───────────────────────────────────────────────
  function renderAccountCell(vendor, accountKey) {
    const alias = lookupAlias(vendor, accountKey);
    const raw = accountKey || '—';
    if (alias) {
      return `<span class="tm-alias">${escape(alias)}</span>`
        + `<span class="tm-account-key" title="${escape(raw)}"> · ${escape(raw)}</span>`
        + `<span class="tm-edit-icon" title="rename">✎</span>`;
    }
    return `<span class="tm-account-key" title="${escape(raw)}">${escape(raw)}</span>`
      + `<span class="tm-edit-icon" title="rename">✎</span>`;
  }

  function renderAccounts(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
      els.accountBody.innerHTML = '<tr><td colspan="9" class="tm-empty">No usage recorded yet — let the collector run.</td></tr>';
      return;
    }
    const html = rows.map((r) => {
      const v = VENDOR_LABEL[r.vendor] || { name: r.vendor || '?', pill: '' };
      return `<tr>
        <td><span class="tm-pill ${v.pill}">${escape(v.name)}</span></td>
        <td class="tm-account-cell" data-vendor="${escape(r.vendor || '')}" data-account="${escape(r.accountKey || '')}">${renderAccountCell(r.vendor, r.accountKey)}</td>
        <td class="tm-num">${fmtFull(r.input)}</td>
        <td class="tm-num">${fmtFull(r.output)}</td>
        <td class="tm-num">${fmtFull(r.cacheRead)}</td>
        <td class="tm-num">${fmtFull(r.cacheCreate)}</td>
        <td class="tm-num">${fmtFull(r.reasoning)}</td>
        <td class="tm-num"><strong>${fmtFull(r.total)}</strong></td>
        <td class="tm-num">${fmtFull(r.records)}</td>
      </tr>`;
    }).join('');
    els.accountBody.innerHTML = html;
  }

  // ─── Alias inline edit ────────────────────────────────────────────
  function startAliasEdit(cell) {
    if (cell.querySelector('input')) return; // already editing
    const vendor = cell.getAttribute('data-vendor') || '';
    const accountKey = cell.getAttribute('data-account') || '';
    const current = lookupAlias(vendor, accountKey);

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'tm-alias-input';
    input.value = current;
    input.placeholder = 'alias (empty to clear)';
    input.maxLength = 60;

    cell.innerHTML = '';
    cell.appendChild(input);
    input.focus();
    input.select();

    let committing = false;
    const commit = async () => {
      if (committing) return;
      committing = true;
      const next = input.value.trim();
      try {
        if (!next) {
          if (current) await window.zero.tokens.removeAlias(vendor, accountKey);
        } else if (next !== current) {
          await window.zero.tokens.setAlias(vendor, accountKey, next);
        }
        // Refresh aliases + re-render tables so other rows pick up the change too.
        await loadAll();
      } catch (err) {
        setStatus(`alias save failed: ${err.message || err}`, 'error');
        cell.innerHTML = renderAccountCell(vendor, accountKey);
      }
    };
    const cancel = () => {
      cell.innerHTML = renderAccountCell(vendor, accountKey);
    };

    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); commit(); }
      else if (e.key === 'Escape') { e.preventDefault(); cancel(); }
    });
    input.addEventListener('blur', commit);
  }

  els.accountBody.addEventListener('click', (e) => {
    const cell = e.target.closest('.tm-account-cell');
    if (!cell) return;
    if (e.target.tagName === 'INPUT') return;
    startAliasEdit(cell);
  });

  function renderProjects(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
      els.projectBody.innerHTML = '<tr><td colspan="8" class="tm-empty">No projects yet.</td></tr>';
      return;
    }
    const html = rows.map((r) => {
      return `<tr>
        <td><strong title="${escape(r.pathSample || '')}">${escape(r.project || '—')}</strong></td>
        <td class="tm-mono">${escape(r.vendors || '')}</td>
        <td class="tm-num">${fmtFull(r.input)}</td>
        <td class="tm-num">${fmtFull(r.output)}</td>
        <td class="tm-num">${fmtFull(r.cacheRead)}</td>
        <td class="tm-num"><strong>${fmtFull(r.total)}</strong></td>
        <td class="tm-num">${fmtFull(r.sessions)}</td>
        <td class="tm-mono">${escape(fmtUtc(r.lastSeen))}</td>
      </tr>`;
    }).join('');
    els.projectBody.innerHTML = html;
  }

  function renderSessions(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
      els.sessionBody.innerHTML = '<tr><td colspan="9" class="tm-empty">No sessions yet.</td></tr>';
      return;
    }
    const html = rows.map((r) => {
      const v = VENDOR_LABEL[r.vendor] || { name: r.vendor || '?', pill: '' };
      return `<tr>
        <td><span class="tm-pill ${v.pill}">${escape(v.name)}</span></td>
        <td class="tm-mono" title="${escape(r.sessionId || '')}">${escape(shortSession(r.sessionId))}</td>
        <td class="tm-mono" title="${escape(r.cwd || '')}">${escape(shortPath(r.cwd))}</td>
        <td class="tm-mono">${escape(r.model || '—')}</td>
        <td class="tm-mono">${escape(fmtUtc(r.lastSeen))}</td>
        <td class="tm-num">${fmtFull(r.input)}</td>
        <td class="tm-num">${fmtFull(r.output)}</td>
        <td class="tm-num"><strong>${fmtFull(r.total)}</strong></td>
        <td class="tm-num">${fmtFull(r.records)}</td>
      </tr>`;
    }).join('');
    els.sessionBody.innerHTML = html;
  }

  // ─── Reset & re-scan ──────────────────────────────────────────────
  async function resetAndRescan() {
    if (els.reset.disabled) return;
    if (!window.confirm('Wipe all collected token usage rows + checkpoints, then re-scan all profile dirs from scratch?')) return;
    els.reset.disabled = true;
    setStatus('resetting…', 'busy');
    try {
      const r = await window.zero.tokens.reset();
      setStatus(`reset done: -${r.rowsDeleted} rows, -${r.checkpointsDeleted} files. Re-scanning…`, 'busy');
      const tick = await window.zero.tokens.refresh();
      updateTickInfo(tick);
      await loadAll();
      setStatus('idle');
    } catch (err) {
      setStatus(`reset failed: ${err.message || err}`, 'error');
    } finally {
      els.reset.disabled = false;
    }
  }

  function escape(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // ─── Render: chart (vanilla canvas, two-series stacked line) ──────
  function renderChart(series) {
    const canvas = els.chart;
    const ctx = canvas.getContext('2d');
    const dpr = window.devicePixelRatio || 1;

    const cssW = canvas.clientWidth || 900;
    const cssH = canvas.clientHeight || 220;
    if (canvas.width !== cssW * dpr || canvas.height !== cssH * dpr) {
      canvas.width = cssW * dpr;
      canvas.height = cssH * dpr;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);

    if (!Array.isArray(series) || series.length === 0) {
      els.chartEmpty.hidden = false;
      return;
    }
    els.chartEmpty.hidden = true;

    const buckets = new Map(); // bucketISO -> { anthropic, openai }
    for (const p of series) {
      const k = p.bucketUtc;
      let row = buckets.get(k);
      if (!row) { row = { bucket: k, anthropic: 0, openai: 0 }; buckets.set(k, row); }
      if (p.vendor === 'anthropic') row.anthropic = (row.anthropic || 0) + Number(p.total || 0);
      else if (p.vendor === 'openai') row.openai = (row.openai || 0) + Number(p.total || 0);
    }
    const points = Array.from(buckets.values()).sort((a, b) => new Date(a.bucket) - new Date(b.bucket));
    if (points.length === 0) { els.chartEmpty.hidden = false; return; }

    const padL = 48, padR = 12, padT = 14, padB = 26;
    const w = cssW - padL - padR;
    const h = cssH - padT - padB;
    const maxY = Math.max(1, ...points.map((p) => Math.max(p.anthropic, p.openai)));

    // grid + Y labels
    ctx.strokeStyle = '#2a3140';
    ctx.fillStyle = '#8b949e';
    ctx.font = '10px Inter, system-ui, sans-serif';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = padT + (h * i) / 4;
      ctx.beginPath();
      ctx.moveTo(padL, y);
      ctx.lineTo(padL + w, y);
      ctx.stroke();
      const v = maxY * (1 - i / 4);
      ctx.fillText(fmtNum(v), 6, y + 3);
    }

    // X axis labels (sparse — 4 ticks)
    ctx.fillStyle = '#8b949e';
    const xTickCount = Math.min(5, points.length);
    for (let i = 0; i < xTickCount; i++) {
      const idx = Math.round(((points.length - 1) * i) / Math.max(1, xTickCount - 1));
      const xRatio = points.length === 1 ? 0.5 : idx / (points.length - 1);
      const x = padL + xRatio * w;
      const label = formatBucketShort(points[idx].bucket);
      const tw = ctx.measureText(label).width;
      ctx.fillText(label, Math.max(padL, Math.min(padL + w - tw, x - tw / 2)), cssH - 8);
    }

    drawSeries(ctx, points, 'anthropic', padL, padT, w, h, maxY, '#d68a4a');
    drawSeries(ctx, points, 'openai',    padL, padT, w, h, maxY, '#10a37f');
  }

  function drawSeries(ctx, points, key, padL, padT, w, h, maxY, color) {
    if (points.length === 0) return;
    ctx.strokeStyle = color;
    ctx.fillStyle = color + '33';
    ctx.lineWidth = 1.6;

    ctx.beginPath();
    let started = false;
    for (let i = 0; i < points.length; i++) {
      const xRatio = points.length === 1 ? 0.5 : i / (points.length - 1);
      const x = padL + xRatio * w;
      const yRatio = (points[i][key] || 0) / maxY;
      const y = padT + (1 - yRatio) * h;
      if (!started) { ctx.moveTo(x, y); started = true; }
      else ctx.lineTo(x, y);
    }
    ctx.stroke();

    // Soft fill under line
    ctx.lineTo(padL + w, padT + h);
    ctx.lineTo(padL, padT + h);
    ctx.closePath();
    ctx.fill();

    // Dots
    ctx.fillStyle = color;
    for (let i = 0; i < points.length; i++) {
      const xRatio = points.length === 1 ? 0.5 : i / (points.length - 1);
      const x = padL + xRatio * w;
      const yRatio = (points[i][key] || 0) / maxY;
      const y = padT + (1 - yRatio) * h;
      ctx.beginPath();
      ctx.arc(x, y, 2.2, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function formatBucketShort(iso) {
    try {
      const d = new Date(iso);
      const pad = (x) => String(x).padStart(2, '0');
      if (currentRangeHours > 0 && currentRangeHours <= 24)
        return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
      if (currentRangeHours > 0 && currentRangeHours <= 168)
        return `${pad(d.getUTCMonth()+1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}h`;
      return `${pad(d.getUTCMonth()+1)}-${pad(d.getUTCDate())}`;
    } catch { return '?'; }
  }

  // ─── Tick + manual refresh ────────────────────────────────────────
  function updateTickInfo(s) {
    const ts = s.finishedAt ? fmtUtc(s.finishedAt) : '—';
    const err = s.error ? ` · err: ${s.error}` : '';
    els.tickInfo.textContent = `last tick: ${ts} UTC · scanned ${s.filesScanned}, +${s.rowsInserted} rows (claude ${s.claudeRows}, codex ${s.codexRows})${err}`;
  }

  async function manualRefresh() {
    if (els.refresh.disabled) return;
    els.refresh.disabled = true;
    els.refresh.classList.add('tm-loading');
    setStatus('refreshing…', 'busy');
    try {
      const s = await window.zero.tokens.refresh();
      updateTickInfo(s);
      await loadAll();
    } catch (err) {
      setStatus(`refresh failed: ${err.message || err}`, 'error');
    } finally {
      els.refresh.disabled = false;
      els.refresh.classList.remove('tm-loading');
    }
  }

  // ─── Wire UI ──────────────────────────────────────────────────────
  els.range.addEventListener('change', () => {
    currentRangeHours = parseInt(els.range.value, 10) || 0;
    els.rangeLabel.textContent = rangeLabelText(currentRangeHours);
    loadAll();
  });
  els.refresh.addEventListener('click', manualRefresh);
  els.reset.addEventListener('click', resetAndRescan);

  window.zero.tokens.onTick((s) => {
    if (!s) return;
    updateTickInfo(s);
    // Throttle reload — collector ticks every minute, schema's already
    // displayed; just pull fresh aggregates without thrashing the chart.
    loadAll();
  });

  // Initial paint
  els.rangeLabel.textContent = rangeLabelText(currentRangeHours);
  loadAll();
})();
