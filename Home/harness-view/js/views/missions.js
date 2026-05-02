/**
 * Missions — TODO-style PRD ↔ Result pairing.
 *
 * Sources (resolved at build time by scripts/build-indexes.js):
 *   PRD     → harness/missions/M{NNNN}-*.md
 *   Result  → harness/logs/mission-records/M{NNNN}-*.md
 *
 * Pairing key: the M{NNNN} prefix in the filename. New missions are picked up
 * automatically on the next index rebuild — no view code change needed.
 *
 * Routes:
 *   #missions              — list (filter pills + checklist)
 *   #missions/{id}         — detail view, PRD tab default
 *   #missions/{id}/result  — detail view, Result tab
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, renderSubBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

// Status → display metadata. Single source of truth — adding a new status to
// the index builder's STATUSES list shows up here too via fallback handling.
const STATUS_META = {
  inbox:       { label: 'Inbox',        icon: 'circle',      cls: 'status-inbox' },
  in_progress: { label: 'In Progress',  icon: 'dotSquare',   cls: 'status-progress' },
  done:        { label: 'Done',         icon: 'checkSquare', cls: 'status-done' },
  partial:     { label: 'Partial',      icon: 'dotSquare',   cls: 'status-partial' },
  blocked:     { label: 'Blocked',      icon: 'x',           cls: 'status-blocked' },
  cancelled:   { label: 'Cancelled',    icon: 'x',           cls: 'status-cancelled' },
};
function statusMeta(s) {
  return STATUS_META[s] || { label: humanize(s || 'unknown'), icon: 'circle', cls: 'status-unknown' };
}

export async function render(ctx) {
  const { viewEl, topbarEl, subbarEl, menu, params } = ctx;
  const index = await loadIndex('harness-missions');

  if (params) return renderDetail(ctx, index, params);

  const items = Array.isArray(index?.items) ? index.items : [];
  const counts = index?.counts || { all: items.length };
  const statuses = Array.isArray(index?.statuses) ? index.statuses : Object.keys(STATUS_META);

  const state = { status: 'all', query: '' };

  renderTopBar(topbarEl, {
    title: 'Missions',
    subtitle: `${index?.base || 'harness/missions'} · paired with ${index?.recordsBase || 'harness/logs/mission-records'}`,
    badge: { kind: 'readonly', text: 'Reference' },
    search: { placeholder: 'Search id, title, operator...', oninput: e => { state.query = e.target.value; redraw(); } },
  });

  if (!items.length) {
    mount(viewEl, h('div', { class: 'empty' }, [
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, `${index?.base || 'harness/missions'} — no missions yet`),
      h('div', { style: { color: '#6B7280' } }, 'Add a file named M{NNNN}-{slug}.md under harness/missions/. The frontmatter status drives this view.'),
    ]));
    return;
  }

  const container = h('div');
  mount(viewEl, container);

  function filter() {
    const q = state.query.toLowerCase();
    return items.filter(it => {
      if (state.status !== 'all' && it.status !== state.status) return false;
      if (!q) return true;
      return [it.id, it.title, it.operator]
        .filter(Boolean)
        .some(v => String(v).toLowerCase().includes(q));
    });
  }

  function redraw() {
    const filtered = filter();

    // Pills — All + each status that has at least one entry. Hide zero buckets
    // so the bar stays compact; user still sees them when populated.
    const pills = h('div', { class: 'pill-bar' });
    pills.appendChild(pill('All', counts.all || items.length, state.status === 'all',
      () => { state.status = 'all'; redraw(); }));
    for (const s of statuses) {
      const n = counts[s] || 0;
      if (!n && state.status !== s) continue;
      const meta = statusMeta(s);
      pills.appendChild(pill(meta.label, n, state.status === s,
        () => { state.status = s; redraw(); }, meta.cls));
    }

    const list = h('div', { class: 'todo-list' });
    if (!filtered.length) {
      list.appendChild(h('div', { class: 'empty' }, 'No missions match the current filter.'));
    } else {
      for (const it of filtered) list.appendChild(renderRow(it, menu));
    }

    mount(container, pills, list);
  }

  function pill(label, count, active, onclick, extraCls = '') {
    return h('div', {
      class: 'pill' + (active ? ' active' : '') + (active ? '' : ' ' + extraCls),
      onclick,
    }, `${label} · ${count}`);
  }

  redraw();
}

function renderRow(it, menu) {
  const meta = statusMeta(it.status);
  const indicator = h('span', {
    class: 'todo-indicator ' + meta.cls,
    title: meta.label,
    html: ICONS[meta.icon] || ICONS.circle,
  });

  const idBadge = h('span', { class: 'todo-id' }, it.id);

  const titleEl = h('div', { class: 'todo-title' }, it.title || it.id);

  const metaBits = [];
  if (it.priority) metaBits.push(h('span', { class: 'todo-pri pri-' + it.priority }, `priority: ${it.priority}`));
  if (it.operator) metaBits.push(h('span', { class: 'todo-meta' }, `@${it.operator}`));
  if (it.language) metaBits.push(h('span', { class: 'todo-meta' }, it.language));
  if (it.created)  metaBits.push(h('span', { class: 'todo-meta' }, it.created));
  if (it.recordFinished) {
    metaBits.push(h('span', { class: 'todo-meta done' }, '✓ ' + (it.recordFinished.slice(0, 10))));
  } else if (it.recordFile) {
    metaBits.push(h('span', { class: 'todo-meta done' }, '✓ logged'));
  }
  const metaRow = h('div', { class: 'todo-meta-row' }, metaBits);

  const links = h('div', { class: 'todo-links' });
  links.appendChild(h('span', { class: 'todo-status-text ' + meta.cls }, meta.label));
  if (it.recordFile) {
    links.appendChild(h('span', { class: 'todo-tag rec' }, 'PRD + Result'));
  } else {
    links.appendChild(h('span', { class: 'todo-tag prd' }, 'PRD only'));
  }

  return h('div', {
    class: 'todo-row ' + meta.cls,
    onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`; },
  }, [
    indicator,
    h('div', { class: 'todo-body' }, [
      h('div', { class: 'todo-head' }, [idBadge, titleEl]),
      metaRow,
    ]),
    links,
  ]);
}

async function renderDetail(ctx, index, params) {
  const { viewEl, topbarEl, subbarEl, menu } = ctx;
  // params can be "M0002" or "M0002/result"
  const parts = String(params).split('/');
  const id = decodeURIComponent(parts[0] || '');
  const tab = parts[1] === 'result' ? 'result' : 'prd';

  const it = index?.items?.find(x => x.id === id);
  if (!it) {
    renderTopBar(topbarEl, {
      title: id,
      extra: h('button', { class: 'btn', onclick: () => { location.hash = '#missions'; } }, '← Back to list'),
    });
    mount(viewEl, emptyState(`Mission ${id} not found in the index.`));
    return;
  }

  const sMeta = statusMeta(it.status);
  renderTopBar(topbarEl, {
    title: `${it.id} — ${it.title}`,
    subtitle: tab === 'result' ? it.recordFile : it.file,
    badge: { kind: 'readonly', text: sMeta.label },
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#missions'; } }, '← Back to list'),
  });

  const tabs = [
    {
      label: 'PRD (Request)',
      count: null,
      active: tab === 'prd',
      onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`; },
    },
    {
      label: 'Result (Execution Log)',
      count: it.recordFile ? null : 0,
      active: tab === 'result',
      onclick: () => {
        if (!it.recordFile) return;
        location.hash = `#${menu.id}/${encodeURIComponent(it.id)}/result`;
      },
    },
  ];
  renderSubBar(subbarEl, tabs);

  mount(viewEl, loadingState());

  const targetPath = tab === 'result' ? it.recordFile : it.file;
  if (!targetPath) {
    mount(viewEl, emptyState('No execution log yet — only the PRD has been written.'));
    return;
  }
  const content = await loadMd(targetPath);
  if (content == null) {
    mount(viewEl, emptyState('Could not load file: ' + targetPath));
    return;
  }

  // Split frontmatter → structured card; pass only the body to the MD viewer
  // so the YAML block doesn't show up as raw text.
  const { meta, body } = parseFrontmatter(content);
  const card = createMissionCard(meta, { kind: tab, fallbackId: it.id });
  const wrap = h('div');
  if (card) wrap.appendChild(card);
  wrap.appendChild(createMdViewer({
    content: body || content,  // fallback: if no frontmatter, render the whole file
    readOnly: true,
    breadcrumb: targetPath.split('/'),
  }));
  mount(viewEl, wrap);
}

/* ─── Mission frontmatter → structured card ───────────────────────────── */
function createMissionCard(meta, opts = {}) {
  if (!meta || typeof meta !== 'object' || !Object.keys(meta).length) return null;

  const id = meta.id || meta.mission || opts.fallbackId || '?';
  const title = meta.title || '(untitled)';
  const status = meta.status || null;
  const sMeta = statusMeta(status);

  const card = h('div', { class: 'skill-spec mission-spec' });

  // Header: ID badge + title + status badge
  const head = h('div', { class: 'spec-head mission-head' }, [
    h('span', { class: 'todo-id' }, id),
    h('span', { class: 'spec-name', style: { flex: '1' } }, title),
    status ? h('span', {
      class: 'todo-status-text ' + sMeta.cls,
      style: { fontSize: '11px', padding: '4px 10px' },
    }, sMeta.label) : null,
    opts.kind === 'result'
      ? h('span', { class: 'spec-tag' }, 'EXECUTION LOG')
      : h('span', { class: 'spec-tag' }, 'PRD'),
  ]);
  card.appendChild(head);

  // Inline meta strip (operator · language · priority · created)
  const stripBits = [];
  if (meta.operator) stripBits.push(`@${meta.operator}`);
  if (meta.language) stripBits.push(meta.language);
  if (meta.priority) stripBits.push(`priority: ${meta.priority}`);
  if (meta.created)  stripBits.push(`created: ${meta.created}`);
  if (stripBits.length) {
    card.appendChild(h('div', { class: 'mission-strip' }, stripBits.join('  ·  ')));
  }

  // Type / description (if present in frontmatter — usually in PRDs)
  if (meta.type) {
    card.appendChild(row('Type', meta.type));
  }

  // Related missions (e.g. related: [M0000, ...])
  const related = parseListLike(meta.related);
  if (related.length) {
    card.appendChild(chipRow('Related · ' + related.length, related));
  }

  // ── Result-only fields ──
  if (opts.kind === 'result') {
    if (meta.started)  card.appendChild(row('Started',  formatDateTime(meta.started)));
    if (meta.finished) card.appendChild(row('Finished', formatDateTime(meta.finished)));
    const dur = duration(meta.started, meta.finished);
    if (dur) card.appendChild(row('Duration', dur));

    const dispatched = parseListLike(meta.dispatched_to || meta.dispatchedTo);
    if (dispatched.length) {
      card.appendChild(chipRow('Dispatched · ' + dispatched.length, dispatched));
    }

    const artifacts = parseListLike(meta.artifacts);
    if (artifacts.length) {
      card.appendChild(artifactRow(artifacts));
    }
  }

  return card;
}

