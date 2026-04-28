# CREATOR — 하네스 뷰 자체를 만들고 개선

> 개발 모드. 하네스뷰의 코드 (HTML/CSS/JS) 를 작성하거나 수정한다.
> 인덱스 갱신은 BUILDER 모드 담당이라 여기선 다루지 않는다.

## 설계 철학

### 핵심 원칙
1. **설계 First** — Pencil 로 화면 설계 후 정적 페이지로 구현. `Docs/design/design.pen` 이 이 프로젝트의 단일 설계 원본 (앱디자인, 어플리케이션 가치 중심).
2. **두 가지 데이터 모드**
   - **리소스참고**: 하드코딩 없이 `indexes/*.json` 매니페스트로 동적 로딩. 컨텐츠 변경 시 빌드만 다시.
   - **사전구현**: 정적 JSON (`data/*.json`) 으로 사전 분석 결과 보관. 외부 리서치·그래프 정의 등.
3. **번들 없음** — 빌드 단계 없는 vanilla HTML + ES Modules. CDN 라이브러리만 사용 (marked, mermaid, lucide).
4. **하나의 파일에 다 넣지 않는다** — 메뉴별 1뷰 1파일 (`js/views/<menu>.js`).
5. **공통 컴포넌트는 분리** — `js/components/` 에 두고 여러 뷰에서 import (예: `md-viewer.js`, `pen-renderer.js`, `spec-card.js`).

## 디렉토리 구조

```
Home/harness-view/
├── index.html                    # shell: sidebar + topbar + view container + modal-root
├── css/
│   ├── main.css                  # 레이아웃 (sidebar, topbar, subbar, modal)
│   ├── components.css            # 카드/탭/필/게시판/트리/뱃지/spec-* 등
│   └── md.css                    # 마크다운 렌더 + 에디터 + mermaid 블록
├── js/
│   ├── app.js                    # hash 라우터 + marked/mermaid 부트스트랩 + sidebar
│   ├── config/menu.js            # MENU 배열 + inline SVG ICONS
│   ├── utils/
│   │   ├── loader.js             # fetchText / fetchJson / loadIndex / loadData / loadMd
│   │   └── dom.js                # h(tag, props, children) / mount / clear / humanize
│   ├── components/
│   │   ├── md-viewer.js          # marked + mermaid + 보기/편집 토글 + ReadOnly
│   │   ├── pen-renderer.js       # .pen JSON → DOM
│   │   ├── pen-viewer.js         # .pen 모달 (현재 메뉴에선 미사용 — Product Design 이 in-place)
│   │   └── spec-card.js          # ★ Roles/Skills frontmatter (name/persona/triggers/description/allowed-tools) → 카드
│   └── views/
│       ├── _common.js            # renderTopBar / renderSubBar / emptyState / loadingState
│       ├── dashboard.js          # 4 섹션: Recent Updates / PDSA / Build Log / Contributors
│       ├── workflow.js           # data/workflow-graph.json + mermaid
│       ├── role.js               # 카드그리드 + spec card 상세 (harness/agents)
│       ├── skill.js              # 카드그리드 + spec card 상세 (Docs/harness/template)
│       ├── knowledge.js          # 탭 2개 (Expert / Tech-Domain)
│       ├── usage-log.js          # 게시판 + 동적 카테고리 (subfolder = tag)
│       ├── design.js             # ★ Product Design — in-place .pen viewer
│       └── product-intro.js      # ★ Product Intro — Home/index.html iframe
├── data/                          # 사전구현 정적 JSON
│   ├── news.json                 # Recent Updates (영문 — AgentZero Lite 톤)
│   ├── pdsa-insight.json         # PDSA Learning (영문)
│   └── workflow-graph.json       # 7개 workflow (스킬 시스템 노드는 의도적 제외)
├── indexes/                       # 빌더가 생성하는 매니페스트 (수동 편집 X)
└── scripts/
    ├── build-indexes.js          # 디렉토리 스캔 → 매니페스트 출력
    └── serve.js                  # 127.0.0.1:8765 dev 서버 (preflight 자동 빌드 포함)
```

## 라우팅

`location.hash` 기반. 패턴: `#<menuId>` 또는 `#<menuId>/<params>`.

```
#dashboard                  → views/dashboard.js (목록)
#dashboard/v1.1.5.md        → views/dashboard.js (상세 ReadOnly MD)
#knowledge/knowledge        → views/knowledge.js (Expert 탭)
#knowledge/domain/...       → views/knowledge.js (Tech/Domain 탭, 트리+편집)
#design                     → views/design.js
#product-intro              → views/product-intro.js (iframe)
```

