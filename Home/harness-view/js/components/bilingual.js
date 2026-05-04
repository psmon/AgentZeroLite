/**
 * Bilingual EN/KO toggle — shared across tutorial views (Principles,
 * Models). Pick a language, render a small pill toggle, and persist the
 * choice across navigation via localStorage. Default is English (the
 * project's audience-facing language; the toggle exists so the operator
 * can also share content with Korean readers).
 *
 * Usage:
 *   import { getLang, makeLangToggle, t } from '../components/bilingual.js';
 *   const lang = getLang();
 *   const toggle = makeLangToggle(() => render());      // re-renders parent
 *   const title = t(node.title, lang);                  // node.title = { en, ko }
 */

import { h } from '../utils/dom.js';

const STORAGE_KEY = 'harnessViewLang';
const DEFAULT_LANG = 'en';
const VALID = new Set(['en', 'ko']);

export function getLang() {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v && VALID.has(v)) return v;
  } catch { /* localStorage may be unavailable */ }
  return DEFAULT_LANG;
}

export function setLang(lang) {
  if (!VALID.has(lang)) return;
  try { localStorage.setItem(STORAGE_KEY, lang); } catch { /* ignore */ }
}

/**
 * Picks the right string from a `{ en, ko }` shape. Falls back to EN if
 * the requested lang is missing, then to whatever value is present, then
 * to empty string.
 */
export function t(field, lang) {
  if (field == null) return '';
  if (typeof field === 'string') return field;
  if (typeof field === 'object') {
    return field[lang] ?? field.en ?? Object.values(field)[0] ?? '';
  }
  return String(field);
}

/**
 * Renders a 2-pill EN/KO toggle. `onChange` is called with the new lang
 * AFTER it's been persisted, so the caller typically just re-renders the
 * view with the new value of `getLang()`.
 */
export function makeLangToggle(onChange) {
  const lang = getLang();
  const wrap = h('div', { class: 'lang-toggle', role: 'group', 'aria-label': 'Language' });
  const mk = (id, label) => {
    const btn = h('button', {
      type: 'button',
      class: 'lang-pill' + (lang === id ? ' active' : ''),
      'aria-pressed': lang === id ? 'true' : 'false',
      onclick: () => {
        if (getLang() === id) return;
        setLang(id);
        try { onChange?.(id); } catch (e) { console.warn('[bilingual] onChange threw', e); }
      },
    }, label);
    return btn;
  };
  wrap.appendChild(mk('en', 'EN'));
  wrap.appendChild(mk('ko', '한국어'));
  return wrap;
}
