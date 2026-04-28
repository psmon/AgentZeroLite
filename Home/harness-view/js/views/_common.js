/** 뷰 간 공통 helpers */
import { h, mount } from '../utils/dom.js';
import { ICONS } from '../config/menu.js';

/**
 * 기본 TopBar 렌더
 * @param {HTMLElement} topbarEl
 * @param {{title: string, subtitle?: string, badge?: {kind: string, text: string}, search?: {placeholder: string}}} opts
 */
export function renderTopBar(topbarEl, opts) {
  const right = h('div', { class: 'right' });
  if (opts.badge) {
    const cls = 'badge badge-' + opts.badge.kind;
    right.appendChild(h('span', { class: cls }, [
      h('span', { html: ICONS.eye }),
      document.createTextNode(' ' + opts.badge.text),
    ]));
  }
  if (opts.search) {
    right.appendChild(h('label', { class: 'search' }, [
      h('span', { html: ICONS.search }),
      h('input', { type: 'search', placeholder: opts.search.placeholder, oninput: opts.search.oninput }),
    ]));
  }
  if (opts.extra) right.appendChild(opts.extra);

  mount(topbarEl,
    h('div', {}, [
      h('h1', { class: 'title' }, opts.title),
      opts.subtitle ? h('div', { class: 'subtitle' }, opts.subtitle) : null,
    ]),
    right,
  );
}

export function renderSubBar(subbarEl, tabs) {
  subbarEl.hidden = false;
  mount(subbarEl, h('div', { class: 'tabs' }, tabs.map(t => h('div', {
    class: 'tab' + (t.active ? ' active' : ''),
    onclick: t.onclick,
  }, [
    h('span', {}, t.label),
    t.count != null ? h('span', { class: 'count' }, String(t.count)) : null,
  ]))));
}

export function emptyState(msg) {
  return h('div', { class: 'empty' }, msg || 'No data available.');
}

export function loadingState(msg) {
  return h('div', { class: 'loading' }, msg || 'Loading...');
}
