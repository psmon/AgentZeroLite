/**
 * Missions — fridge + post-it metaphor.
 *
 * The list view paints a brushed-metal "fridge" surface with sticky notes
 * pinned on it (one per M{NNNN}). Tapping a note opens a popup modal that
 * re-uses the existing PRD/Result detail card; closing the modal returns
 * to the fridge. The hash routes are unchanged so deep-linking still works.
 *
 * Sources (resolved at build time by scripts/build-indexes.js):
 *   PRD     → harness/missions/M{NNNN}-*.md
 *   Result  → harness/logs/mission-records/M{NNNN}-*.md
 *
 * Pairing key: the M{NNNN} prefix in the filename. New missions are picked up
 * automatically on the next index rebuild — no view code change needed.
 *
 * Routes:
 *   #missions              — fridge view
 *   #missions/{id}         — fridge + detail modal (PRD tab default)
 *   #missions/{id}/result  — fridge + detail modal (Result tab)
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

// Status → display metadata. Single source of truth.
const STATUS_META = {
  inbox:       { label: 'Inbox',       short: 'INBOX',       icon: 'circle',      cls: 'inbox' },
  in_progress: { label: 'In Progress', short: 'IN PROGRESS', icon: 'dotSquare',   cls: 'progress' },
  done:        { label: 'Done',        short: 'DONE',        icon: 'checkSquare', cls: 'done' },
  partial:     { label: 'Partial',     short: 'PARTIAL',     icon: 'dotSquare',   cls: 'partial' },
  blocked:     { label: 'Blocked',     short: 'BLOCKED',     icon: 'x',           cls: 'blocked' },
  cancelled:   { label: 'Cancelled',   short: 'CANCELLED',   icon: 'x',           cls: 'cancelled' },
};
function statusMeta(s) {
  return STATUS_META[s] || { label: humanize(s || 'unknown'), short: 'UNKNOWN', icon: 'circle', cls: 'unknown' };
}

// Deterministic hash → slot index (so the same mission always lands on the
// same sticky color / rotation / corner shape).
function hashCode(str) {
  let h = 0;
  const s = String(str || '');
  for (let i = 0; i < s.length; i++) h = ((h << 5) - h + s.charCodeAt(i)) | 0;
  return Math.abs(h);
}

// Per-status palette + a single fallback for unknown buckets.
const STICKY_VARIANTS = {
  done:       ['done-1', 'done-2'],
  inbox:      ['inbox-1', 'inbox-2'],
  in_progress:['progress-1'],
  partial:    ['partial-1'],
  blocked:    ['blocked-1'],
  cancelled:  ['cancelled-1'],
};
function stickyVariant(item) {
  const list = STICKY_VARIANTS[item.status] || ['inbox-1'];
  return list[hashCode(item.id) % list.length];
}

// Picked from the pencil draft; positive = clockwise, used as inline
// transform so each sticky has the same lean across reloads.
const ROTATIONS = [-3, 2, -1, 1.5, -2, 2.5, -1.8, 1.2];
function stickyRotation(item) {
  return ROTATIONS[hashCode(item.id) % ROTATIONS.length];
}

// One of three corner-cut shapes — cheap way to break the grid uniformity.
const CORNER_SHAPES = ['corner-tl', 'corner-tr', 'corner-bl'];
function stickyCorner(item) {
  return CORNER_SHAPES[hashCode(item.id + 'c') % CORNER_SHAPES.length];
}

const ICONS_TAPE = `<svg viewBox="0 0 64 20" preserveAspectRatio="none" xmlns="http://www.w3.org/2000/svg"><rect width="64" height="20" rx="1" fill="rgba(255,250,224,0.55)" stroke="rgba(180,170,120,0.35)" stroke-width="1"/></svg>`;

export async function render(ctx) {
  const { viewEl, topbarEl, subbarEl, menu, params } = ctx;
  const index = await loadIndex('harness-missions');

  // The fridge is always the base layer. The modal lifts on top when params present.
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

  // No subbar in fridge mode — keep it hidden so the sticky surface gets the room.
  if (subbarEl) { subbarEl.hidden = true; subbarEl.replaceChildren(); }

  if (!items.length) {
    mount(viewEl, h('div', { class: 'fridge fridge-empty' }, [
      h('div', { class: 'fridge-empty-card' }, [
        h('div', { class: 'fridge-empty-title' }, `${index?.base || 'harness/missions'} — no missions yet`),
        h('div', { class: 'fridge-empty-sub' }, 'Add a file named M{NNNN}-{slug}.md under harness/missions/. The frontmatter status drives this view.'),
      ]),
    ]));
    closeModal();
    return;
  }

  const fridge = h('div', { class: 'fridge' });
  mount(viewEl, fridge);

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
    fridge.replaceChildren();

    // Magnet toolbar — pills sit on a translucent bar that reads like a
    // strip of magnets pinned to the top of the fridge.
    const bar = h('div', { class: 'fridge-toolbar' });
    bar.appendChild(magnet('All', counts.all || items.length, state.status === 'all', 'all',
      () => { state.status = 'all'; redraw(); }));
    for (const s of statuses) {
      const n = counts[s] || 0;
      if (!n && state.status !== s) continue;
      const meta = statusMeta(s);
      bar.appendChild(magnet(meta.label, n, state.status === s, meta.cls,
        () => { state.status = s; redraw(); }));
    }
    fridge.appendChild(bar);

    if (!filtered.length) {
      fridge.appendChild(h('div', { class: 'fridge-empty-card subtle' }, 'No missions match the current filter.'));
      return;
    }

    const board = h('div', { class: 'sticky-board' });
    for (const it of filtered) board.appendChild(renderSticky(it, menu));
    // Trailing hint slot — read-only handle for "add a new mission" since
    // creation is file-driven, not in-app.
    board.appendChild(renderAddHint(index?.base || 'harness/missions'));
    fridge.appendChild(board);
  }

  function magnet(label, count, active, cls, onclick) {
    return h('div', {
      class: 'magnet' + (active ? ' active' : '') + ' magnet-' + cls,
      onclick,
    }, `${label} · ${count}`);
  }

  redraw();

  // Drive the modal off route params. params can be "M0002" or "M0002/result".
  if (params) {
    openMissionModal(index, params, menu);
  } else {
    closeModal();
  }
}

/* ─── Sticky note ─────────────────────────────────────────────────────── */

