/** DOM 헬퍼 - 가볍게 태그·텍스트·엘리먼트를 생성 */

export function h(tag, props = {}, children = []) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class' || k === 'className') el.className = v;
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'dataset' && typeof v === 'object') Object.assign(el.dataset, v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  const list = Array.isArray(children) ? children : [children];
  for (const c of list) {
    if (c == null || c === false) continue;
    if (typeof c === 'string' || typeof c === 'number') el.appendChild(document.createTextNode(String(c)));
    else el.appendChild(c);
  }
  return el;
}

export function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }

export function mount(parent, ...children) {
  clear(parent);
  for (const c of children) {
    if (c == null) continue;
    parent.appendChild(c);
  }
}

/** 텍스트 변환 */
export function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, ch => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));
}

/** 파일명에서 날짜 추출 (YYYY-MM-DD- 또는 YYYY-MM-DD-HH-MM- 형식) */
export function parseDateFromFilename(filename) {
  const m = filename.match(/^(\d{4})-(\d{2})-(\d{2})(?:-(\d{2})-(\d{2}))?/);
  if (!m) return { date: null, title: filename.replace(/\.md$/, '') };
  const [_, y, mo, d, h, mi] = m;
  const date = `${y}-${mo}-${d}`;
  const rest = filename.replace(/^\d{4}-\d{2}-\d{2}(?:-\d{2}-\d{2})?-?/, '').replace(/\.md$/, '');
  return { date, time: h && mi ? `${h}:${mi}` : null, title: rest };
}

/** 제목 변환: kebab-case → Title Case (간단한 한글/영문 혼합 대응) */
export function humanize(s) {
  if (!s) return '';
  return s
    .replace(/\.md$/, '')
    .split(/[-_]/)
    .map(w => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}