`app.js` 의 `route()` 가 `await import('./views/<menu.view>.js')` 로 동적 로딩 후 `mod.render(ctx)` 호출.

## 뷰 작성 규칙

각 뷰 모듈은 단 하나의 export 만 가진다:
```js
export async function render(ctx) {
  const { viewEl, topbarEl, subbarEl, menu, params } = ctx;
  // 1) 데이터 로드
  // 2) renderTopBar(topbarEl, ...)
  // 3) (선택) renderSubBar(subbarEl, ...)
  // 4) viewEl 안에 본문 mount
  // 5) params 가 있으면 상세화면 분기
}
```

### 헬퍼 사용
```js
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd, loadData } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { createSpecCard, parseFrontmatter } from '../components/spec-card.js';
import { renderTopBar, renderSubBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';
```

### 상세 진입 패턴
```js
if (params) return renderDetail(ctx, index, params);
// 목록 렌더
```
상세에서는 `← Back to list` 버튼을 `topBar.extra` 에 넣어 hash 를 부모로 되돌린다.

## Spec Card (Roles + Skills 공통)

`harness/agents/*.md` 와 `Docs/harness/template/<skill>/SKILL.md` 가 사용하는
공통 frontmatter 패턴. raw YAML 대신 구조화된 카드로 보여준다.

지원 키:
| Key | 표시 |
|---|---|
| `persona` | 큰 글씨 (display name) |
| `name` | mono code id (persona 와 다를 때) |
| `description` | 본문 (280자 넘으면 collapsible) |
| `triggers` | chip list — `"phrase"` 형식 (YAML 배열) |
| `allowed-tools` | chip list — `builtin` / `mcp__*` 분류 |
| 기타 (model, version 등) | generic label/value 행 |

YAML 파서는 다음을 지원:
- `key: value` (단일 라인)
- `key: |` 멀티라인 블록 스칼라
- `key:\n  - "item1"` 배열 (따옴표 자동 제거)

사용 예 (role.js):
```js
const { meta, body } = parseFrontmatter(raw);
const card = createSpecCard(meta, { tag: 'ROLE', fallbackName: baseName });
if (card) screen.appendChild(card);
screen.appendChild(createMdViewer({ content: body, readOnly: true, breadcrumb }));
```

skill.js 도 같은 패턴, 단지 `tag: 'SKILLS 2.0'` 만 다르다.

## 컴포넌트 패턴

### 카드 그리드
```js
const grid = h('div', { class: 'card-grid' });  // CSS grid: auto-fill minmax(280px, 1fr)
for (const it of items) {
  grid.appendChild(h('div', {
    class: 'card',
    onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.file)}`; },
  }, [
    h('span', { class: 'card-icon', html: ICONS.file }),
    h('div', { class: 'card-title' }, it.title),
    h('div', { class: 'card-desc' }, it.desc),
  ]));
}
```

### 탭바
`renderSubBar(subbarEl, [{ label, count, active, onclick }, ...])` 호출.
탭 클릭은 hash 변경: `location.hash = '#menu/tab1'`.

### Split (트리 + 미리보기)
```html
<div class="split">       <!-- grid 320px 1fr, height: viewport - topbar - subbar -->
  <div class="pane pane-l">…tree…</div>
  <div class="pane pane-r">…preview…</div>
</div>
```

### 게시판 (테이블 형) — 동적 카테고리
`.board` + `.board-head` + `.board-row` (CSS grid `120px 1fr 140px`).
`usage-log.js` 가 reference. 카테고리는 `index.categories` (subfolder 동적) 사용 + `humanize()` 라벨링.

### Read+Edit MD
`createMdViewer({ content, breadcrumb, onSave })`. `readOnly: true` 로 보기 전용.
`onSave` 미제공 시 토글만 동작 (sessionStorage 저장).

### iframe 뷰 (Product Intro 패턴)
```js
const frame = h('iframe', {
  src: '../index.html',  // Home/harness-view/ → ../ = Home/
  style: { width: '100%', height: 'calc(100vh - 120px)', border: '1px solid #E5E7EB' },
});
```

## MD 렌더링

`marked@12` + `mermaid@10` (CDN). `app.js` 에서 marked.renderer 를 커스터마이즈해 ` ```mermaid ` 코드블록을 `<div class="mermaid">` 로 변환. 이후 `mermaid.run({ nodes })` 로 SVG 생성.

