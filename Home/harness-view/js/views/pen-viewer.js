/**
 * Pen Viewer 모달 — .pen(JSON) 파일을 로드해 프레임 넘겨보기.
 * 암호화된 .pen 은 로드 실패 시 "export 필요" 안내.
 */
import { h, mount } from '../utils/dom.js';
import { parsePen, renderFrame } from '../components/pen-renderer.js';
import { ICONS } from '../config/menu.js';

/**
 * 모달 열기 — 프로젝트 루트 기준 상대 경로.
 */
export async function openPenViewer(relativePath) {
  const modalRoot = document.getElementById('modal-root');
  modalRoot.hidden = false;

  const close = () => { modalRoot.hidden = true; modalRoot.replaceChildren(); };

  // 모달 골격
  const canvas = h('div', {
    style: {
      background: '#FFFFFF', borderRadius: '8px',
      border: '1px solid #E5E7EB', margin: '0 auto',
    },
  });
  const viewport = h('div', {
    style: { flex: '1 1 auto', padding: '24px', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'auto', background: '#F9FAFB' },
  }, [canvas]);
  const thumbs = h('div', { style: { width: '220px', borderRight: '1px solid #E5E7EB', padding: '16px 12px', background: '#FFFFFF', overflow: 'auto' } });
  const body = h('div', { style: { display: 'flex', flex: 1, minHeight: 0 } }, [thumbs, viewport]);
  const pageLabel = h('span', { style: { fontSize: '13px', fontWeight: 600, color: '#6B7280' } }, '0 / 0');
  const prevBtn = h('button', { class: 'btn', onclick: () => navigate(-1) }, [h('span', { html: ICONS.chevLeft }), document.createTextNode('이전')]);
  const nextBtn = h('button', { class: 'btn btn-primary', onclick: () => navigate(1) }, [document.createTextNode('다음'), h('span', { html: ICONS.chevRight })]);
  const nav = h('div', {
    style: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'center', padding: '8px 16px', background: '#FFFFFF', borderTop: '1px solid #E5E7EB' },
  }, [prevBtn, pageLabel, nextBtn]);

  const header = h('div', {
    style: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', padding: '16px 24px', background: '#FFFFFF', borderBottom: '1px solid #E5E7EB', gap: '16px' },
  }, [
    h('div', { style: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0, flex: 1 } }, [
      h('div', { style: { display: 'flex', gap: '10px', alignItems: 'center' } }, [
        h('span', { html: ICONS.external }),
        h('div', { style: { fontWeight: 700, color: '#111827' } }, relativePath),
        h('div', { style: { fontSize: '12px', color: '#9CA3AF' } }, '· 웹 렌더링'),
      ]),
      h('div', {
        style: {
          display: 'inline-flex', alignItems: 'center', gap: '6px',
          padding: '4px 10px', borderRadius: '12px',
          background: '#F5F3FF', color: '#6D28D9',
          fontSize: '11px', fontWeight: 500,
          alignSelf: 'flex-start',
        },
      }, [
        h('span', { style: { fontSize: '11px' } }, '💡'),
        h('span', {}, '이 하네스는 설계 First로 개발되었으며 펜슬 설계도면을 웹뷰로 보여줍니다'),
      ]),
    ]),
    h('button', {
      class: 'btn',
      style: { padding: '6px 10px', flex: '0 0 auto' },
      onclick: close,
    }, [h('span', { html: ICONS.x })]),
  ]);

  const modal = h('div', { class: 'modal' }, [header, body, nav]);
  mount(modalRoot, modal);

  // 파일 로드 & 파싱
  const state = { frames: [], vars: {}, idx: 0 };
  try {
    const res = await fetch(`../${relativePath}`);
    if (!res.ok) throw new Error(`${res.status}`);
    const text = await res.text();
    const parsed = parsePen(text);
    state.frames = parsed.frames;
    state.vars = parsed.variables;
  } catch (e) {
    mount(viewport, h('div', { class: 'empty' }, [
      h('div', { style: { fontSize: '14px', color: '#6B7280', marginBottom: '8px' } },
        '.pen 파일을 파싱할 수 없습니다. 아마도 암호화된 형식이거나 서버에서 serving 되지 않는 경로입니다.'),
      h('div', { style: { fontSize: '12px', color: '#9CA3AF' } },
        `경로: ../${relativePath}`),
      h('div', { style: { fontSize: '12px', color: '#9CA3AF', marginTop: '8px' } },
        '해결책: Pencil MCP 의 batch_get 으로 문서 전체를 .json 으로 내보낸 뒤 상대 경로로 접근하세요.'),
    ]));
    return;
  }

  function drawThumbs() {
    thumbs.replaceChildren(h('div', {
      style: { fontSize: '11px', fontWeight: 600, color: '#6B7280', marginBottom: '8px' },
    }, `프레임 ${state.frames.length}개`));
    state.frames.forEach((fr, i) => {
      const card = h('div', {
        style: {
          padding: '8px', borderRadius: '6px', cursor: 'pointer', marginBottom: '6px',
          background: i === state.idx ? '#EFF6FF' : '#F9FAFB',
          border: i === state.idx ? '2px solid #2563EB' : '1px solid #E5E7EB',
        },
        onclick: () => { state.idx = i; drawAll(); },
      }, [
        h('div', { style: { width: '100%', height: '72px', background: '#E5E7EB', borderRadius: '4px', marginBottom: '4px' } }),
        h('div', { style: { fontSize: '11px', color: i === state.idx ? '#2563EB' : '#6B7280', fontWeight: i === state.idx ? 600 : 400 } },
          `${i + 1}. ${fr.name || 'Frame ' + (i + 1)}`),
      ]);
      thumbs.appendChild(card);
    });
  }

  function drawCanvas() {
    const fr = state.frames[state.idx];
    if (!fr) { canvas.replaceChildren(h('div', { class: 'empty' }, '프레임이 없습니다.')); return; }
    const avail = Math.max(400, (viewport.clientWidth || 1000) - 48);
    renderFrame(fr, canvas, { maxWidth: avail, vars: state.vars });
  }

  function drawNav() {
    pageLabel.textContent = `${state.idx + 1} / ${state.frames.length}`;
  }

  function navigate(delta) {
    state.idx = Math.max(0, Math.min(state.frames.length - 1, state.idx + delta));
    drawAll();
  }

  function drawAll() { drawThumbs(); drawCanvas(); drawNav(); }

  drawAll();

  // ESC 닫기
  const onKey = (e) => { if (e.key === 'Escape') { close(); window.removeEventListener('keydown', onKey); } };
  window.addEventListener('keydown', onKey);
}

/** 라우팅 등록용 render (해당 페이지는 일반적으로 모달로 열림) */
export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;
  mount(topbarEl, h('h1', { class: 'title' }, 'Pen Viewer'));
  mount(viewEl, h('div', { class: 'empty' }, 'openPenViewer(relativePath) 를 호출하세요. (예: 온보딩의 "하네스 디자인 보기" 버튼)'));
}
