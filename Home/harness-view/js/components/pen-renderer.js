/**
 * Pen Renderer — .pen (JSON) 구조를 DOM 으로 렌더링
 *
 * 참고 구현: D:\code\AI\agent-win\Project\AgentZeroWpf\Services\PencilRenderer.cs
 * 본 JS 버전은 다음을 보강했다:
 *  - 모든 위치에서 $variable 색상 토큰 일관 해석 (fill, stroke.fill, gradient.colors)
 *  - stroke.fill 가 변수일 때 정확히 풀어내기 (C# 버전의 누락 수정)
 *  - icon_font 를 lucide CDN 으로 실제 아이콘 SVG 렌더 (■ placeholder 폐기)
 *  - layout 'none' / 미지정 시 absolute 배치 정확화
 *  - sizing: fit_content/fill_container/숫자/문자열 모두 지원
 *  - text wrapping: textGrowth 별 동작 (auto / fixed-width / fixed-width-height)
 *  - 안전한 fontFamily fallback 체인
 */

// ─── 기본 fallback ──────────────────────────────────────────────────────
const FONT_FALLBACK = '"Noto Sans KR", "Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
const DEFAULT_TEXT_FILL = '#111827';
const DEFAULT_BORDER    = '#E5E7EB';

// ─── .pen 파싱 ──────────────────────────────────────────────────────────
export function parsePen(jsonText) {
  const doc = JSON.parse(jsonText);
  const frames = (doc.children || []).filter(c => c.type === 'frame');
  const variables = {};
  for (const [k, v] of Object.entries(doc.variables || {})) {
    if (v && typeof v.value === 'string') {
      variables[k] = v.value;
    } else if (v && Array.isArray(v.value) && v.value[0]?.value) {
      // theme-별 값 배열 — 첫 항목 사용
      variables[k] = v.value[0].value;
    }
  }
  return { doc, frames, variables };
}

// ─── 색상 해석 (변수 + hex + named) ────────────────────────────────────
function resolveColor(input, vars, fallback = 'transparent') {
  if (input == null) return fallback;
  if (typeof input === 'string') {
    if (input.startsWith('$')) {
      const k = input.slice(1);
      const r = vars[k];
      if (r) return resolveColor(r, vars, fallback); // 변수가 다른 변수를 참조하는 경우
      return fallback;
    }
    return input;
  }
  // Fill 객체
  if (typeof input === 'object') {
    if (input.enabled === false) return 'transparent';
    if (input.type === 'color' || (!input.type && input.color)) {
      return resolveColor(input.color, vars, fallback);
    }
    if (input.type === 'gradient') {
      return gradientToCss(input, vars);
    }
    if (input.type === 'image' && input.url) {
      return `url("${input.url}")`;
    }
  }
  return fallback;
}

function gradientToCss(g, vars) {
  const stops = (g.colors || [])
    .map(c => `${resolveColor(c.color, vars, '#000')} ${(typeof c.position === 'number' ? c.position * 100 : 0)}%`)
    .join(', ');
  if (g.gradientType === 'radial') return `radial-gradient(${stops})`;
  if (g.gradientType === 'angular') return `conic-gradient(${stops})`;
  const rot = typeof g.rotation === 'number' ? g.rotation : 0;
  return `linear-gradient(${rot}deg, ${stops})`;
}

// ─── 숫자/사이즈 헬퍼 ──────────────────────────────────────────────────
function numOr(v, fallback) { return typeof v === 'number' ? v : fallback; }

function applySize(el, node, key, scale) {
  const v = node[key];
  const cssKey = key === 'width' ? 'width' : 'height';
  if (typeof v === 'number') { el.style[cssKey] = `${v * scale}px`; return; }
  if (typeof v === 'string') {
    if (v === 'fill_container') {
      // Mark only — the parent's child loop resolves this based on flex
      // direction. Applying `flex: 1 1 auto` here would land on the
      // PARENT's main axis regardless of whether width or height matched
      // it (e.g. width=fill_container in a vertical parent would wrongly
      // grow the child's height). The deferred resolution lives in
      // renderFrameNode below.
      if (key === 'width')  el.dataset.fillW = '1';
      else                  el.dataset.fillH = '1';
      return;
    }
    if (v.startsWith('fit_content')) {
      // fit_content(fallback) — children 으로 결정. CSS 기본 동작.
      const m = v.match(/fit_content\((\d+)\)/);
      if (m && (!node.children || node.children.length === 0)) el.style[cssKey] = `${parseInt(m[1]) * scale}px`;
      // 아니면 default (콘텐츠 기준)
      return;
    }
  }
}

