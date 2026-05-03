/**
 * Workflow — split into two tabs:
 *
 *   Core (default) — application-level dev architecture flows, sourced
 *     from the static `data/workflow-graph.json` (multi-CLI routing,
 *     AIMODE handshake, tool-call loop, provider switch, etc.). These
 *     describe how the *running app* operates.
 *
 *   Task (orchestration) — harness/engine/*.md entries, sourced from the
 *     `indexes/harness-engine.json` manifest. These describe how the
 *     *harness orchestrates work* — pre-commit review, release-build
 *     pipeline, mission dispatch, harness-view publish — workflows that
 *     coordinate agents, not application components.
 *
 * Detail view (deep link `#workflow/<path>`) loads the source .md from
 * either tab's items via the shared MD viewer.
 */
import { h, mount } from '../utils/dom.js';
import { loadData, loadMd, loadIndex } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

const TABS = {
  core: { label: 'Core',
          subtitle: 'AgentZero Lite engine workflows (pre-built diagrams)' },
  task: { label: 'Task (Orchestration)',
          subtitle: 'Harness orchestrations — how work is coordinated across agents' },
};

const STATE = { tab: 'core', current: { core: null, task: null } };

export async function render(ctx) {
  const { viewEl, topbarEl, params } = ctx;

  // Both datasets resolved in parallel so tab switching feels instant.
  const [coreData, engineIdx] = await Promise.all([
    loadData('workflow-graph'),
    loadIndex('harness-engine'),
  ]);

  if (params) return renderDetail(ctx, coreData, engineIdx, params);

  if (STATE.current.core == null && coreData?.workflows?.length)
    STATE.current.core = coreData.workflows[0].id;
  if (STATE.current.task == null && engineIdx?.items?.length)
    STATE.current.task = engineIdx.items[0].file;

  drawTabs();

  function drawTabs() {
    renderTopBar(topbarEl, {
      title: 'Workflow',
      subtitle: TABS[STATE.tab].subtitle,
      extra: h('div', { class: 'toggle' },
        Object.entries(TABS).map(([id, t]) => h('button', {
          class: STATE.tab === id ? 'active' : '',
          onclick: () => { STATE.tab = id; drawTabs(); drawBody(); },
        }, t.label))),
    });
    drawBody();
  }

  function drawBody() {
    if (STATE.tab === 'core') return drawCore(viewEl, coreData);
    return drawTask(viewEl, engineIdx);
  }
}

// ─── Core tab — static workflow-graph.json ───────────────────────────────
function drawCore(viewEl, data) {
  if (!data?.workflows?.length) {
    mount(viewEl, emptyState('data/workflow-graph.json is missing.'));
    return;
  }

  const cont = h('div', { style: { display: 'grid', gridTemplateColumns: '260px 1fr', gap: '16px', alignItems: 'start' } });
  const listPane = h('div', { class: 'tree' });
  const detailPane = h('div');
  cont.append(listPane, detailPane);
  mount(viewEl, cont);

  const drawList = () => listPane.replaceChildren(...data.workflows.map(wf => h('div', {
    class: 'tree-node' + (STATE.current.core === wf.id ? ' active' : ''),
    onclick: () => { STATE.current.core = wf.id; drawList(); drawDetail(); },
  }, [
    h('span', { html: ICONS.branch }),
    h('span', {}, wf.label),
  ])));

  const drawDetail = () => {
    const wf = data.workflows.find(w => w.id === STATE.current.core);
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
  };

  drawList();
  drawDetail();
}

