/**
 * Activity Log — harness/logs/<subfolder>/*.md
 * Subfolder name = category tag (discovered dynamically at build time).
 * Filename pattern: YYYY-MM-DD-HHMM-title.md or YYYY-MM-DD-HH-MM-title.md
 *
 * Bulletin-board style (newest first). Category pills filter by tag.
 * Resource-Reference mode (read-only).
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';

const PAGE_SIZE = 20;

/** "code-coach" → "Code Coach", "tamer" → "Tamer" */
function tagLabel(tag) {
  return humanize ? humanize(tag) : tag.split('-').map(s => s ? s[0].toUpperCase() + s.slice(1) : '').join(' ');
}

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;
  const index = await loadIndex('harness-logs');
  if (params) return renderDetail(ctx, index, params);

  const cats = Array.isArray(index?.categories) ? index.categories : [];

  const state = { category: 'all', query: '', page: 1 };

  renderTopBar(topbarEl, {
    title: 'Activity Log',
    subtitle: `${index?.base || 'harness/logs'} — entries grouped by subfolder tag (newest first)`,
    badge: { kind: 'readonly', text: 'Reference' },
    search: { placeholder: 'Search date or title...', oninput: e => { state.query = e.target.value; redraw(); } },
  });

  if (!index || !index.items?.length) {
    const path = index?.base || 'harness/logs';
    mount(viewEl, h('div', { class: 'empty' }, [
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, `${path} — no entries yet`),
      h('div', { style: { color: '#6B7280' } }, 'Add YYYY-MM-DD-HHMM-title.md under any subfolder of harness/logs/. The subfolder name becomes the entry tag.'),
    ]));
    return;
  }

  const container = h('div');
  mount(viewEl, container);

  function filter() {
    const q = state.query.toLowerCase();
    return index.items.filter(it =>
      (state.category === 'all' || it.category === state.category) &&
      ((it.title || '').toLowerCase().includes(q) || (it.date || '').includes(q))
    );
  }

  function redraw() {
    const filtered = filter();
    const pages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
    if (state.page > pages) state.page = pages;
    const slice = filtered.slice((state.page - 1) * PAGE_SIZE, state.page * PAGE_SIZE);

    const pills = h('div', { class: 'pill-bar' });
    const total = index.items.length;
    pills.appendChild(pill('All', total, state.category === 'all', () => { state.category = 'all'; state.page = 1; redraw(); }));
    for (const c of cats) {
      pills.appendChild(pill(tagLabel(c), index.counts?.[c] || 0, state.category === c, () => { state.category = c; state.page = 1; redraw(); }, 'cat-' + c));
    }

    const board = h('div', { class: 'board' }, [
      h('div', { class: 'board-head' }, [
        h('div', {}, 'Date'),
        h('div', {}, 'Title'),
        h('div', {}, 'Tag'),
      ]),
      ...slice.map(it => h('div', {
        class: 'board-row',
        onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.file)}`; },
      }, [
        h('div', { class: 'date' }, `${it.date || ''}${it.time ? ' ' + it.time : ''}`),
        h('div', { class: 'title' }, it.title || it.file),
        h('div', { class: 'board-cat cat-' + it.category }, tagLabel(it.category)),
      ])),
    ]);

    const pag = h('div', { class: 'pagination' });
    pag.appendChild(h('span', { class: 'page', onclick: () => { if (state.page > 1) { state.page--; redraw(); } } }, '‹'));
    for (let i = 1; i <= pages; i++) {
      if (i === 1 || i === pages || Math.abs(i - state.page) <= 1) {
        pag.appendChild(h('span', { class: 'page' + (i === state.page ? ' active' : ''), onclick: () => { state.page = i; redraw(); } }, String(i)));
      } else if (i === 2 || i === pages - 1) {
        pag.appendChild(h('span', { class: 'page' }, '…'));
      }
    }
    pag.appendChild(h('span', { class: 'page', onclick: () => { if (state.page < pages) { state.page++; redraw(); } } }, '›'));

    mount(container, pills, board, pag);
  }

  function pill(label, count, active, onclick, extraCls = '') {
    return h('div', {
      class: 'pill' + (active ? ' active' : '') + (active ? '' : ' ' + extraCls),
      onclick,
    }, `${label} · ${count}`);
  }

  redraw();
}

async function renderDetail(ctx, index, filePath) {
  const { viewEl, topbarEl } = ctx;
  const rel = decodeURIComponent(filePath);
  const item = index?.items?.find(i => i.file === rel);

  renderTopBar(topbarEl, {
    title: item?.title || rel,
    subtitle: rel,
    badge: { kind: 'readonly', text: 'Reference' },
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#usage-log'; } }, '← Back to board'),
  });

  mount(viewEl, loadingState());
  const content = await loadMd(rel);
  if (content == null) { mount(viewEl, emptyState('Could not load file.')); return; }
  mount(viewEl, createMdViewer({ content, readOnly: true, breadcrumb: rel.split('/') }));
}