// Resolve any fill_container markers on a child after we know the parent's
// flex direction. Main-axis fill becomes flex-grow; cross-axis fill becomes
// align-self stretch.
function resolveFillMarkers(cn, parentLayout) {
  if (parentLayout !== 'horizontal' && parentLayout !== 'vertical') return;
  const isHor = parentLayout === 'horizontal';
  if (cn.dataset?.fillW) {
    if (isHor) { cn.style.flex = '1 1 auto'; cn.style.minWidth = '0'; }
    else       { cn.style.alignSelf = 'stretch'; cn.style.width = 'auto'; }
    delete cn.dataset.fillW;
  }
  if (cn.dataset?.fillH) {
    if (!isHor) { cn.style.flex = '1 1 auto'; cn.style.minHeight = '0'; }
    else        { cn.style.alignSelf = 'stretch'; cn.style.height = 'auto'; }
    delete cn.dataset.fillH;
  }
}

function getPadding(node, scale) {
  const p = node.padding;
  if (p == null) return '0';
  if (typeof p === 'number') return `${p * scale}px`;
  if (Array.isArray(p)) {
    const v = p.map(x => `${x * scale}px`);
    if (v.length === 1) return v[0];
    if (v.length === 2) return `${v[0]} ${v[1]}`;
    if (v.length === 4) return `${v[0]} ${v[1]} ${v[2]} ${v[3]}`;
  }
  return '0';
}

function applyStroke(el, node, vars, scale) {
  const s = node.stroke;
  if (!s) return;
  const colorRaw = s.fill;
  const color = resolveColor(colorRaw, vars, DEFAULT_BORDER);
  const t = s.thickness;
  if (t && typeof t === 'object') {
    if (t.top)    el.style.borderTop    = `${(t.top    || 1) * scale}px solid ${color}`;
    if (t.right)  el.style.borderRight  = `${(t.right  || 1) * scale}px solid ${color}`;
    if (t.bottom) el.style.borderBottom = `${(t.bottom || 1) * scale}px solid ${color}`;
    if (t.left)   el.style.borderLeft   = `${(t.left   || 1) * scale}px solid ${color}`;
  } else {
    const thick = typeof t === 'number' ? t : 1;
    el.style.border = `${thick * scale}px solid ${color}`;
    if (s.align === 'inside') el.style.boxSizing = 'border-box';
  }
}

function cssJustify(v) {
  return ({ start: 'flex-start', end: 'flex-end', center: 'center', space_between: 'space-between', space_around: 'space-around' })[v] || 'flex-start';
}
function cssAlign(v) {
  return ({ start: 'flex-start', end: 'flex-end', center: 'center' })[v] || 'flex-start';
}

// ─── icon_font (lucide) ────────────────────────────────────────────────
function makeLucideIcon(name, color, size) {
  const span = document.createElement('span');
  // lucide.createIcons() 가 data-lucide 를 가진 요소를 SVG 로 치환
  span.setAttribute('data-lucide', name || 'circle');
  span.style.display = 'inline-flex';
  span.style.alignItems = 'center';
  span.style.justifyContent = 'center';
  span.style.color = color;
  span.style.width = `${size}px`;
  span.style.height = `${size}px`;
  span.style.flex = '0 0 auto';
  // SVG 자체의 width/height 를 부모 사이즈에 맞춤
  span.setAttribute('width', size);
  span.setAttribute('height', size);
  return span;
}

// ─── 노드 렌더 디스패처 ────────────────────────────────────────────────
function renderNode(node, scale, vars) {
  const type = node.type;
  if (!type || node.enabled === false) return null;

  switch (type) {
    case 'text':       return renderText(node, scale, vars);
    case 'rectangle':  return renderRect(node, scale, vars);
    case 'frame':      return renderFrameNode(node, scale, vars);
    case 'ellipse':    return renderEllipse(node, scale, vars);
    case 'line':       return renderLine(node, scale, vars);
    case 'icon_font':  return renderIcon(node, scale, vars);
    case 'group':      return renderFrameNode(node, scale, vars); // group 도 frame 처럼
    default: return null;
  }
}