// ─── Task tab — engine orchestration manifests ──────────────────────────
function drawTask(viewEl, engineIdx) {
  if (!engineIdx?.items?.length) {
    mount(viewEl, emptyState('No engine entries found at harness/engine/. Add an orchestration MD with frontmatter (name / agents / triggers / description) to populate this tab.'));
    return;
  }

  const cont = h('div', { style: { display: 'grid', gridTemplateColumns: '260px 1fr', gap: '16px', alignItems: 'start' } });
  const listPane = h('div', { class: 'tree' });
  const detailPane = h('div');
  cont.append(listPane, detailPane);
  mount(viewEl, cont);

  const drawList = () => listPane.replaceChildren(...engineIdx.items.map(e => h('div', {
    class: 'tree-node' + (STATE.current.task === e.file ? ' active' : ''),
    onclick: () => { STATE.current.task = e.file; drawList(); drawDetail(); },
  }, [
    h('span', { html: ICONS.branch }),
    h('span', {}, e.title || e.file.replace(/\.md$/, '')),
  ])));

  const drawDetail = async () => {
    const item = engineIdx.items.find(e => e.file === STATE.current.task);
    if (!item) { mount(detailPane, emptyState('Engine not found.')); return; }

    mount(detailPane, loadingState());
    const path = `${engineIdx.base.replace(/\/?$/, '/')}${item.file}`;       // e.g. "harness/engine/release-build-pipeline.md"
    const raw = await loadMd(path);
    if (raw == null) {
      mount(detailPane, emptyState(`Could not load ${path}.`));
      return;
    }

    // Engine MDs lead with YAML frontmatter (name / agents / triggers /
    // description / auto_invoke_on). Parse it out so the MD viewer doesn't
    // render the YAML as a stray bulleted list, and surface the bits that
    // matter as a header.
    const { meta, body } = parseFrontmatter(raw);
    const description = meta?.description || '';
    const triggers = Array.isArray(meta?.triggers) ? meta.triggers : [];
    const agents = Array.isArray(meta?.agents) ? meta.agents : [];

    const header = h('div', {
      style: { background: '#FFFFFF', border: '1px solid #E5E7EB', borderRadius: '8px',
               padding: '14px 16px', marginBottom: '12px' },
    }, [
      h('h2', { style: { margin: '0 0 8px', fontSize: '16px' } }, meta?.name || item.title || item.file),
      description ? h('p', { style: { margin: '0 0 10px', color: '#374151', whiteSpace: 'pre-line' } }, description) : null,
      agents.length ? h('div', { style: { fontSize: '11px', color: '#6B7280', marginBottom: '4px' } },
        [h('strong', {}, 'agents: '), agents.join(', ')]) : null,
      triggers.length ? h('div', { style: { fontSize: '11px', color: '#6B7280' } },
        [h('strong', {}, 'triggers: '), triggers.slice(0, 6).join(' · '),
         triggers.length > 6 ? ` (+${triggers.length - 6})` : '']) : null,
    ].filter(Boolean));

    const open = h('button', {
      class: 'btn',
      onclick: () => { location.hash = `#workflow/${encodeURIComponent(path)}`; },
    }, 'Open source .md →');

    const viewer = createMdViewer({
      content: body,
      readOnly: true,
      breadcrumb: path.split('/'),
    });

    const box = h('div', {}, [
      h('div', { style: { marginBottom: '12px' } }, [open]),
      header,
      viewer,
    ]);
    mount(detailPane, box);
    // createMdViewer's internal rAF-runMermaid may fire while the body is
    // still detached from the document — mermaid 10.x then renders an
    // error SVG and stamps data-processed=true on the block, blocking any
    // future automatic retry. After the box is fully mounted, reset any
    // stuck blocks and re-run so the diagram lands.
    requestAnimationFrame(() => {
      const blocks = box.querySelectorAll('.mermaid');
      const stuck = Array.from(blocks).filter(b =>
        b.innerHTML.includes('aria-roledescription="error"'));
      if (!stuck.length) return;
      // Restore original source from the engine MD body and re-run.
      const fenceRe = /```mermaid\n([\s\S]*?)\n```/g;
      const sources = [];
      let m;
      while ((m = fenceRe.exec(body)) !== null) sources.push(m[1]);
      stuck.forEach((b, i) => {
        if (sources[i] != null) {
          b.innerHTML = sources[i];
          b.removeAttribute('data-processed');
        }
      });
      if (window.mermaid) {
        const fresh = box.querySelectorAll('.mermaid:not([data-processed])');
        if (fresh.length) mermaid.run({ nodes: fresh }).catch(e => console.warn('mermaid retry', e));
      }
    });
  };

  drawList();
  drawDetail();
}

// ─── Detail (deep link) — works for both tabs since it just loads the .md ─
async function renderDetail(ctx, coreData, engineIdx, filepath) {
  const { viewEl, topbarEl } = ctx;
  const file = decodeURIComponent(filepath);

  const coreMatch = coreData?.workflows.find(w => w.file === file);
  const enginePrefix = engineIdx?.base ? engineIdx.base.replace(/\/?$/, '/') : '';
  const engineMatch = engineIdx?.items.find(e => `${enginePrefix}${e.file}` === file);

  renderTopBar(topbarEl, {
    title: coreMatch?.label || engineMatch?.title || file,
    subtitle: file,
    extra: h('button', { class: 'btn', onclick: () => { location.hash = '#workflow'; } }, '← Back to list'),
  });

  mount(viewEl, loadingState());
  const content = await loadMd(file);
  if (content == null) { mount(viewEl, emptyState('Could not load file.')); return; }
  mount(viewEl, createMdViewer({ content, readOnly: true, breadcrumb: file.split('/') }));
}