function row(label, value) {
  return h('div', { class: 'spec-row' }, [
    h('div', { class: 'spec-label' }, label),
    h('div', { class: 'spec-value' }, String(value)),
  ]);
}

function chipRow(label, items) {
  const chips = h('div', { class: 'spec-tools' });
  for (const it of items) {
    chips.appendChild(h('span', { class: 'spec-tool', title: it }, it));
  }
  return h('div', { class: 'spec-row' }, [
    h('div', { class: 'spec-label' }, label),
    chips,
  ]);
}

function artifactRow(items) {
  // Artifacts can be many — show as compact monospace list, scrollable if very long.
  const list = h('div', { class: 'mission-artifacts' });
  for (const p of items) {
    list.appendChild(h('div', { class: 'mission-artifact' }, [
      h('span', { class: 'spec-tool', html: ICONS.file, style: { padding: '2px 4px' } }),
      h('code', {}, p),
    ]));
  }
  return h('div', { class: 'spec-row' }, [
    h('div', { class: 'spec-label' }, `Artifacts · ${items.length}`),
    list,
  ]);
}

/* parse YAML-like list. Accepts:
 *   - already-an-array (block-style "- foo" parsed by spec-card)
 *   - "[a, b, c]" inline form (string from minimal parser)
 *   - "" / undefined → [] */
function parseListLike(v) {
  if (Array.isArray(v)) return v.map(s => String(s).trim()).filter(Boolean);
  if (typeof v !== 'string') return [];
  const m = v.trim().match(/^\[(.*)\]$/);
  if (!m) return [];
  return m[1].split(',').map(s => s.trim().replace(/^["']|["']$/g, '')).filter(Boolean);
}

function formatDateTime(iso) {
  if (!iso) return '';
  // "2026-05-02T08:50:00+09:00" → "2026-05-02 08:50 (+09:00)"
  const m = String(iso).match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2})?(.*)$/);
  if (!m) return iso;
  const tz = m[3] ? ` (${m[3]})` : '';
  return `${m[1]} ${m[2]}${tz}`;
}

function duration(startIso, endIso) {
  if (!startIso || !endIso) return null;
  const s = Date.parse(startIso), e = Date.parse(endIso);
  if (!Number.isFinite(s) || !Number.isFinite(e) || e < s) return null;
  const minutes = Math.round((e - s) / 60000);
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60), m = minutes % 60;
  return m ? `${h}h ${m}m` : `${h}h`;
}
