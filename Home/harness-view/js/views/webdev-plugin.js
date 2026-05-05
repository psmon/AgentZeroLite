/**
 * WebDev Plugin tutorial — how anyone with web knowledge can build an AI
 * tool plugin on AgentZero's bridge. Static EN/KO content from
 * data/webdev-plugin.json. Bilingual toggle (EN/KO) shared with the rest
 * of the secondary tutorial menus.
 *
 * Reuses the principles tutorial CSS (tut-* classes) for hero / section /
 * paragraph / feature-list / callout-large blocks. Adds four local kinds:
 *  - step-list   : numbered "do this, then this" with optional code block
 *  - api-table   : 2-column "call → what you get" reference grid
 *  - plugin-grid : auto-fill cards for shipped reference plugins
 *  - file-list   : monospace path + bilingual note
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
      data = await loadData('webdev-plugin');
    }
    if (!data) { mount(viewEl, emptyState('data/webdev-plugin.json missing.')); return; }
    lang = getLang();

    renderTopBar(topbarEl, {
      title: t(data.title, lang),
      subtitle: t(data.subtitle, lang),
      extra: makeLangToggle(() => draw()),
    });

    const root = h('div', { class: 'tut-root' },
      (data.sections || []).map(s => renderSection(s, lang)));
    mount(viewEl, root);
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

    case 'step-list': {
      const list = h('div', { class: 'wd-step-list' });
      (item.items || []).forEach((it, i) => {
        const body = h('div', { class: 'wd-step-body' }, [
          h('h4', {}, t(it.title, lang)),
          h('p',  {}, t(it.lead, lang)),
          it.code ? h('pre', { class: 'wd-step-code' }, it.code) : null,
        ].filter(Boolean));
        list.appendChild(h('div', { class: 'wd-step' }, [
          h('div', { class: 'wd-step-num' }, String(i + 1)),
          body,
        ]));
      });
      return list;
    }

    case 'api-table': {
      const tbl = h('div', { class: 'wd-api-table' });
      tbl.appendChild(h('div', { class: 'wd-api-head' }, [
        h('div', {}, t(item.headers?.call, lang)),
        h('div', {}, t(item.headers?.note, lang)),
      ]));
      (item.rows || []).forEach(r => {
        tbl.appendChild(h('div', { class: 'wd-api-row' }, [
          h('div', { class: 'wd-api-call' }, String(r.call || '')),
          h('div', { class: 'wd-api-note' }, t(r.note, lang)),
        ]));
      });
      return tbl;
    }

    case 'plugin-grid': {
      const grid = h('div', { class: 'wd-plugin-grid' });
      (item.items || []).forEach(p => {
        grid.appendChild(h('div', { class: 'wd-plugin-card' }, [
          h('div', { class: 'wd-plugin-head' }, [
            h('span', { class: 'wd-plugin-icon' }, p.icon || ''),
            h('span', {}, t(p.name, lang)),
            p.id ? h('span', { class: 'wd-plugin-id' }, p.id) : null,
          ].filter(Boolean)),
          h('div', { class: 'wd-plugin-tagline' }, t(p.tagline, lang)),
          p.demonstrates ? h('div', { class: 'wd-plugin-show' }, t(p.demonstrates, lang)) : null,
        ].filter(Boolean)));
      });
      return grid;
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

    default:
      return null;
  }
}