```js
// app.js
const renderer = new marked.Renderer();
const origCode = renderer.code.bind(renderer);
renderer.code = function(code, lang) {
  if (lang === 'mermaid') return `<div class="mermaid">${code}</div>`;
  return origCode(code, lang);
};
```

`createMdViewer` 가 렌더 후 `requestAnimationFrame` 안에서 mermaid.run 호출.

### Mermaid 노드 라벨 주의
`|` 문자가 mermaid 파서에서 link-label 분리자로 해석되어 syntax error 가 날 수 있다.
필요하면 노드 라벨을 큰따옴표로 감싸거나 `·` 같은 문자로 치환:
```
S["LLM tab — Local · External"] --> F[LlmProviderFactory]
```

## Pencil 렌더 (pen-renderer.js)

`Docs/design/design.pen` 의 frames 를 DOM 으로 그린다. C# `PencilRenderer.cs` 포팅 + 다음 보강:

| 개선 | 설명 |
|------|------|
| `$variable` 일관 해석 | fill / stroke.fill / gradient.colors / shadow.color 모두 |
| **layout 기본값 horizontal** | Pen 스키마 표준. C# 원본의 누락 (없으면 absolute) 수정 |
| icon_font 실 렌더 | lucide CDN 으로 `data-lucide` → SVG 변환 (`window.lucide.createIcons`) |
| 그라데이션 | linear/radial/angular → CSS gradient |
| 효과 | shadow (inner/outer), blur |
| 회전 | pen 은 CCW → CSS transform `rotate(-deg)` |
| 사이징 | `fit_content`, `fill_container`, `fit_content(N)` 폴백 |
| textGrowth | auto(nowrap) / fixed-width(wrap) / fixed-width-height(clip) |
| fontFamily | `"X", "Noto Sans KR", "Inter", -apple-system, ...` 폴백 체인 |

핵심 함수:
- `parsePen(jsonText)` → `{ doc, frames, variables }`
- `renderFrame(frame, containerEl, { maxWidth, vars })` — 프레임 1개를 컨테이너에 그림

Product Design 메뉴 (`design.js`) 가 reference. 모달이 아닌 in-place full-page 패턴.

## 새 메뉴 추가하는 절차

1. **메뉴 등록**: `js/config/menu.js` 의 MENU 배열에 항목 추가
   ```js
   { id: 'reports', label: 'Reports', icon: 'file', section: 'main', view: 'reports' }
   ```
   `icon` 키가 ICONS 에 없으면 SVG 도 함께 추가.

2. **뷰 모듈 생성**: `js/views/reports.js` 작성
   ```js
   import { renderTopBar, emptyState, loadingState } from './_common.js';
   export async function render(ctx) { ... }
   ```

3. **데이터 소스 결정**:
   - 리소스참고 → `indexes/<name>.json` 매니페스트 + `scripts/build-indexes.js` 에 builder 함수 추가
   - 사전구현 → `data/<name>.json` 직접 작성
   - frontmatter spec 표시 필요하면 `createSpecCard` 사용

4. **컴포넌트 재사용** 우선. 새 패턴이 필요하면 `css/components.css` 에 클래스 추가.

5. **동적 동기화**: 인덱스 빌드 후 git push (BUILDER 모드 참조).

## 새 뷰 컴포넌트 추가

공용으로 재사용할 가치가 있으면:
1. `js/components/<name>.js` 작성 (export 함수형)
2. `css/components.css` 에 스타일 추가 (BEM-lite)
3. 여러 뷰에서 import

뷰 1개에서만 쓰면 그 뷰 파일 안에 helper 함수로 둔다.

## 자주 만나는 함정

