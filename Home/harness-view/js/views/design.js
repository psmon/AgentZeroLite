/**
 * Product Design — full-page Pencil (.pen) viewer.
 * Renders every frame in Docs/design/*.pen with thumbnail nav on the left
 * and the active frame on the right.
 *
 * Story line: AgentZero Lite was designed-first. The .pen blueprint is the
 * source of truth — this view shows the user the actual design canvases that
 * the application was built against.
 */
import { h, mount } from '../utils/dom.js';
import { loadIndex } from '../utils/loader.js';
import { parsePen, renderFrame } from '../components/pen-renderer.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

// External resources are mirrored under Home/_resources/ at build time so
// GitHub Pages (Home-only publish) can serve them. ../_resources/<rel>
// resolves to Home/_resources/<rel> in both local and Pages runs.
const PROJECT_ROOT_PREFIX = '../_resources/';

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  const index = await loadIndex('design-index');
  const pens = (index?.items || []).filter(it => it.type === 'pen');

  renderTopBar(topbarEl, {
    title: 'Product Design',
    subtitle: `${index?.base || 'Docs/design'} — design-first blueprint of AgentZero Lite`,
    badge: { kind: 'edit', text: 'Pencil .pen' },
  });

  if (!pens.length) {
    mount(viewEl, emptyState('No .pen files found under Docs/design/.'));
    return;
  }

  // intro banner — design-first narrative
  const intro = h('div', {
    style: {
      padding: '12px 16px', marginBottom: '16px',
      background: '#F5F3FF', border: '1px solid #DDD6FE', borderRadius: '8px',
      color: '#4C1D95', fontSize: '13px', lineHeight: '1.6',
    },
  }, [
    h('div', { style: { fontWeight: 600, marginBottom: '4px' } },
      '💡 Designed first, then built'),
    h('div', {},
      'AgentZero Lite started as a Pencil canvas. The frames below are the original blueprint — every screen the app ships with traces back to one of them. The view renders them straight from the .pen JSON, no PNG export.'),
  ]);

  const state = { fileIdx: 0, frameIdx: 0, frames: [], vars: {} };
  const root = h('div');
  mount(viewEl, intro, root);

  await loadActive();

  async function loadActive() {
    const it = pens[state.fileIdx];
    if (!it) { mount(root, emptyState('No active .pen.')); return; }

    mount(root, loadingState(`Loading ${it.file}…`));

    let parsed;
    try {
      const url = `${PROJECT_ROOT_PREFIX}${index.base}/${it.file}`;
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const text = await res.text();
      parsed = parsePen(text);
    } catch (e) {
      mount(root, h('div', { class: 'empty' }, [
        h('div', { style: { fontSize: '14px', color: '#6B7280', marginBottom: '8px' } },
          'Could not parse the .pen file. It may be encrypted or not served by the dev server.'),
        h('div', { style: { fontSize: '12px', color: '#9CA3AF' } },
          `path: ${index.base}/${it.file}`),
        h('div', { style: { fontSize: '12px', color: '#9CA3AF', marginTop: '8px' } },
          'Workaround: export the document via Pencil MCP `batch_get` to plain JSON, then place it next to the .pen.'),
      ]));
      return;
    }

    state.frames = parsed.frames || [];
    state.vars = parsed.variables || {};
    state.frameIdx = 0;
    drawAll();
  }

  function drawAll() {
    // Left = file list (rare — usually 1) + frame thumbs.  Right = canvas + nav.
    const fileList = h('div', { style: { marginBottom: '12px' } });
    if (pens.length > 1) {
      fileList.appendChild(h('div', { style: { fontSize: '11px', fontWeight: 600, color: '#6B7280', marginBottom: '6px', textTransform: 'uppercase', letterSpacing: '.04em' } }, 'Files'));
      pens.forEach((it, i) => {
        fileList.appendChild(h('div', {
          style: {
            padding: '6px 10px', borderRadius: '4px', cursor: 'pointer', marginBottom: '4px',
            fontSize: '12px',
            background: i === state.fileIdx ? '#EFF6FF' : 'transparent',
            color: i === state.fileIdx ? '#2563EB' : '#374151',
            fontWeight: i === state.fileIdx ? 600 : 400,
          },
          onclick: () => { state.fileIdx = i; loadActive(); },
        }, it.file));
      });
    }

    const thumbsHead = h('div', {
      style: { fontSize: '11px', fontWeight: 600, color: '#6B7280', marginBottom: '8px', textTransform: 'uppercase', letterSpacing: '.04em' },
    }, `Frames · ${state.frames.length}`);
    const thumbList = h('div', { style: { display: 'flex', flexDirection: 'column', gap: '6px' } });
    state.frames.forEach((fr, i) => {
      thumbList.appendChild(h('div', {
        style: {
          padding: '8px', borderRadius: '6px', cursor: 'pointer',
          background: i === state.frameIdx ? '#EFF6FF' : '#F9FAFB',
          border: i === state.frameIdx ? '2px solid #2563EB' : '1px solid #E5E7EB',
        },
        onclick: () => { state.frameIdx = i; drawAll(); },
      }, [
        h('div', { style: { width: '100%', height: '60px', background: '#E5E7EB', borderRadius: '4px', marginBottom: '4px' } }),
        h('div', {
          style: {
            fontSize: '11px',
            color: i === state.frameIdx ? '#2563EB' : '#6B7280',
            fontWeight: i === state.frameIdx ? 600 : 400,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          },
        }, `${i + 1}. ${fr.name || 'Frame ' + (i + 1)}`),
      ]));
    });

    const left = h('div', {
      style: {
        width: '240px', flex: '0 0 240px', overflow: 'auto',
        padding: '12px', background: '#FFFFFF',
        border: '1px solid #E5E7EB', borderRadius: '8px',
      },
    }, [fileList, thumbsHead, thumbList]);

    const canvas = h('div', { style: { background: '#FFFFFF', borderRadius: '8px', border: '1px solid #E5E7EB', margin: '0 auto' } });
    const viewport = h('div', {
      style: {
        flex: '1 1 auto', overflow: 'auto',
        padding: '24px', background: '#F9FAFB',
        border: '1px solid #E5E7EB', borderRadius: '8px',
        display: 'flex', alignItems: 'flex-start', justifyContent: 'center',
        minHeight: '60vh',
      },
    }, [canvas]);

    const pageLabel = h('span', { style: { fontSize: '13px', fontWeight: 600, color: '#6B7280' } },
      `${state.frameIdx + 1} / ${state.frames.length}`);
    const prevBtn = h('button', { class: 'btn', onclick: () => navigate(-1) },
      [h('span', { html: ICONS.chevLeft }), document.createTextNode(' Prev')]);
    const nextBtn = h('button', { class: 'btn btn-primary', onclick: () => navigate(1) },
      [document.createTextNode('Next '), h('span', { html: ICONS.chevRight })]);
    const nav = h('div', {
      style: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'center', padding: '12px', background: '#FFFFFF', borderTop: '1px solid #E5E7EB', borderRadius: '0 0 8px 8px' },
    }, [prevBtn, pageLabel, nextBtn]);

    const right = h('div', { style: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '0' } }, [viewport, nav]);

    mount(root, h('div', { style: { display: 'flex', gap: '16px', alignItems: 'stretch', minHeight: '60vh' } }, [left, right]));

    // render the active frame after layout settles so we can measure viewport width
    requestAnimationFrame(() => {
      const fr = state.frames[state.frameIdx];
      if (!fr) { canvas.replaceChildren(h('div', { class: 'empty' }, 'No frame.')); return; }
      const avail = Math.max(400, (viewport.clientWidth || 1000) - 48);
      renderFrame(fr, canvas, { maxWidth: avail, vars: state.vars });
    });
  }

  function navigate(delta) {
    if (!state.frames.length) return;
    state.frameIdx = Math.max(0, Math.min(state.frames.length - 1, state.frameIdx + delta));
    drawAll();
  }
}
