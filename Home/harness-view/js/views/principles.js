/**
 * Principles — PDSA tutorial. Static EN/KO content from
 * data/principles.json. Bilingual toggle (EN/KO) persists across
 * navigation via localStorage.
 *
 * Render strategy: a stack of section blocks, each driven by `kind`
 * (hero / section), and each section's `body` is a stack of `kind`-driven
 * blocks (p / quote / compare / mermaid / feature-list / phase-table /
 * compare-three / callout-large). Adding a new block kind = adding one
 * branch in renderBodyItem().
 */
import { h, mount } from '../utils/dom.js';
import { loadData } from '../utils/loader.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { getLang, makeLangToggle, t } from '../components/bilingual.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  let data = null;
  let lang = getLang();

  const draw = async () => {
    if (!data) {
      mount(viewEl, loadingState());
      data = await loadData('principles');
    }
    if (!data) { mount(viewEl, emptyState('data/principles.json missing.')); return; }
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

    case 'quote':
      return h('blockquote', { class: 'tut-quote' }, [
        h('div', { class: 'tut-quote-mark' }, '“'),
        h('div', { class: 'tut-quote-text' }, t(item.text, lang)),
        item.by ? h('div', { class: 'tut-quote-by' }, `— ${item.by}`) : null,
      ].filter(Boolean));

    case 'mermaid':
      return h('div', { class: 'tut-mermaid' }, [
        h('div', { class: 'mermaid', html: item.code }),
      ]);

    case 'compare': {
      const wrap = h('div', { class: 'tut-compare' });
      ['left', 'right'].forEach(side => {
        const col = item[side];
        if (!col) return;
        const card = h('div', { class: `tut-compare-col tone-${col.tone || 'blue'}` }, [
          h('div', { class: 'tut-compare-label' }, t(col.label, lang)),
          h('ul', { class: 'tut-compare-list' },
            (col.items || []).map(it => h('li', {}, t(it, lang)))),
        ]);
        wrap.appendChild(card);
      });
      return wrap;
    }

    case 'feature-list': {
      const list = h('div', { class: 'tut-features' });
      (item.items || []).forEach(f => {
        list.appendChild(h('div', { class: 'tut-feature' }, [
          h('div', { class: 'tut-feature-head' }, [
            h('span', { class: 'tut-feature-name' }, t(f.name, lang)),
            f.translit ? h('span', { class: 'tut-feature-translit' }, t(f.translit, lang)) : null,
          ].filter(Boolean)),
          h('p', { class: 'tut-feature-text' }, t(f.text, lang)),
        ]));
      });
      return list;
    }

    case 'phase-table': {
      const tbl = h('div', { class: 'tut-phase-table' });
      (item.phases || []).forEach(p => {
        tbl.appendChild(h('div', { class: `tut-phase-row tone-${p.color || 'blue'}` }, [
          h('div', { class: 'tut-phase-label' }, p.phase),
          h('div', { class: 'tut-phase-text' }, t(p.harness, lang)),
        ]));
      });
      return tbl;
    }

    case 'compare-three': {
      const tbl = h('div', { class: 'tut-three' });
      tbl.appendChild(h('div', { class: 'tut-three-head' }, [
        h('div', {}, lang === 'ko' ? '도구' : 'Tool'),
        h('div', {}, lang === 'ko' ? '얕은 질문 (Check)' : 'Surface question (Check)'),
        h('div', {}, lang === 'ko' ? '깊은 질문 (Study)' : 'Deeper question (Study)'),
      ]));
      (item.rows || []).forEach(r => {
        tbl.appendChild(h('div', { class: 'tut-three-row' }, [
          h('div', { class: 'tut-three-tool' }, r.tool),
          h('div', { class: 'tut-three-asks' }, t(r.asks, lang)),
          h('div', { class: 'tut-three-study' }, t(r.study, lang)),
        ]));
      });
      return tbl;
    }

    case 'callout-large':
      return h('div', { class: 'tut-callout-large' }, t(item.text, lang));

    default:
      return null;
  }
}
