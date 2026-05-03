/**
 * Skills — `.claude/skills/<skill>/SKILL.md` card view (in-repo plugins)
 * plus hardcoded external entries (e.g. harness-kakashi-creator) that
 * open the upstream URL on click instead of routing to a detail page.
 * Resource-Reference mode (read-only).
 *
 * Detail page parses Skills 2.0 frontmatter (name / description / allowed-tools
 * / triggers / etc.) and renders it as a structured spec card above the markdown
 * body. Same shared component as the Roles view.
 */
import { h, mount } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { createSpecCard, parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;
  const index = await loadIndex('claude-skills');
  if (params) return renderDetail(ctx, index, params);

  renderTopBar(topbarEl, {
    title: 'Skills',
    subtitle: `${index?.base || '.claude/skills'} — ${index?.items?.length || 0} available skills (read-only)`,
    badge: { kind: 'readonly', text: 'Reference' },
    search: { placeholder: 'Search skills...', oninput: e => applyFilter(e.target.value) },
  });

  if (!index || !index.items?.length) {
    mount(viewEl, emptyState('No skills found under .claude/skills/.'));
    return;
  }

  const grid = h('div', { class: 'card-grid' });
  function applyFilter(q) {
    q = (q || '').toLowerCase();
    grid.childNodes.forEach(card => {
      card.style.display = (card.dataset.search || '').includes(q) ? '' : 'none';
    });
  }

  for (const it of index.items) {
    const isExternal = !!it.external;
    const card = h('div', {
      class: 'card' + (isExternal ? ' card-external' : ''),
      dataset: { search: (it.name + ' ' + (it.description || '') + ' ' + (it.source || '')).toLowerCase() },
      onclick: isExternal
        ? () => { window.open(it.url, '_blank', 'noopener,noreferrer'); }
        : () => { location.hash = `#${menu.id}/${encodeURIComponent(it.id)}`; },
    }, [
      h('span', { class: 'card-icon', html: ICONS.zap }),
      h('div', { class: 'card-title' }, [
        h('span', {}, it.name),
        isExternal ? h('span', {
          style: { marginLeft: '6px', fontSize: '11px', color: '#6B7280' },
          title: 'External — opens in a new tab',
        }, '↗') : null,
      ].filter(Boolean)),
      h('div', { class: 'card-desc' },
        isExternal
          ? (it.description || it.source || it.url)
          : `${it.file}`),
      h('div', { class: 'card-meta' },
        isExternal
          ? `external · ${it.source || 'upstream'}`
          : `${it.modified}`),
    ]);
    grid.appendChild(card);
  }

  mount(viewEl, grid);
}

async function renderDetail(ctx, index, skillId) {
  const { viewEl, topbarEl } = ctx;
  const id = decodeURIComponent(skillId);
  const item = index?.items?.find(i => i.id === id);
  if (!item) { mount(viewEl, emptyState('Skill not found.')); return; }

  // External skill deep-link landed here (e.g. user typed the hash). The
  // SKILL.md body isn't in this repo — bounce them to the upstream URL.
  if (item.external && item.url) {
    window.open(item.url, '_blank', 'noopener,noreferrer');
    location.hash = '#skill';
    return;
  }

  renderTopBar(topbarEl, {
    title: item.name,
    subtitle: item.file,
    badge: { kind: 'readonly', text: 'Reference' },
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#skill'; } }, '← Back to list'),
  });

  mount(viewEl, loadingState());
  // Skills index ships SKILL.md body inline (build-time embed). Fall back to
  // loadMd only if a future entry omits content for any reason.
  const raw = item.content ?? await loadMd(item.file);
  if (raw == null) { mount(viewEl, emptyState('Could not load file.')); return; }

  const { meta, body } = parseFrontmatter(raw);
  const screen = h('div', { class: 'md-screen' });
  const card = createSpecCard(meta, { tag: 'SKILLS 2.0', fallbackName: item.name });
  if (card) screen.appendChild(card);
  screen.appendChild(createMdViewer({
    content: body,
    readOnly: true,
    breadcrumb: item.file.split('/'),
  }));
  mount(viewEl, screen);
}
