/**
 * AgentModels — tutorial on how the Actor model maps onto the AgentLoop
 * layer in this repo. Static EN/KO content from data/agent-models.json.
 * Bilingual toggle (EN/KO) shared with Principles + Models views.
 *
 * Reuses the principles tutorial CSS (tut-* classes) for hero / section /
 * paragraph / quote / mermaid / feature-list / callout-large blocks, then
 * adds three local kinds:
 *  - trait-map  : 2-column "Actor trait → AgentLoop benefit" table
 *  - spec-table : 3-column AgentLoopOptions field/default/purpose grid
 *  - file-list  : monospace path + bilingual note (canonical-source pointers)
 */
import { h, mount } from '../utils/dom.js';
import { loadData, fetchText } from '../utils/loader.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { getLang, makeLangToggle, t } from '../components/bilingual.js';
import { parsePen, renderFrame } from '../components/pen-renderer.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  let data = null;
  let lang = getLang();

  const draw = async () => {
    if (!data) {
      mount(viewEl, loadingState());
      data = await loadData('agent-models');
    }
    if (!data) { mount(viewEl, emptyState('data/agent-models.json missing.')); return; }
    lang = getLang();

    renderTopBar(topbarEl, {
      title: t(data.title, lang),
      subtitle: t(data.subtitle, lang),
      extra: makeLangToggle(() => draw()),
    });

    const root = h('div', { class: 'tut-root' },
      (data.sections || []).map(s => renderSection(s, lang)));
    mount(viewEl, root);

    if (window.mermaid) {
      mermaid.run({ nodes: root.querySelectorAll('.mermaid') })
        .catch(e => console.warn('mermaid', e));
    }
  };

  await draw();
}

function renderSection(s, lang) {
  if (s.kind === 'hero') return renderHero(s, lang);
  return renderStandard(s, lang);
}

function renderHero(s, lang) {
  return h('section', { class: 'tut-hero' }, [
    h('div', { class: 'tut-hero-icon', html: ICONS[s.icon] || ICONS.bulb }),
    h('h1', { class: 'tut-hero-title' }, t(s.title, lang)),
    h('p',  { class: 'tut-hero-lead' }, t(s.lead, lang)),
    s.callout ? h('div', { class: 'tut-hero-callout' }, t(s.callout, lang)) : null,
  ].filter(Boolean));
}

function renderStandard(s, lang) {
  const sec = h('section', { class: 'tut-sec', 'data-tone': s.tone || 'blue' });
  sec.appendChild(h('div', { class: 'tut-sec-head' }, [
    s.tagline ? h('span', { class: 'tut-sec-tag' }, s.tagline) : null,
    h('h2', { class: 'tut-sec-title' }, t(s.title, lang)),
  ].filter(Boolean)));
  const body = h('div', { class: 'tut-sec-body' });
  (s.body || []).forEach(item => {
    const node = renderBodyItem(item, lang);
    if (node) body.appendChild(node);
  });
  sec.appendChild(body);
  return sec;
}

