---
name: harness-view-build
description: |
  AgentZero Dev 하네스 뷰어(Home/harness-view/) 의 부분 업데이트 빌드 스킬.
  콘텐츠가 추가/수정되면 인덱스 매니페스트만 부분 재생성해 즉시 UI에 반영한다.

  네 가지 모드:
   1) BUILDER 모드 — 인덱스 매니페스트 재생성 (콘텐츠 변경 반영용)
   2) CREATOR 모드 — 뷰어 코드 자체를 만들거나 개선 (HTML/CSS/JS)
   3) DEV SERVER 모드 — 로컬 dev 서버(127.0.0.1:8765, 인증 없음) 구동
   4) PDSA UPDATE 모드 — harness/logs 최신 5개를 분석해 대시보드 PDSA 섹션 데이터 갱신

  사용자 발화에 따라 네 모드 중 하나로 분기한다.

  BUILDER 모드 트리거 (운영 — 컨텐츠 갱신):
  - "하네스 뷰어 최신으로 갱신", "하네스뷰어 갱신", "harness-view 갱신"
  - "하네스 뷰어 빌드", "harness-view 빌드", "뷰어 인덱스 재생성"
  - "하네스 뷰어 새로고침", "뷰어 매니페스트 다시"
  - "하네스 뷰어에 OOO 안 보임", "방금 추가한 파일이 뷰어에 안 보임"
  - harness/{docs,agents,knowledge,logs,engine}, Docs/, Docs/design,
    Docs/harness/template 중 하나라도 추가/삭제/수정 후 "뷰어에 반영" 언급 시 트리거.

  CREATOR 모드 트리거 (개발 — 뷰어 자체 손보기):
  - "하네스뷰 OOO 개선해줘", "하네스뷰어 OOO 개선", "harness-view OOO 개선"
  - "하네스뷰에 OOO 추가해줘", "하네스뷰어 OOO 메뉴 추가"
  - "하네스뷰 OOO 화면 만들어줘", "하네스뷰어 새 뷰", "harness-view 새 메뉴"
  - "하네스뷰 OOO 버그", "하네스뷰어 깨짐 수정", "harness-view 디자인 변경"
  - "하네스뷰 펜슬렌더 개선", "하네스뷰 MD 뷰어 개선", "spec card 개선"
  - Home/harness-view/ 하위 css/js/index.html 파일을 수정·생성해야 하는 모든 요청

  DEV SERVER 모드 트리거 (검증 — 로컬 서버 기동):
  - "하네스뷰 데브서버 구동해", "하네스뷰 데브서브 구동해", "하네스뷰 dev 서버 구동"
  - "하네스뷰 로컬 서버 띄워", "하네스뷰 서버 기동", "harness-view 로컬 서버"
  - "하네스뷰 로컬 테스트 준비", "하네스뷰 playwright 서버", "harness-view serve"
  - harness-view 를 Playwright 로 검증하기 직전 로컬 서버가 필요하면 무조건 트리거

  PDSA UPDATE 모드 트리거 (콘텐츠 — 최근 로그 분석→인사이트 갱신):
  - "하네스뷰 PDSA 업데이트", "하네스뷰 PDSA 업데이트해", "하네스뷰 PDSA 갱신"
  - "PDSA 학습 다시 뽑아", "PDSA 인사이트 새로 정리", "PDSA 다시 분석"
  - "대시보드 PDSA 최신화", "harness-view PDSA refresh"
  - harness/logs 에 새 로그가 쌓였고 대시보드의 PDSA 섹션을 최신화하고 싶을 때 트리거
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

# harness-view-build — 부분 업데이트 빌드 스킬 (AgentZero Dev 변형)

이 스킬은 네 가지 책임을 가진다. 사용자 발화에서 모드를 판별한 뒤,
BUILDER/CREATOR 는 해당 reference 파일을 **즉시 Read 도구로 읽어** 그 안의 절차를 따르고,
DEV SERVER / PDSA UPDATE 는 본 파일 하단 인라인 절차를 바로 수행한다.

