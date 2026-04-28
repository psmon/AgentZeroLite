/**
 * 지식 — 탭 2개 + 편집 가능 MD 뷰
 *  - 전문가 지식: harness/knowledge (카드뷰)
 *  - 도메인 지식: document/** (계층 트리 + preview)  (리소스참고)
 *
 * 클릭 후 MD 는 Read+Edit 모드. 저장은 브라우저 로컬 상태에서만 동작(저장 버튼이 서버 API
 * 없이 동작하려면 개발 서버 필요). 기본적으로 onSave 핸들러가 없으면 편집만 로컬에 머문다.
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, renderSubBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, subbarEl, menu, params } = ctx;

  // params 형태:  ""  /  "knowledge/<filename>"  /  "domain/<relpath>"
  const [tab = 'knowledge', ...rest] = (params || '').split('/');
  const filePath = rest.length ? rest.join('/') : null;

  const kIdx = await loadIndex('harness-knowledge');
  const dIdx = await loadIndex('document-tree');

  renderTopBar(topbarEl, {
    title: 'Knowledge',
    subtitle: 'Expert Knowledge · Tech / Domain Knowledge (read + edit)',
    badge: { kind: 'edit', text: 'Read + Edit' },
  });

  renderSubBar(subbarEl, [
    { label: 'Expert Knowledge', count: kIdx?.items?.length || 0, active: tab === 'knowledge', onclick: () => { location.hash = '#knowledge/knowledge'; } },
    { label: 'Tech / Domain', count: countFiles(dIdx?.tree || []), active: tab === 'domain', onclick: () => { location.hash = '#knowledge/domain'; } },
  ]);

  if (tab === 'knowledge') {
    if (filePath) return renderKnowledgeDetail(ctx, kIdx, filePath);
    return renderKnowledgeList(ctx, kIdx);
  }
  return renderDomain(ctx, dIdx, filePath);
}

function countFiles(tree) {
  let n = 0;
  (function walk(nodes) { for (const x of nodes) { if (x.type === 'file') n++; else if (x.children) walk(x.children); } })(tree);
  return n;
}

/* ─── Expert Knowledge — card grid ─── */
function renderKnowledgeList(ctx, index) {
  const { viewEl } = ctx;
  if (!index || !index.items?.length) {
    const path = (index?.base || 'Docs/harness/knowledge').replace(/^Docs\//, '');
    mount(viewEl, h('div', { class: 'empty' }, [
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, `${path} — no entries yet`),
      h('div', { style: { color: '#6B7280' } }, 'Add .md files under this folder to populate Expert Knowledge cards.'),
    ]));
    return;
  }
  const grid = h('div', { class: 'card-grid' });
  for (const it of index.items) {
    grid.appendChild(h('div', {
      class: 'card',
      onclick: () => { location.hash = `#knowledge/knowledge/${encodeURIComponent(it.file)}`; },
    }, [
      h('span', { class: 'card-icon', html: ICONS.bulb }),
      h('div', { class: 'card-title' }, humanize(it.title)),
      h('div', { class: 'card-desc' }, `${it.modified}`),
    ]));
  }
  mount(viewEl, grid);
}

async function renderKnowledgeDetail(ctx, index, filename) {
  const { viewEl } = ctx;
  const fullPath = decodeURIComponent(filename);   // ROOT-relative
  mount(viewEl, loadingState());
  const content = await loadMd(fullPath);
  if (content == null) { mount(viewEl, emptyState('Could not load file.')); return; }
  const back = h('button', { class: 'btn', style: { marginBottom: '12px' }, onclick: () => { location.hash = '#knowledge/knowledge'; } }, '← Back to Expert Knowledge');
  mount(viewEl, back, createMdViewer({
    content,
    breadcrumb: fullPath.split('/'),
    onSave: async (newContent) => {
      // Local-only save (no server API). Persisted in sessionStorage.
      sessionStorage.setItem(`md:${fullPath}`, newContent);
    },
  }));
}

/* ─── 도메인 지식 — 계층 트리 + preview ─── */
function renderDomain(ctx, dIdx, filePath) {
  const { viewEl } = ctx;
  if (!dIdx || !dIdx.tree) { mount(viewEl, emptyState('No Tech / Domain index. Add .md files under Docs/.')); return; }

  const state = { current: filePath };

  const container = h('div', { class: 'split' });
  const lp = h('div', { class: 'pane pane-l' });
  const rp = h('div', { class: 'pane pane-r' });
  container.append(lp, rp);
  mount(viewEl, container);

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
    // file
    return h('div', {
      class: 'tree-node leaf' + (state.current === node.path ? ' active' : ''),
      style: { paddingLeft: `${22 + depth * 14}px` },
      onclick: () => { location.hash = `#knowledge/domain/${encodeURIComponent(node.path)}`; },
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

  // 좌측: 검색 + 트리
  const searchBox = h('div', {
    style: { display: 'flex', gap: '6px', alignItems: 'center', padding: '8px 10px', marginBottom: '8px', background: '#F9FAFB', border: '1px solid #E5E7EB', borderRadius: '6px' },
  }, [
    h('span', { html: ICONS.search }),
    h('input', {
      type: 'search',
      placeholder: 'Search path',
      style: { border: 'none', outline: 'none', background: 'transparent', fontSize: '12px', flex: 1 },
      oninput: e => filterTree(e.target.value),
    }),
  ]);
  const tree = h('div', { class: 'tree' });
  for (const n of dIdx.tree) tree.appendChild(treeNode(n));
  mount(lp, searchBox, tree);

  function filterTree(q) {
    q = (q || '').toLowerCase();
    tree.querySelectorAll('.tree-node').forEach(node => {
      const t = node.textContent.toLowerCase();
      node.style.display = !q || t.includes(q) ? '' : 'none';
    });
  }

  // 우측: 선택된 md preview
  async function renderRight() {
    if (!state.current) { mount(rp, h('div', { class: 'empty' }, 'Select a file from the tree on the left.')); return; }
    mount(rp, loadingState());
    const content = await loadMd(`${dIdx.base}/${state.current}`);
    if (content == null) { mount(rp, emptyState('Could not load file.')); return; }
    mount(rp, createMdViewer({
      content,
      breadcrumb: [dIdx.base || 'Docs', ...state.current.split('/')],
      onSave: async (newContent) => {
        sessionStorage.setItem(`md:${dIdx.base}/${state.current}`, newContent);
      },
    }));
  }

  renderRight();
}