function renderText(node, scale, vars) {
  const content = typeof node.content === 'string' ? node.content : '';
  if (!content) return null;
  const el = document.createElement('div');
  el.textContent = content;
  const fs = numOr(node.fontSize, 14) * scale;
  el.style.fontSize = `${Math.max(6, fs)}px`;
  el.style.color = resolveColor(node.fill, vars, DEFAULT_TEXT_FILL);
  el.style.fontWeight = node.fontWeight || 'normal';
  el.style.fontFamily = node.fontFamily ? `"${node.fontFamily}", ${FONT_FALLBACK}` : FONT_FALLBACK;
  if (typeof node.lineHeight === 'number') el.style.lineHeight = node.lineHeight;
  if (typeof node.letterSpacing === 'number') el.style.letterSpacing = `${node.letterSpacing * scale}px`;
  if (node.fontStyle) el.style.fontStyle = node.fontStyle;
  if (node.underline) el.style.textDecoration = 'underline';
  if (node.strikethrough) el.style.textDecoration = (el.style.textDecoration || '') + ' line-through';
  if (node.textAlign) el.style.textAlign = node.textAlign;
  el.style.flexShrink = '0';

  const tg = node.textGrowth || 'auto';
  if (tg === 'auto') {
    el.style.whiteSpace = 'nowrap';
  } else if (tg === 'fixed-width') {
    el.style.whiteSpace = 'normal';
    el.style.overflowWrap = 'break-word';
    applySize(el, node, 'width', scale);
  } else if (tg === 'fixed-width-height') {
    el.style.whiteSpace = 'normal';
    el.style.overflowWrap = 'break-word';
    applySize(el, node, 'width', scale);
    applySize(el, node, 'height', scale);
    el.style.overflow = 'hidden';
  }
  return el;
}

function applyCommonGfx(el, node, scale, vars) {
  el.style.background = resolveColor(node.fill, vars, 'transparent');
  if (node.cornerRadius) {
    if (Array.isArray(node.cornerRadius)) {
      el.style.borderRadius = node.cornerRadius.map(c => `${c * scale}px`).join(' ');
    } else {
      el.style.borderRadius = `${node.cornerRadius * scale}px`;
    }
  }
  applyStroke(el, node, vars, scale);
  if (typeof node.opacity === 'number') el.style.opacity = node.opacity;
  if (Array.isArray(node.effect) ? node.effect.length : node.effect) {
    applyEffects(el, node.effect, scale, vars);
  }
}

function applyEffects(el, effects, scale, vars) {
  const list = Array.isArray(effects) ? effects : [effects];
  const shadows = [];
  for (const e of list) {
    if (!e || e.enabled === false) continue;
    if (e.type === 'shadow') {
      const ox = (e.offset?.x || 0) * scale;
      const oy = (e.offset?.y || 0) * scale;
      const blur = (e.blur || 0) * scale;
      const spread = (e.spread || 0) * scale;
      const color = resolveColor(e.color, vars, '#00000040');
      shadows.push(`${e.shadowType === 'inner' ? 'inset ' : ''}${ox}px ${oy}px ${blur}px ${spread}px ${color}`);
    } else if (e.type === 'blur') {
      el.style.filter = `${el.style.filter || ''} blur(${(e.radius || 0) * scale}px)`.trim();
    }
  }
  if (shadows.length) el.style.boxShadow = shadows.join(', ');
}

function renderRect(node, scale, vars) {
  const el = document.createElement('div');
  applyCommonGfx(el, node, scale, vars);
  applySize(el, node, 'width', scale);
  applySize(el, node, 'height', scale);
  el.style.flexShrink = '0';
  return el;
}

function renderFrameNode(node, scale, vars) {
  const el = document.createElement('div');
  applyCommonGfx(el, node, scale, vars);
  el.style.padding = getPadding(node, scale);
  applySize(el, node, 'width', scale);
  applySize(el, node, 'height', scale);
  if (node.clip) el.style.overflow = 'hidden';

  // Pen 스키마 기본값: frame=horizontal, group=none
  const layout = node.layout != null ? node.layout
    : (node.type === 'group' ? 'none' : 'horizontal');
  const isFlex = layout === 'horizontal' || layout === 'vertical';
  if (isFlex) {
    el.style.display = 'flex';
    el.style.flexDirection = layout === 'horizontal' ? 'row' : 'column';
    if (node.gap) el.style.gap = `${node.gap * scale}px`;
    if (node.justifyContent) el.style.justifyContent = cssJustify(node.justifyContent);
    if (node.alignItems)     el.style.alignItems = cssAlign(node.alignItems);
  } else {
    el.style.position = 'relative';
  }

  for (const c of node.children || []) {
    const cn = renderNode(c, scale, vars);
    if (!cn) continue;
    if (isFlex) {
      resolveFillMarkers(cn, layout);
    } else if (c.layoutPosition !== 'auto') {
      cn.style.position = 'absolute';
      cn.style.left = `${(c.x || 0) * scale}px`;
      cn.style.top  = `${(c.y || 0) * scale}px`;
    }
    if (typeof c.rotation === 'number' && c.rotation) {
      cn.style.transformOrigin = '0 0';
      cn.style.transform = `rotate(${-c.rotation}deg)`; // pen 은 CCW
    }
    el.appendChild(cn);
  }

  el.style.flexShrink = '0';
  return el;
}

