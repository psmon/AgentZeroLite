/**
 * 워크플로우 — 사전구현 (data/workflow-graph.json) + mermaid 렌더
 *  - 좌측: 워크플로우 목록 (card)
 *  - 우측: 선택된 mermaid 그래프 + 원본 MD 링크
 *  - 노드/그래프 클릭 시 MD 문서(harness/engine/*.md) 진입
 */
import { h, mount } from '../utils/dom.js';
import { loadData, loadMd } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;
  const data = await loadData('workflow-graph');
  if (params) return renderDetail(ctx, data, params);

  renderTopBar(topbarEl, {
    title: 'Workflow',
    subtitle: 'AgentZero Lite engine workflows (pre-built diagrams)',
  });

  if (!data || !data.workflows?.length) {
    mount(viewEl, emptyState('data/workflow-graph.json is missing.'));
    return;
  }

  const state = { current: data.workflows[0].id };
  const cont = h('div', { style: { display: 'grid', gridTemplateColumns: '260px 1fr', gap: '16px', alignItems: 'start' } });
  const listPane = h('div', { class: 'tree' });
  const detailPane = h('div');
  cont.append(listPane, detailPane);
  mount(viewEl, cont);

  function drawList() {
    listPane.replaceChildren(...data.workflows.map(wf => h('div', {
      class: 'tree-node' + (state.current === wf.id ? ' active' : ''),
      onclick: () => { state.current = wf.id; drawList(); drawDetail(); },
    }, [
      h('span', { html: ICONS.branch }),
      h('span', {}, wf.label),
    ])));
  }

  function drawDetail() {
    const wf = data.workflows.find(w => w.id === state.current);
    if (!wf) { mount(detailPane, emptyState('Workflow not found.')); return; }
    const open = h('button', {
      class: 'btn',
      onclick: () => { location.hash = `#workflow/${encodeURIComponent(wf.file)}`; },
    }, 'Open source .md →');
    const box = h('div', { class: 'md-viewer' }, [
      h('h2', {}, wf.label),
      h('p', {}, wf.description),
      h('div', { class: 'mermaid', html: wf.mermaid }),
      h('div', { style: { marginTop: '16px' } }, [open]),
    ]);
    mount(detailPane, box);
    if (window.mermaid) {
      mermaid.run({ nodes: box.querySelectorAll('.mermaid') }).catch(e => console.warn('mermaid', e));
    }
  }

  drawList();
  drawDetail();
}

async function renderDetail(ctx, data, filepath) {
  const { viewEl, topbarEl } = ctx;
  const file = decodeURIComponent(filepath);
  const wf = data?.workflows.find(w => w.file === file);

  renderTopBar(topbarEl, {
    title: wf?.label || file,
    subtitle: file,
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#workflow'; } }, '← Back to list'),
  });

  mount(viewEl, loadingState());
  const content = await loadMd(file);
  if (content == null) { mount(viewEl, emptyState('Could not load file.')); return; }
  mount(viewEl, createMdViewer({ content, readOnly: true, breadcrumb: file.split('/') }));
}