function renderSticky(it, menu) {
  const meta = statusMeta(it.status);
  const variant = stickyVariant(it);
  const rotation = stickyRotation(it);
  const corner = stickyCorner(it);

  // Header row: ID badge + small status chip.
  const head = h('div', { class: 'sticky-head' }, [
    h('span', { class: 'sticky-id' }, it.id),
    h('span', { class: 'sticky-status sticky-status-' + meta.cls }, [
      meta.cls === 'progress' ? h('span', { class: 'sticky-status-pulse' }) : null,
      h('span', {}, meta.short),
    ]),
  ]);

  // Title — handwritten font.
  const title = h('div', { class: 'sticky-title' }, it.title || it.id);

  // Meta strip: operator, language, dates, finished marker.
  const metaBits = [];
  if (it.operator) metaBits.push('@' + it.operator);
  if (it.priority) metaBits.push('priority: ' + it.priority);
  if (it.created)  metaBits.push(it.created);
  const metaRow = metaBits.length
    ? h('div', { class: 'sticky-meta' }, metaBits.join('  ·  '))
    : null;

  // Result tag — "PRD only" if no execution log yet, "PRD + Result" otherwise.
  const tag = h('span', {
    class: 'sticky-tag ' + (it.recordFile ? 'tag-rec' : 'tag-prd'),
  }, it.recordFile ? 'PRD + Result' : 'PRD only');

  const finishedNote = it.recordFinished
    ? h('span', { class: 'sticky-tag tag-done' }, '✓ ' + String(it.recordFinished).slice(0, 10))
    : null;

  const tagRow = h('div', { class: 'sticky-tags' }, [tag, finishedNote]);

  // Tape — only on a few variants, decorative only.
  const tape = variant.startsWith('done') || variant.startsWith('inbox')
    ? h('span', { class: 'sticky-tape', html: ICONS_TAPE })
    : null;

  return h('div', {
    class: ['sticky', 'sticky-' + variant, 'sticky-' + corner, 'status-' + meta.cls].join(' '),
    style: { '--rot': rotation + 'deg' },
    onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`; },
    role: 'button',
    tabindex: '0',
    onkeydown: (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`;
      }
    },
  }, [tape, head, title, metaRow, tagRow]);
}