function renderEllipse(node, scale, vars) {
  const el = document.createElement('div');
  const w = numOr(node.width, 20) * scale;
  const h = numOr(node.height, 20) * scale;
  el.style.width = `${w}px`;
  el.style.height = `${h}px`;
  el.style.background = resolveColor(node.fill, vars, 'transparent');
  el.style.borderRadius = '50%';
  el.style.flexShrink = '0';
  applyStroke(el, node, vars, scale);
  if (typeof node.opacity === 'number') el.style.opacity = node.opacity;
  return el;
}

function renderLine(node, scale, vars) {
  const el = document.createElement('div');
  const w = numOr(node.width, 0);
  const h = numOr(node.height, 1);
  const stroke = node.stroke || {};
  const color = resolveColor(stroke.fill, vars, DEFAULT_BORDER);
  const thick = typeof stroke.thickness === 'number' ? stroke.thickness : 1;
  if (w >= h) {
    el.style.width = w ? `${w * scale}px` : '100%';
    el.style.height = `${thick * scale}px`;
    el.style.background = color;
  } else {
    el.style.width = `${thick * scale}px`;
    el.style.height = h ? `${h * scale}px` : '100%';
    el.style.background = color;
  }
  el.style.flexShrink = '0';
  return el;
}

function renderIcon(node, scale, vars) {
  const w = numOr(node.width, 16) * scale;
  const h = numOr(node.height, 16) * scale;
  const size = Math.max(8, Math.min(w, h));
  const color = resolveColor(node.fill, vars, '#666');
  return makeLucideIcon(node.iconFontName, color, size);
}

// ─── 프레임 렌더 (프레임 1개를 컨테이너에) ──────────────────────────────
export function renderFrame(frame, containerEl, { maxWidth = 900, vars = {} } = {}) {
  const fw = numOr(frame.width, 1920);
  const fh = numOr(frame.height, 1080);
  const scale = Math.min(maxWidth / fw, 1.0);
  const w = fw * scale;
  const h = fh * scale;

  containerEl.style.width = `${w}px`;
  containerEl.style.height = `${h}px`;
  containerEl.style.background = resolveColor(frame.fill, vars, '#FAFAFA');
  containerEl.style.position = 'relative';
  containerEl.style.overflow = 'hidden';
  containerEl.style.borderRadius = '6px';
  containerEl.style.border = `1px solid ${DEFAULT_BORDER}`;
  containerEl.style.boxShadow = '0 2px 12px rgba(0,0,0,0.06)';
  containerEl.replaceChildren();

  // 프레임 내부 = 자식들을 담는 root
  const inner = document.createElement('div');
  inner.style.position = 'absolute';
  inner.style.inset = '0';
  inner.style.overflow = 'hidden';
  inner.style.padding = getPadding(frame, scale);

  // 최상위 frame 도 동일 규칙: layout 미지정 = horizontal
  const layout = frame.layout != null ? frame.layout : 'horizontal';
  const isFlex = layout === 'horizontal' || layout === 'vertical';
  if (isFlex) {
    inner.style.display = 'flex';
    inner.style.flexDirection = layout === 'horizontal' ? 'row' : 'column';
    if (frame.gap)            inner.style.gap            = `${frame.gap * scale}px`;
    if (frame.justifyContent) inner.style.justifyContent = cssJustify(frame.justifyContent);
    if (frame.alignItems)     inner.style.alignItems     = cssAlign(frame.alignItems);
  }

  for (const child of frame.children || []) {
    const n = renderNode(child, scale, vars);
    if (!n) continue;
    if (isFlex) {
      resolveFillMarkers(n, layout);
    } else if (child.layoutPosition !== 'auto') {
      n.style.position = 'absolute';
      n.style.left = `${(child.x || 0) * scale}px`;
      n.style.top  = `${(child.y || 0) * scale}px`;
    }
    if (typeof child.rotation === 'number' && child.rotation) {
      n.style.transformOrigin = '0 0';
      n.style.transform = `rotate(${-child.rotation}deg)`;
    }
    inner.appendChild(n);
  }
  containerEl.appendChild(inner);

  // lucide 아이콘 SVG 변환 (모든 data-lucide 요소 → SVG)
  if (window.lucide && typeof window.lucide.createIcons === 'function') {
    try { window.lucide.createIcons({ root: containerEl }); } catch (_) {}
  }
}