| 증상 | 원인 | 대처 |
|------|------|------|
| `Failed to fetch dynamically imported module` (Pages) | Jekyll 이 `_*.js` 무시 | `Home/.nojekyll` 존재 확인 (이미 6822624 에 추가됨) |
| Roles/Knowledge/Active Log 등 MD 가 Pages 에서 404 | `_resources/` 미러가 artifact 에 없거나 경로/대소문자 불일치 | `pages.yml` 의 build step 로그 확인 — `_resources mirror (...)` 줄에 카운트 찍히는지. 안 뜨면 paths 트리거 / Node setup 단 점검 |
| Pen 카드 모두 (0,0) 위치에 겹침 | layout 미지정 frame 을 absolute 처리 | `renderFrameNode` 에서 default `'horizontal'` |
| `v0.10.0` 가 `v0.1.0` 다음에 정렬 | 알파벳 정렬 | `localeCompare(... { numeric: true })` |
| 콘텐츠 변경 후 Pages 에 안 보임 | push 안 했거나 paths 트리거 미매칭 (`harness/**`, `Docs/**` 외) | git push 확인 → Actions 탭에서 "Deploy to GitHub Pages" 잡 실행 여부 확인 |
| 한글 폰트 깨짐 | fontFamily 단일 지정 | `"Noto Sans KR"` 폴백 체인 |
| 모달이 사이드바 위에 안 뜸 | z-index 누락 | `.modal-root { z-index: 100 }` |
| 클릭 후 빈 화면 | hash 라우팅 인식 못함 | hash 형식 `#menuId` 또는 `#menuId/params` 확인 |
| Mermaid syntax error 후 그래프 안 뜸 | `|` 문자가 link-label 로 잘못 해석 | 노드 라벨 `"..."` 또는 `·` 로 치환 |
| `loadMd` 가 404 | path가 ROOT-rel 아닌 단순 basename | items.file 이 ROOT-relative 인지 확인 |
| `.gitignore` 가 mirror 경로 차단 | VS 템플릿의 `[Ll]ogs/` 같은 광범위 룰이 underscore-prefix mirror 도 잡음 | mirror 는 어차피 gitignored 가 정답 (44e19d4 이후 의도). 미러를 git에 넣으려는 시도 자체가 안티패턴 |

## 디자인 토큰

| 용도 | 색 |
|------|----|
| 본문 텍스트 | `#111827` (heading), `#374151` (body), `#6B7280` (subtle) |
| 보더 | `#E5E7EB` |
| 배경 | `#FFFFFF` (카드), `#F0F2F5` (페이지), `#F9FAFB` (테이블 헤드) |
| Primary | `#2563EB` / hover `#1D4ED8` |
| 강조 (보라) | `#7C3AED` (Pencil 관련) |
| 뱃지 | 참고모드 `#FEF3C7/#B45309`, 편집 `#DBEAFE/#1D4ED8`, 리서치 `#F3E8FF/#7C3AED` |

## 사용자가 새 메뉴/기능을 요청할 때 체크리스트

1. **데이터 소스가 무엇인가?** 동적 .md 파일들 → 리소스참고. 외부 리서치/사전 분석 → 사전구현.
2. **권한 모드?** ReadOnly (참고) vs Read+Edit (지식형).
3. **레이아웃 타입?** 카드/탭/트리+미리보기/게시판/리서치 페이지 중 어디에 가까운가.
4. **상세 화면 필요?** 카드 클릭 후 MD 진입할지, 그 자리에서 펼칠지.
5. **사이드바 메뉴 위치?** main 섹션 vs secondary 섹션.
6. **인덱스 빌더 추가?** 새 디렉토리를 스캔해야 하면 `build-indexes.js` 에 builder 함수 추가.
7. **Frontmatter spec 표시?** `createSpecCard` 활용 고려.

체크리스트 응답이 끝나면 위 "새 메뉴 추가 절차" 따라 구현.

## 검증 흐름

### 로컬 테스트는 무조건 `127.0.0.1`

운영 URL (GitHub Pages) 인증/리다이렉트 이슈가 있을 수 있으므로, 검증 단계에서는
**로컬 서버만 사용**.

### 서버 기동

```bash
node Home/harness-view/scripts/serve.js
```

127.0.0.1:8765 바인딩 + 인증 없음 — 로컬 전용이라 안전.

### 검증 단계

1. 위 명령으로 로컬 서버 기동 (preflight 가 staleness 자동 빌드)
2. Playwright 로 `http://127.0.0.1:8765/Home/harness-view/#<menu>` 접근
3. 콘솔 에러 확인 (`browser_console_messages --level error`)
4. 스크린샷 — `tmp/playwright/<목적>.png` 경로로 저장 (`.gitignore` 등록됨)
5. 변경 commit + push → GitHub Pages 자동 배포 (사용자가 수동, 30-60초)

## 관련 외부 라이브러리

- [marked](https://marked.js.org/) — Markdown → HTML
- [mermaid](https://mermaid.js.org/) — 그래프/플로우차트 → SVG
- [lucide](https://lucide.dev/) — 아이콘 (펜 렌더러 + 확장 사이드바용)

모두 CDN. 빌드/번들 단계 없음.
