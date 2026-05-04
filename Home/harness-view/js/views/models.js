/**
 * Models — card view of all LLM / STT / TTS models adopted in
 * AgentZero Lite. Static EN/KO content from data/models.json. Bilingual
 * toggle (EN/KO) shared with the Principles view.
 *
 * Layout:
 *   Hero intro block
 *   Per-category section (lead + grid of model cards)
 *   Footnote
 *
 * Each card surfaces: name, vendor, badges (on-device / hosted /
 * default / etc.), variant, size, params, EN/KO summary, optional EN/KO
 * details paragraph. The category color tone is applied as a left
 * border accent on every card in that category.
 */
import { h, mount } from '../utils/dom.js';
import { loadData } from '../utils/loader.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { getLang, makeLangToggle, t } from '../components/bilingual.js';

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  let data = null;
  let lang = getLang();

  const draw = async () => {
    if (!data) {
      mount(viewEl, loadingState());
      data = await loadData('models');
    }
    if (!data) { mount(viewEl, emptyState('data/models.json missing.')); return; }
    lang = getLang();

    renderTopBar(topbarEl, {
      title: t(data.title, lang),
      subtitle: t(data.subtitle, lang),
      extra: makeLangToggle(() => draw()),
    });

    const root = h('div', { class: 'mod-root' });
    if (data.intro) {
      root.appendChild(h('div', { class: 'mod-intro' }, t(data.intro, lang)));
    }
    (data.categories || []).forEach(cat => {
      root.appendChild(renderCategory(cat, lang));
    });
    if (data.footnote) {
      root.appendChild(h('div', { class: 'mod-footnote' }, t(data.footnote, lang)));
    }
    mount(viewEl, root);
  };

  await draw();
}

function renderCategory(cat, lang) {
  const sec = h('section', { class: 'mod-cat', 'data-tone': cat.tone || 'blue' });
  sec.appendChild(h('div', { class: 'mod-cat-head' }, [
    h('h2', { class: 'mod-cat-title' }, t(cat.label, lang)),
    cat.lead ? h('p', { class: 'mod-cat-lead' }, t(cat.lead, lang)) : null,
  ].filter(Boolean)));
  const grid = h('div', { class: 'mod-grid' });
  (cat.models || []).forEach(m => grid.appendChild(renderModelCard(m, lang)));
  sec.appendChild(grid);
  return sec;
}

function renderModelCard(m, lang) {
  const badges = (m.badges || []).map(b =>
    h('span', { class: `mod-badge badge-${slug(b)}` }, b));

  const facts = h('div', { class: 'mod-facts' }, [
    fact('vendor', m.vendor),
    fact('variant', m.variant),
    fact('size', m.size),
    fact('params', m.params),
  ].filter(Boolean));

  const card = h('div', { class: `mod-card tone-${m.tone || 'blue'}` }, [
    h('div', { class: 'mod-card-head' }, [
      h('div', { class: 'mod-name' }, m.name),
      h('div', { class: 'mod-badges' }, badges),
    ]),
    facts,
    h('p', { class: 'mod-summary' }, t(m.summary, lang)),
    m.details ? h('p', { class: 'mod-details' }, t(m.details, lang)) : null,
  ].filter(Boolean));
  return card;
}

function fact(key, value) {
  if (value == null || value === '' || value === '—') {
    // Skip empty rows so cards stay compact for hosted models that don't
    // expose a fixed size / param count.
    if (key === 'size' || key === 'params') return null;
  }
  return h('div', { class: 'mod-fact' }, [
    h('span', { class: 'mod-fact-key' }, key),
    h('span', { class: 'mod-fact-val' }, String(value || '—')),
  ]);
}

function slug(s) {
  return String(s).toLowerCase().replace(/[^a-z0-9]+/g, '-');
}
