/**
 * MD 뷰어 컴포넌트
 * - marked 로 렌더링
 * - mermaid 코드 블록 자동 변환 (app.js 의 marked renderer 커스터마이즈에서 처리)
 * - 모드: 'view' | 'edit' (edit 에서는 textarea, view 에서는 렌더)
 * - readOnly: true 시 편집 토글 비활성
 */
import { h, mount, clear } from '../utils/dom.js';
import { ICONS } from '../config/menu.js';

function runMermaid(container) {
  if (!window.mermaid) return;
  const blocks = container.querySelectorAll('.mermaid:not([data-processed])');
  if (!blocks.length) return;
  mermaid.run({ nodes: blocks }).catch(err => console.warn('mermaid', err));
}

function renderMarkdown(md) {
  if (!window.marked) return `<pre>${md}</pre>`;
  return marked.parse(md || '');
}

/**
 * @param {object} opts
 * @param {string} opts.content - 마크다운 원문
 * @param {string} opts.title - 파일 제목(breadcrumb 등 표시용)
 * @param {string[]} opts.breadcrumb - 경로 세그먼트 배열
 * @param {boolean} opts.readOnly - true 이면 편집 금지
 * @param {function} opts.onSave - (newContent) => Promise<void>
 * @returns {HTMLElement}
 */
export function createMdViewer(opts) {
  const state = {
    mode: 'view',   // 'view' | 'edit'
    content: opts.content || '',
  };

  const root = h('div', { class: 'md-screen' });

  function renderTopBar() {
    const bc = h('div', { class: 'breadcrumb' });
    (opts.breadcrumb || []).forEach((seg, i, arr) => {
      const isLast = i === arr.length - 1;
      bc.appendChild(h('span', { class: isLast ? 'current' : '' }, seg));
      if (!isLast) bc.appendChild(h('span', { class: 'sep' }, '/'));
    });

    const right = h('div', { style: { display: 'flex', gap: '8px', alignItems: 'center' } });

    if (opts.readOnly) {
      right.appendChild(h('span', { class: 'badge badge-readonly' }, [
        h('span', { html: ICONS.eye }),
        document.createTextNode(' Reference'),
      ]));
    } else {
      const toggle = h('div', { class: 'toggle' });
      const viewBtn = h('button', {
        class: state.mode === 'view' ? 'active' : '',
        onclick: () => { state.mode = 'view'; draw(); },
      }, 'View');
      const editBtn = h('button', {
        class: state.mode === 'edit' ? 'active' : '',
        onclick: () => { state.mode = 'edit'; draw(); },
      }, 'Edit');
      toggle.append(viewBtn, editBtn);
      right.appendChild(toggle);

      if (state.mode === 'edit' && typeof opts.onSave === 'function') {
        const saveBtn = h('button', {
          class: 'btn btn-primary',
          onclick: async () => {
            const textarea = root.querySelector('textarea');
            if (textarea) {
              state.content = textarea.value;
              try { await opts.onSave(state.content); saveBtn.textContent = 'Saved'; }
              catch (e) { alert('Save failed: ' + e.message); }
              setTimeout(() => saveBtn.textContent = 'Save', 1500);
            }
          },
        }, 'Save');
        right.appendChild(saveBtn);
      }
    }

    return h('div', {
      style: {
        display: 'flex', justifyContent: 'space-between', alignItems: 'center',
        padding: '12px 16px', background: '#FFFFFF',
        border: '1px solid #E5E7EB', borderRadius: '10px',
      },
    }, [bc, right]);
  }

  function draw() {
    clear(root);
    root.appendChild(renderTopBar());

    if (state.mode === 'view') {
      const body = h('div', { class: 'md-viewer', html: renderMarkdown(state.content) });
      root.appendChild(body);
      requestAnimationFrame(() => runMermaid(body));
    } else {
      const wrap = h('div', { class: 'md-editor' });
      const ta = h('textarea', {
        spellcheck: 'false',
      });
      ta.value = state.content;
      wrap.appendChild(ta);
      root.appendChild(wrap);
    }
  }

  draw();
  return root;
}