function renderAddHint(base) {
  return h('div', { class: 'sticky-add-slot', title: base }, [
    h('div', { class: 'sticky-add-icon', html: '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>' }),
    h('div', { class: 'sticky-add-text' }, 'Pin a new mission'),
    h('div', { class: 'sticky-add-hint' }, base + '/M{NNNN}-*.md'),
  ]);
}

/* ─── Detail modal ────────────────────────────────────────────────────── */

let escListener = null;

function closeModal() {
  const root = document.getElementById('modal-root');
  if (!root) return;
  root.hidden = true;
  root.replaceChildren();
  root.classList.remove('mission-modal-root');
  if (escListener) {
    window.removeEventListener('keydown', escListener);
    escListener = null;
  }
}

async function openMissionModal(index, params, menu) {
  const root = document.getElementById('modal-root');
  if (!root) return;

  const parts = String(params).split('/');
  const id = decodeURIComponent(parts[0] || '');
  const tab = parts[1] === 'result' ? 'result' : 'prd';

  const it = index?.items?.find(x => x.id === id);
  if (!it) {
    root.classList.add('mission-modal-root');
    root.hidden = false;
    mount(root, h('div', { class: 'mission-modal' }, [
      h('div', { class: 'mission-modal-head' }, [
        h('span', { class: 'sticky-id' }, id),
        h('span', { class: 'mission-modal-title' }, 'Mission not found in the index.'),
        modalCloseBtn(menu),
      ]),
      h('div', { class: 'mission-modal-body' }, emptyState(`Mission ${id} is not present in harness-missions.json — try rebuilding the harness-view indexes.`)),
    ]));
    bindClose(root, menu);
    return;
  }

  const sMeta = statusMeta(it.status);

  // Skeleton first so the user sees structure while the markdown fetches.
  const tabsEl = h('div', { class: 'mission-modal-tabs' });
  const bodyEl = h('div', { class: 'mission-modal-body' }, loadingState());

  const head = h('div', { class: 'mission-modal-head' }, [
    h('span', { class: 'sticky-id' }, it.id),
    h('span', { class: 'mission-modal-title' }, it.title || it.id),
    h('span', { class: 'mission-modal-status sticky-status sticky-status-' + sMeta.cls }, sMeta.label),
    modalCloseBtn(menu),
  ]);

  const sub = h('div', { class: 'mission-modal-sub' }, tab === 'result' ? (it.recordFile || '—') : it.file);

  const modal = h('div', { class: 'mission-modal' }, [head, sub, tabsEl, bodyEl]);

  root.classList.add('mission-modal-root');
  root.hidden = false;
  mount(root, modal);
  bindClose(root, menu);

  // Tabs
  const tabDefs = [
    {
      id: 'prd',
      label: 'PRD (Request)',
      active: tab === 'prd',
      onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`; },
    },
    {
      id: 'result',
      label: it.recordFile ? 'Result (Execution Log)' : 'Result (none yet)',
      active: tab === 'result',
      disabled: !it.recordFile,
      onclick: () => {
        if (!it.recordFile) return;
        location.hash = `#${menu.id}/${encodeURIComponent(it.id)}/result`;
      },
    },
  ];
  tabsEl.replaceChildren(...tabDefs.map(t => h('div', {
    class: 'mission-modal-tab' + (t.active ? ' active' : '') + (t.disabled ? ' disabled' : ''),
    onclick: t.disabled ? null : t.onclick,
  }, t.label)));

  // Content
  const targetPath = tab === 'result' ? it.recordFile : it.file;
  if (!targetPath) {
    bodyEl.replaceChildren(emptyState('No execution log yet — only the PRD has been written.'));
    return;
  }
  const content = await loadMd(targetPath);
  if (content == null) {
    bodyEl.replaceChildren(emptyState('Could not load file: ' + targetPath));
    return;
  }

  // Split frontmatter → structured strip; pass only the body to the MD viewer.
  const { meta, body } = parseFrontmatter(content);
  const wrap = h('div');
  const card = createMissionCard(meta, { kind: tab, fallbackId: it.id });
  if (card) wrap.appendChild(card);
  wrap.appendChild(createMdViewer({
    content: body || content,
    readOnly: true,
    breadcrumb: targetPath.split('/'),
  }));
  bodyEl.replaceChildren(wrap);
}