> **이 변형의 정체성**
> 본 스킬은 다른 프로젝트의 하네스 뷰어 빌드 스킬을 베이스로 가져와
> AgentZeroLite 에 맞게 적응된 버전이다. 핵심 차이:
> - **위치**: `harness-view/` → **`Home/harness-view/`** (Home 하위에 둠)
> - **사이트 제목**: **`AgentZero Dev`** (sidebar logo + page title)
> - **언어**: 산출물 모두 **영문 전용** (UI 라벨, 빈 상태, PDSA 본문 등). 사용자 대화는 한국어.
> - **GitHub Pages 배포 = `.github/workflows/pages.yml`** 가 자동 처리.
>   `paths: Home/** + harness/** + Docs/** + pages.yml` 매칭 시 발동 →
>   CI 가 `node Home/harness-view/scripts/build-indexes.js` 로 매니페스트 +
>   `Home/_resources/{Docs,harness}` 미러를 새로 만든 뒤 `Home/` 를 업로드.
>   **단일 진실 원천 = `harness/` + `Docs/` 원본.** `Home/_resources/` 는
>   gitignored (44e19d4 이후) — git에 안 들어감, CI 가 매번 빌드.
> - **`Home/.nojekyll` 필수** (이미 추가됨, 6822624). underscore-prefix
>   디렉토리 (`_resources/`) 를 Jekyll 이 제외하지 못하도록 함.
> - **외부 절대경로 참조 안티패턴 금지** — 모든 데이터 소스는 in-repo.

## 데이터 소스 매핑 (canonical for this project)

| 화면 영역 | 소스 경로 | 비고 |
|---|---|---|
| Dashboard "Build Log" 섹션 | `harness/docs/*.md` | semver-desc 정렬 (vN.N.N 카드) |
| Dashboard "Recent Updates" | `Home/harness-view/data/news.json` | **정적**. 수동 갱신 |
| Dashboard "PDSA Learning" | `Home/harness-view/data/pdsa-insight.json` | **정적**. PDSA UPDATE 모드로 재생성 |
| Dashboard "Contributors" | git log on `harness/docs` | 자동 |
| Workflow | `Home/harness-view/data/workflow-graph.json` | **정적**. mermaid 그래프 7개 (스킬 시스템 노드는 의도적 제외) |
| Roles | `harness/agents/*.md` | 동적. 상세 페이지에 spec card |
| Skills | `Docs/harness/template/<skill>/SKILL.md` | **in-repo 스냅샷**. SKILL.md 본문은 인덱스에 인라인 임베드. 사용자 수동 동기화 |
| Knowledge → Expert | `harness/knowledge/**/*.md` (재귀) | 동적 |
| Knowledge → Tech / Domain (TECH-DOC) | `Docs/**/*.md` | 동적 트리 |
| Activity Log | `harness/logs/<subfolder>/*.md` | 동적. **subfolder 이름 = 카테고리 태그** (qa/ba/code/cto 같은 hardcoded 없음) |
| Product Design | `Docs/design/*.pen` | 동적. parsePen + renderFrame |
| Product Intro | `Home/index.html` | iframe 임베드 |

## 모드 판별

| 사용자 의도 | 모드 | reference |
|------------|------|-----------|
| 새 .md 파일 추가 후 뷰어에 반영 | **BUILDER** | `references/builder.md` |
| 운영 사이트 갱신 / 매니페스트 재빌드 | **BUILDER** | `references/builder.md` |
| 새 메뉴 / 화면 만들기 | **CREATOR** | `references/creator.md` |
| 뷰어 컴포넌트 / 디자인 개선 | **CREATOR** | `references/creator.md` |
| 펜슬 렌더 / MD 뷰어 / spec-card 수정 | **CREATOR** | `references/creator.md` |
| 버그 수정 (코드 변경 필요) | **CREATOR** | `references/creator.md` |
| 로컬 dev 서버 기동 (Playwright 검증 준비) | **DEV SERVER** | 본 파일 아래 절차 |
| 대시보드 PDSA 섹션 최신화 (로그 5개 재분석) | **PDSA UPDATE** | 본 파일 아래 절차 |
| 컨텐츠와 코드 둘 다 변경 필요 | **CREATOR → BUILDER** 순차 |

판별이 모호하면 사용자에게 한 줄 확인.

## DEV SERVER 모드 — 로컬 dev 서버 기동 (인라인 절차)

사용자가 "하네스뷰 데브서버 구동해" 등을 말하면, reference 파일 읽지 말고 **즉시 아래 절차 수행**.