function renderBodyItem(item, lang) {
  switch (item.kind) {
    case 'p':
      return h('p', { class: 'tut-p' }, t(item.text, lang));

    case 'mermaid':
      return h('div', { class: 'tut-mermaid' }, [
        h('div', { class: 'mermaid', html: item.code }),
      ]);

    case 'feature-list': {
      const list = h('div', { class: 'tut-features' });
      (item.items || []).forEach(f => {
        list.appendChild(h('div', { class: 'tut-feature' }, [
          h('div', { class: 'tut-feature-head' }, [
            h('span', { class: 'tut-feature-name' }, t(f.name, lang)),
          ]),
          h('p', { class: 'tut-feature-text' }, t(f.text, lang)),
        ]));
      });
      return list;
    }

    case 'callout-large':
      return h('div', { class: 'tut-callout-large' }, t(item.text, lang));

    case 'trait-map': {
      // 2-column: Actor trait → AgentLoop benefit
      const tbl = h('div', { class: 'am-trait-map' });
      tbl.appendChild(h('div', { class: 'am-trait-head' }, [
        h('div', {}, t(item.headers?.trait, lang)),
        h('div', {}, t(item.headers?.agent, lang)),
      ]));
      (item.rows || []).forEach(r => {
        tbl.appendChild(h('div', { class: 'am-trait-row' }, [
          h('div', { class: 'am-trait-key' }, t(r.trait, lang)),
          h('div', { class: 'am-trait-val' }, t(r.agent, lang)),
        ]));
      });
      return tbl;
    }

    case 'spec-table': {
      // 3-column: field (mono) | default (mono) | purpose (prose)
      const tbl = h('div', { class: 'am-spec-table' });
      tbl.appendChild(h('div', { class: 'am-spec-head' }, [
        h('div', {}, t(item.headers?.field,   lang)),
        h('div', {}, t(item.headers?.default, lang)),
        h('div', {}, t(item.headers?.purpose, lang)),
      ]));
      (item.rows || []).forEach(r => {
        tbl.appendChild(h('div', { class: 'am-spec-row' }, [
          h('div', { class: 'am-spec-field' },   String(r.field || '')),
          h('div', { class: 'am-spec-default' }, String(r.default || '—')),
          h('div', { class: 'am-spec-purpose' }, t(r.purpose, lang)),
        ]));
      });
      return tbl;
    }

    case 'file-list': {
      const list = h('div', { class: 'am-file-list' });
      (item.items || []).forEach(f => {
        list.appendChild(h('div', { class: 'am-file-row' }, [
          h('div', { class: 'am-file-path' }, f.path),
          h('div', { class: 'am-file-note' }, t(f.note, lang)),
        ]));
      });
      return list;
    }

    case 'pen-embed': {
      // Renders a .pen file inline using the project's pen-renderer.
      // src is relative to Home/harness-view/, e.g. "../../harness/knowledge/_shared/agent-loop-real-case.pen".
      const wrap = h('div', { class: 'am-pen-embed' });
      const stage = h('div', { class: 'am-pen-stage' });
      const placeholder = h('div', { class: 'am-pen-loading' }, lang === 'ko' ? '펜 로드 중…' : 'Loading pen…');
      stage.appendChild(placeholder);
      wrap.appendChild(stage);
      if (item.caption) {
        wrap.appendChild(h('div', { class: 'am-pen-caption' }, t(item.caption, lang)));
      }
      // Async fetch + render. parsePen + renderFrame are synchronous once we have JSON text.
      fetchText(item.src)
        .then(text => {
          stage.innerHTML = '';
          const { frames, variables } = parsePen(text);
          const root = frames[0];
          if (!root) {
            stage.appendChild(h('div', { class: 'am-pen-error' },
              lang === 'ko' ? '펜에 frame 이 없습니다.' : 'No frame found in pen.'));
            return;
          }
          renderFrame(root, stage, { vars: variables, maxWidth: 1880 });
        })
        .catch(err => {
          stage.innerHTML = '';
          stage.appendChild(h('div', { class: 'am-pen-error' },
            (lang === 'ko' ? '펜 로드 실패: ' : 'Pen load failed: ') + (err?.message || err)));
        });
      return wrap;
    }

    case 'code-sample': {
      const wrap = h('div', { class: 'am-code-sample' });
      if (item.title) {
        wrap.appendChild(h('div', { class: 'am-code-title' }, [
          h('span', { class: 'am-code-lang' }, item.lang || 'code'),
          h('span', {}, t(item.title, lang)),
        ]));
      }
      wrap.appendChild(h('pre', { class: 'am-code-block' }, item.code || ''));
      if (item.footnote) {
        wrap.appendChild(h('div', { class: 'am-code-footnote' }, t(item.footnote, lang)));
      }
      return wrap;
    }

    default:
      return null;
  }
}
