/** 메인 라우터 + 사이드바 부트스트랩 */
import { MENU, ICONS } from './config/menu.js';
import { h, mount, clear } from './utils/dom.js';
import { toggleSidebar } from './views/_common.js';

const viewEl     = document.getElementById('view');
const topbarEl   = document.getElementById('topbar');
const subbarEl   = document.getElementById('subbar');
const sidebarEl  = document.getElementById('sidebar');
const backdropEl = document.getElementById('sidebar-backdrop');

// Mobile: backdrop click closes the slide-in sidebar.
if (backdropEl) backdropEl.addEventListener('click', () => toggleSidebar(false));

/** Mermaid 초기화 */
if (window.mermaid) {
  mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'loose' });
}

/** Marked 설정 — 커스텀 렌더러로 mermaid 블록 추출
 *
 * IMPORTANT: marked v4+ HTML-escapes fenced-code contents by default —
 * a `-->` arrow becomes `--&gt;` in the resulting innerHTML. mermaid.run
 * happens to read innerHTML rather than textContent, so the entity-
 * encoded source breaks the lexer ("Unrecognized text … B --&gt; C").
 * Decode the common entities before injecting into the .mermaid div so
 * the diagram source survives intact. */
function decodeMermaidEntities(s) {
  return String(s)
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'");
}
if (window.marked) {
  const renderer = new marked.Renderer();
  const origCode = renderer.code.bind(renderer);
  renderer.code = function(code, lang) {
    if (lang === 'mermaid') return `<div class="mermaid">${decodeMermaidEntities(code)}</div>`;
    return origCode(code, lang);
  };
  marked.setOptions({ renderer, gfm: true, breaks: false, headerIds: true });
}

/** 사이드바 렌더 */
function renderSidebar(activeId) {
  const main = MENU.filter(m => m.section === 'main');
  const sec  = MENU.filter(m => m.section === 'secondary');

  const renderItem = m => h('div', {
    class: `nav-item${m.id === activeId ? ' active' : ''}`,
    onclick: () => { location.hash = `#${m.id}`; },
  }, [
    h('span', { class: 'icon', html: ICONS[m.icon] || '' }),
    h('span', {}, m.label),
  ]);

  mount(sidebarEl,
    h('div', { class: 'logo' }, [
      h('div', { class: 'logo-text' }, 'AgentZero Dev'),
    ]),
    h('div', { class: 'nav-section' }, main.map(renderItem)),
    sec.length ? h('div', { class: 'divider' }) : null,
    sec.length ? h('div', { class: 'nav-section' }, sec.map(renderItem)) : null,
    h('div', { class: 'spacer' }),
    h('div', { class: 'footer' }, 'AgentZero Lite — Dev Harness'),
  );
}

/** 라우팅 */
async function route() {
  const rawHash = location.hash.replace(/^#/, '') || 'dashboard';
  const [id, ...rest] = rawHash.split('/');
  const params = rest.join('/');
  const menu = MENU.find(m => m.id === id) || MENU[0];

  renderSidebar(menu.id);

  // 기본 top/sub 상태 리셋
  subbarEl.hidden = true;
  clear(subbarEl);

  try {
    const mod = await import(`./views/${menu.view}.js`);
    const ctx = { viewEl, topbarEl, subbarEl, menu, params };
    await mod.render(ctx);
    // Mermaid 렌더
    if (window.mermaid) {
      const blocks = viewEl.querySelectorAll('.mermaid');
      if (blocks.length) {
        mermaid.run({ nodes: blocks }).catch(err => console.warn('mermaid', err));
      }
    }
  } catch (e) {
    console.error(e);
    mount(viewEl, h('div', { class: 'empty' }, `View load failed: ${e.message}`));
  }
}

// Route changes — also close the mobile sidebar so users see the new view.
window.addEventListener('hashchange', () => { toggleSidebar(false); route(); });
window.addEventListener('DOMContentLoaded', () => {
  if (!location.hash) location.hash = '#dashboard';
  route();
});
