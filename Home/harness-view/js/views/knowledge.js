/**
 * Knowledge — two tabs, both **hierarchy trees** with right-side MD preview.
 *
 *   Expert (`harness/knowledge/**`)         per-agent subdirs:
 *     _shared/, tamer/, code-coach/, security-guard/, test-runner/,
 *     test-sentinel/. Each agent owns the docs under its subdir.
 *   Tech / Domain (`Docs/**`)               full project documentation tree.
 *
 * Card-grid was retired (2026-05-04 with the per-agent reorg) — the tree
 * is the better fit once knowledge has hierarchy. Both tabs share the same
 * left-tree / right-preview component so navigation feels consistent.
 *
 * URL params:
 *   #knowledge                    → defaults to expert tab, no file
 *   #knowledge/expert             → expert tab, no file selected
 *   #knowledge/expert/<rel>       → expert tab, file open (rel = path under harness/knowledge/)
 *   #knowledge/domain             → domain tab
 *   #knowledge/domain/<rel>       → domain tab, file open (rel = path under Docs/)
 *
 * Legacy `#knowledge/knowledge/<full-path>` (flat-card era) is mapped to
 * the expert tab + corresponding rel path.
 */
import { h, mount } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, renderSubBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, subbarEl, params } = ctx;

  const [tabRaw = 'expert', ...rest] = (params || '').split('/');
  let tab = tabRaw;
  let filePath = rest.length ? rest.join('/') : null;

  // Migrate legacy `#knowledge/knowledge/<full-path>` URLs (cards era).
  if (tab === 'knowledge') {
    tab = 'expert';
    if (filePath) filePath = filePath.replace(/^harness\/knowledge\//, '');
  }

  const [kIdx, dIdx] = await Promise.all([
    loadIndex('harness-knowledge'),
    loadIndex('document-tree'),
  ]);

  renderTopBar(topbarEl, {
    title: 'Knowledge',
    subtitle: 'Expert (per-agent) · Tech / Domain — both as hierarchy trees',
    badge: { kind: 'edit', text: 'Read + Edit' },
  });

  renderSubBar(subbarEl, [
    {
      label: 'Expert Knowledge',
      count: countTree(kIdx?.tree || []),
      active: tab === 'expert',
      onclick: () => { location.hash = '#knowledge/expert'; },
    },
    {
      label: 'Tech / Domain',
      count: countTree(dIdx?.tree || []),
      active: tab === 'domain',
      onclick: () => { location.hash = '#knowledge/domain'; },
    },
  ]);

  if (tab === 'expert') {
    return renderTreeAndPreview(ctx, {
      tabId: 'expert',
      base: kIdx?.base || 'harness/knowledge',
      tree: kIdx?.tree || [],
      currentRel: filePath,
      emptyMessage: 'No knowledge docs yet — add .md files under harness/knowledge/<agent>/.',
    });
  }

  return renderTreeAndPreview(ctx, {
    tabId: 'domain',
    base: dIdx?.base || 'Docs',
    tree: dIdx?.tree || [],
    currentRel: filePath,
    emptyMessage: 'No domain docs yet — add .md files under Docs/.',
  });
}

function countTree(tree) {
  let n = 0;
  (function walk(nodes) {
    for (const x of nodes) {
      if (x.type === 'file') n++;
      else if (x.children) walk(x.children);
    }
  })(tree);
  return n;
}

/* ─── Shared tree + preview component ─────────────────────────────────── */
function renderTreeAndPreview(ctx, { tabId, base, tree, currentRel, emptyMessage }) {
  const { viewEl } = ctx;

  if (!tree.length) {
    mount(viewEl, emptyState(emptyMessage));
    return;
  }

  const state = { current: currentRel };

  const container = h('div', { class: 'split' });
  const lp = h('div', { class: 'pane pane-l' });
  const rp = h('div', { class: 'pane pane-r' });
  container.append(lp, rp);
  mount(viewEl, container);

  // Path under base (no leading base/) — the value the URL fragment carries.
  const relOf = (node) => node.path;

  function treeNode(node, depth = 0) {
    if (node.type === 'dir') {
      const openState = { open: depth < 2 };
      const wrapper = h('div');
      const head = h('div', {
        class: 'tree-node',
        style: { paddingLeft: `${8 + depth * 14}px` },
        onclick: () => { openState.open = !openState.open; redraw(); },
      }, [
        h('span', { class: 'chev' }, openState.open ? '▾' : '▸'),
        h('span', { class: 'fico', html: ICONS.folder }),
        h('span', {}, node.name),
        h('span', { class: 'cnt' }, String(countInNode(node))),
      ]);
      const childBox = h('div');
      function redraw() {
        wrapper.replaceChildren(head);
        head.querySelector('.chev').textContent = openState.open ? '▾' : '▸';
        if (openState.open) {
          childBox.replaceChildren();
          for (const c of node.children || []) childBox.appendChild(treeNode(c, depth + 1));
          wrapper.appendChild(childBox);
        }
      }
      redraw();
      return wrapper;
    }
    return h('div', {
      class: 'tree-node leaf' + (state.current === relOf(node) ? ' active' : ''),
      style: { paddingLeft: `${22 + depth * 14}px` },
      onclick: () => { location.hash = `#knowledge/${tabId}/${encodeURIComponent(relOf(node))}`; },
    }, [
      h('span', { html: ICONS.file }),
      h('span', {}, node.name),
    ]);
  }

  function countInNode(node) {
    let n = 0;
    (function walk(list) {
      for (const x of list) {
        if (x.type === 'file') n++;
        else if (x.children) walk(x.children);
      }
    })(node.children || []);
    return n;
  }

  // Left: search + tree
  const treeBox = h('div', { class: 'tree' });
  for (const n of tree) treeBox.appendChild(treeNode(n));

  const searchBox = h('div', {
    style: {
      display: 'flex', gap: '6px', alignItems: 'center',
      padding: '8px 10px', marginBottom: '8px',
      background: '#F9FAFB', border: '1px solid #E5E7EB', borderRadius: '6px',
    },
  }, [
    h('span', { html: ICONS.search }),
    h('input', {
      type: 'search',
      placeholder: 'Search path',
      style: { border: 'none', outline: 'none', background: 'transparent', fontSize: '12px', flex: 1 },
      oninput: (e) => filterTree(e.target.value),
    }),
  ]);

  function filterTree(q) {
    q = (q || '').toLowerCase();
    treeBox.querySelectorAll('.tree-node').forEach((node) => {
      const t = node.textContent.toLowerCase();
      node.style.display = !q || t.includes(q) ? '' : 'none';
    });
  }

  mount(lp, searchBox, treeBox);

  // Right: selected MD preview (or hint)
  async function renderRight() {
    if (!state.current) {
      mount(rp, h('div', { class: 'empty' }, 'Select a file from the tree on the left.'));
      return;
    }
    mount(rp, loadingState());
    const fullPath = `${base}/${state.current}`;
    const content = await loadMd(fullPath);
    if (content == null) {
      mount(rp, emptyState(`Could not load ${fullPath}.`));
      return;
    }
    mount(rp, createMdViewer({
      content,
      breadcrumb: fullPath.split('/'),
      onSave: async (newContent) => {
        sessionStorage.setItem(`md:${fullPath}`, newContent);
      },
    }));
  }

  renderRight();
}
