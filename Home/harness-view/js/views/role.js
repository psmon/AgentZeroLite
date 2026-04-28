/**
 * Roles — card grid of harness/agents/*.md
 * Resource-Reference mode (read-only).
 *
 * Detail view extracts YAML frontmatter (name / persona / triggers /
 * description / etc.) and renders it as a structured spec card above the
 * markdown body. Same pattern used by Skills.
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { createSpecCard, parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;
  const index = await loadIndex('harness-agents');
  if (params) return renderDetail(ctx, index, params);

  renderTopBar(topbarEl, {
    title: 'Roles',
    subtitle: `${index?.base || 'Docs/harness/template'} — agent role definitions (read-only)`,
    badge: { kind: 'readonly', text: 'Reference' },
  });

  if (!index || !index.items?.length) {
    const path = (index?.base || 'Docs/harness/template').replace(/^Docs\//, '');
    mount(viewEl, h('div', { class: 'empty' }, [
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, `${path} — no entries yet`),
      h('div', { style: { color: '#6B7280' } }, 'Add .md files under any agents/ folder anywhere in the template tree.'),
    ]));
    return;
  }

  const grid = h('div', { class: 'card-grid' });
  for (const it of index.items) {
    const card = h('div', {
      class: 'card',
      onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.file)}`; },
    }, [
      h('span', { class: 'card-icon', html: ICONS.users }),
      h('div', { class: 'card-title' }, humanize(it.title)),
      h('div', { class: 'card-desc' }, `${it.modified}`),
    ]);
    grid.appendChild(card);
  }
  mount(viewEl, grid);
}

async function renderDetail(ctx, index, filename) {
  const { viewEl, topbarEl } = ctx;
  const fullPath = decodeURIComponent(filename);   // ROOT-relative, e.g. Docs/harness/template/.../tamer.md
  const baseName = fullPath.split('/').pop();
  const base = index?.base || 'Docs/harness/template';

  renderTopBar(topbarEl, {
    title: humanize(baseName),
    subtitle: fullPath,
    badge: { kind: 'readonly', text: 'Reference' },
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#role'; } }, '← Back to list'),
  });

  mount(viewEl, loadingState());
  const raw = await loadMd(fullPath);
  if (raw == null) { mount(viewEl, emptyState('Could not load file.')); return; }

  const { meta, body } = parseFrontmatter(raw);
  const screen = h('div', { class: 'md-screen' });
  const card = createSpecCard(meta, { tag: 'ROLE', fallbackName: baseName });
  if (card) screen.appendChild(card);
  screen.appendChild(createMdViewer({
    content: body,
    readOnly: true,
    breadcrumb: fullPath.split('/'),
  }));
  mount(viewEl, screen);
}