function modalCloseBtn(menu) {
  return h('button', {
    class: 'mission-modal-close',
    'aria-label': 'Close',
    onclick: () => { location.hash = `#${menu.id}`; },
    html: ICONS.x,
  });
}

function bindClose(root, menu) {
  // Backdrop click closes; clicks inside the card don't bubble to backdrop.
  root.onclick = (e) => {
    if (e.target === root) location.hash = `#${menu.id}`;
  };
  if (escListener) window.removeEventListener('keydown', escListener);
  escListener = (e) => {
    if (e.key === 'Escape') location.hash = `#${menu.id}`;
  };
  window.addEventListener('keydown', escListener);
}

/* ─── Mission frontmatter → structured strip inside modal body ────────── */

function createMissionCard(meta, opts = {}) {
  if (!meta || typeof meta !== 'object' || !Object.keys(meta).length) return null;

  const card = h('div', { class: 'mission-detail-strip' });

  const stripBits = [];
  if (meta.operator) stripBits.push(`@${meta.operator}`);
  if (meta.language) stripBits.push(meta.language);
  if (meta.priority) stripBits.push(`priority: ${meta.priority}`);
  if (meta.created)  stripBits.push(`created: ${meta.created}`);
  if (stripBits.length) {
    card.appendChild(h('div', { class: 'mission-detail-row' }, stripBits.join('  ·  ')));
  }

  if (meta.type) {
    card.appendChild(row('Type', meta.type));
  }

  const related = parseListLike(meta.related);
  if (related.length) {
    card.appendChild(chipRow('Related · ' + related.length, related));
  }

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

  return card.children.length ? card : null;
}

function row(label, value) {
  return h('div', { class: 'mission-detail-kv' }, [
    h('div', { class: 'mission-detail-key' }, label),
    h('div', { class: 'mission-detail-value' }, String(value)),
  ]);
}

function chipRow(label, items) {
  const chips = h('div', { class: 'mission-detail-chips' });
  for (const it of items) {
    chips.appendChild(h('span', { class: 'mission-detail-chip', title: it }, it));
  }
  return h('div', { class: 'mission-detail-kv' }, [
    h('div', { class: 'mission-detail-key' }, label),
    chips,
  ]);
}

function artifactRow(items) {
  const list = h('div', { class: 'mission-detail-artifacts' });
  for (const p of items) {
    list.appendChild(h('div', { class: 'mission-detail-artifact' }, [
      h('span', { class: 'mission-detail-chip', html: ICONS.file, style: { padding: '2px 4px' } }),
      h('code', {}, p),
    ]));
  }
  return h('div', { class: 'mission-detail-kv' }, [
    h('div', { class: 'mission-detail-key' }, `Artifacts · ${items.length}`),
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
