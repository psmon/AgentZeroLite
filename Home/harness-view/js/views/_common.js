/** 뷰 간 공통 helpers */
import { h, mount } from '../utils/dom.js';
import { ICONS } from '../config/menu.js';

/** Toggle the off-canvas sidebar (mobile). Exported so the topbar burger
 *  button and the backdrop click handler share the same flip. */
export function toggleSidebar(open) {
  const sidebarEl = document.getElementById('sidebar');
  const backdropEl = document.getElementById('sidebar-backdrop');
  if (!sidebarEl) return;
  const willOpen = typeof open === 'boolean' ? open : !sidebarEl.classList.contains('open');
  sidebarEl.classList.toggle('open', willOpen);
  if (backdropEl) backdropEl.classList.toggle('open', willOpen);
}

/**
 * 기본 TopBar 렌더 — 좌측 햄버거(모바일에서만 보임) + 제목 + 우측 액션
 * @param {HTMLElement} topbarEl
 * @param {{title: string, subtitle?: string, badge?: {kind: string, text: string}, search?: {placeholder: string}, extra?: HTMLElement}} opts
 */
export function renderTopBar(topbarEl, opts) {
  const burger = h('button', {
    class: 'menu-toggle',
    'aria-label': 'Open menu',
    onclick: () => toggleSidebar(true),
    html: ICONS.menu,
  });

  const titleBlock = h('div', { style: { minWidth: '0', flex: '1 1 auto' } }, [
    h('h1', { class: 'title' }, opts.title),
    opts.subtitle ? h('div', { class: 'subtitle' }, opts.subtitle) : null,
  ]);

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

  mount(topbarEl, burger, titleBlock, right);
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