### 절차

1. **Bash 로 background 실행** (server 는 상주 프로세스라 `run_in_background: true` 필수)
   ```
   node Home/harness-view/scripts/serve.js
   ```
   - 포트 지정: `node Home/harness-view/scripts/serve.js 9000`
   - 메타 자동빌드 skip: `node Home/harness-view/scripts/serve.js --no-build`

2. **Preflight 로그 해석** — serve.js 는 기동 전 메타데이터 staleness 를 자동 판단:
   - `[preflight] rebuild — ...` : 소스가 최신 → `build-indexes.js` 자동 실행 후 기동
   - `[preflight] up-to-date — ...` : 이미 최신 → skip 하고 바로 기동
   - `[preflight] skipped — --no-build flag` : 사용자가 명시 skip

3. **기동 확인** — Bash 출력에 다음이 보이면 OK
   ```
   AgentZero Dev Harness — local server (no auth)
   URL        : http://127.0.0.1:8765/Home/harness-view/
   Screenshots: tmp/playwright/ (gitignored)
   Build log  : Home/harness-view/.meta-build.log (gitignored)
   ```
4. **이미 떠 있으면** (`EADDRINUSE`) — 새로 띄우지 말고 URL 만 안내.
5. **사용자에게 한 줄 보고**: URL + preflight 결과 (rebuild / up-to-date) + 종료법.

### 메타데이터 추적 체계

- **`Home/harness-view/indexes/_meta.json`** : 마지막 빌드 시각·duration·trigger·스캔 경로 스냅샷.
- **`Home/harness-view/.meta-build.log`** : 빌드 이벤트 append-only 로그 (gitignored).
- **Staleness 판정** : `_meta.json.builtAtMs` vs 스캔 경로들 중 최대 mtime. 소스가 최신이면 재빌드.
- **Trigger 값** : `manual` (CLI 직접) / `serve` (serve.js preflight) / `BUILD_TRIGGER` env.

### Playwright 검증 연계

DEV SERVER 모드로 서버 기동 후, 사용자가 Playwright 검증 요청 시:
- `mcp__playwright__browser_navigate` → `http://127.0.0.1:8765/Home/harness-view/#<menu>`
- `mcp__playwright__browser_take_screenshot filename="tmp/playwright/<name>.png"` (gitignored 경로)
- 운영 URL (GitHub Pages) 은 이 스킬에서 직접 검증 안 함 — push 후 사용자가 확인.

### 왜 Node 서버인가

- `py -m http.server` 는 Python 환경 의존. Node 는 repo 내 다른 스크립트들도 이미 사용.
- `serve.js` 는 `127.0.0.1` 로만 바인딩 + 인증 없음 = 로컬 전용 안전.
- Cache-Control: no-store 로 개발 중 캐시 이슈 없음.

## PDSA UPDATE 모드 — 최근 로그 분석→PDSA 섹션 갱신 (인라인 절차)

사용자가 "하네스뷰 PDSA 업데이트해" 등을 말하면, reference 파일 읽지 말고 **즉시 아래 절차 수행**.
대시보드의 PDSA 학습 섹션이 읽는 `Home/harness-view/data/pdsa-insight.json` 을 재생성한다.

### 절차

1. **대상 로그 선정** — `Home/harness-view/indexes/harness-logs.json` 의 `items` 에서:
   - `date` 가 오늘부터 **14일 내**
   - 최신순(이미 정렬됨) 상위 **5건**
   - 카테고리는 혼합 허용 (subfolder 이름이 그대로 카테고리)

2. **각 로그 파일 읽기** — `Read harness/logs/<category>/<file>`
   - 앞부분 40~80줄 정도면 충분.

3. **PDSA 4관점으로 합성** — **모든 본문은 영문**:
   - **tried (Plan + Do)** : 무엇을 시도했나. 3~5 불릿.
   - **solved** : 완료/확정된 것. 2~4 불릿.
   - **remaining** : 미해결/차단/다음 사이클. 2~4 불릿.
   - **learned (Study + Act)** : **가장 중요**. 반복된 Fail · 우연한 발견 · 재사용 가능한 프리미티브.
     - `lead` : 한 문장 핵심 통찰
     - `body` : 2~4문장 상세 근거 + 다음 액션

4. **`Home/harness-view/data/pdsa-insight.json` 덮어쓰기**:
   ```json
   {
     "analyzedAt": "YYYY-MM-DD",
     "windowDays": 14,
     "sources": [
       { "date": "...", "time": "...", "category": "<subfolder>", "file": "harness/logs/<subfolder>/...md", "title": "..." }
     ],
     "tried":     ["...", "..."],
     "solved":    ["...", "..."],
     "remaining": ["...", "..."],
     "learned":   { "lead": "...", "body": "..." }
   }
   ```

5. **인덱스 재빌드 불필요** — `data/*.json` 은 매니페스트 대상 아님.

6. **사용자 보고** — 분석된 로그 5건 + 핵심 학습 리드 + 로컬 URL.

## 수행 패턴

1. **모드 결정** — 위 표에서 매칭.
2. **reference 로드** — `Read .claude/skills/harness-view-build/references/<mode>.md`
3. **그 안의 절차 그대로 수행**.
4. **변경분 보고** — 변경 파일 / URL / 빌드 상태.
5. **git commit / push 는 사용자 명시 요청 시에만** — push 후 `pages.yml` 이
   자동으로 매니페스트·미러 재빌드 + Pages 재배포까지 처리한다 (~1분).

## CI-time 미러 메모 (44e19d4 이후)

콘텐츠 추가/수정 → push 후 Pages 반영까지의 흐름이 단순해졌다:

```
edit harness/agents/foo.md
   → git add/commit/push     (원본만)
   → pages.yml 트리거 (paths: harness/** 매칭)
   → CI: node build-indexes.js  (indexes/*.json + Home/_resources/{Docs,harness} 새로 생성)
   → CI: actions/upload-pages-artifact path=Home
   → Pages 재배포 (30~60초)
```

**Claude 가 이 스킬에서 알아야 할 것**:
- 사용자가 `harness/`, `Docs/` 에 새 파일을 추가한 뒤 "뷰어에 반영" 류로 트리거하면,
  **로컬에서 BUILDER 를 굳이 돌리지 않아도** push 만 하면 CI 가 처리한다.
- 단, **로컬 dev 서버로 push 전에 미리 보고 싶을 때** BUILDER (또는 그냥 serve.js)
  를 돌리는 가치는 그대로 — 그건 "preview" 의도지 "Pages 반영" 의도가 아니다.
- 매니페스트 (`Home/harness-view/indexes/*.json`) 는 아직 git tracked.
  CI 가 push 전 인덱스를 덮어써 artifact 에 반영하므로 stale 한 채 push 해도
  Pages 는 신선한 인덱스를 서빙. git tree 의 인덱스는 약간 lag 할 수 있음 (functional issue 없음).
- **`Home/_resources/` 는 절대 git add 하지 말 것** — `.gitignore:44` 가 막지만,
  실수로 unignore 추가하면 어제(`a55c781`)의 19k 줄 중복이 다시 들어감.

## 컨텍스트 — AgentZero Dev 하네스 뷰어란

`Home/harness-view/` 는 AgentZeroLite 레포 안의 **개발용 정적 사이트**다.
- 설계 First: Pencil(.pen) 로 화면 설계 후 HTML 로 구현
- 디자인 원본: **`Docs/design/design.pen`** (앱디자인 단일 펜) — Product Design 메뉴에 노출
- 두 가지 데이터 모드: 리소스참고 (동적 매니페스트) / 사전구현 (정적 JSON)
- 산출물 언어: **영문 전용** — 외부 글로벌 audience 대상

자세한 디렉토리 구조·라우팅·컴포넌트 패턴은 `references/creator.md`,
빌드 명령은 `references/builder.md` 참조.

## 제약

- 산출물 (UI 텍스트, 메뉴 라벨, 빈 상태 메시지, 본문) 은 모두 **영문**.
- 사용자와의 대화는 한국어.
- 빌드/번들 도구를 추가하지 않는다 — vanilla HTML + ES Modules + CDN 만 사용.
- GitHub Pages 배포 자동화는 이 스킬 범위 밖. 사용자가 직접 commit/push.
- **외부 절대경로 참조 금지** — 모든 데이터는 in-repo. 외부 시스템에서 가져오는 자원은 사용자가 in-repo 스냅샷으로 동기화한다 (예: Skills → `Docs/harness/template/`).
