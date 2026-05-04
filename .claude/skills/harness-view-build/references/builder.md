# BUILDER — 인덱스 매니페스트 부분 재생성

> 운영 모드. 이미 만들어진 하네스뷰의 데이터를 최신화한다.
> 코드는 건드리지 않는다.

## 목적 — 그리고 현재 아키텍처 (2026-05-04 이후)

`Home/harness-view/` 정적 페이지가 보여주는 카드/트리/게시판은 두 종류의 데이터를 읽는다:
- **매니페스트** (`Home/harness-view/indexes/*.json`) — 빌드 산출물, 디렉토리 스캔 결과 캐시
- **upstream 원본** (`harness/**.md`, `Docs/**.md`) — `../../<rel>` 로 직접 fetch.
  미러 없이 단일 진실 원천.

옛날엔 `Home/_resources/` 에 `harness/` + `Docs/` 를 복제했지만 dual-management
문제 (한 파일이 두 곳에 존재) 로 2026-05-04 폐기. 빌드 스크립트는 폐기 잔재
디렉토리만 청소한다 (`cleanupLegacyMirror`).

**Pages 배포 (CI)**:
- `pages.yml` 트리거 = `doc-v*` 태그 push
- CI 가 `node Home/harness-view/scripts/build-indexes.js` 로 매니페스트만 갱신
- `actions/upload-pages-artifact path: .` — repo 전체 업로드 (artifact ~12MB)

**즉 사용자가 `harness/agents/foo.md` 같은 콘텐츠를 추가/수정한 뒤 그냥 push 만 해도
Pages 는 최신을 서빙한다.** BUILDER 를 로컬에서 돌리는 것은 **push 전 로컬 서버에서
미리 보고 싶을 때** 의 옵션이지, Pages 반영의 필수 단계가 아니다.

## 수행 절차

### 1. 빌드 스크립트 실행

```bash
node Home/harness-view/scripts/build-indexes.js
```

스크립트가 다음 디렉토리를 스캔해 9개의 매니페스트 파일을 갱신한다:

| 매니페스트 | 스캔 대상 | 사용처 |
|------------|----------|--------|
| `harness-docs.json` | `harness/docs/*.md` | Dashboard "Build Log" 섹션 (semver-desc 정렬) |
| `harness-agents.json` | `harness/agents/*.md` | Roles 메뉴 (카드 + spec card 상세) |
| `claude-skills.json` | `Docs/harness/template/<skill>/SKILL.md` | Skills 메뉴 — **본문도 인라인 임베드** |
| `harness-knowledge.json` | `harness/knowledge/**/*.md` (재귀) | Knowledge → Expert Knowledge 탭 |
| `document-tree.json` | `Docs/**/*.md` (계층) | Knowledge → Tech / Domain (TECH-DOC) 탭 |
| `harness-logs.json` | `harness/logs/<subfolder>/*.md` | Activity Log — **subfolder = 동적 카테고리 태그** |
| `design-index.json` | `Docs/design/*.md`, `*.pen` | Product Design 메뉴 (.pen 렌더) |
| `harness-engine.json` | `harness/engine/*.md` | (참고용 — Workflow 그래프는 정적 JSON) |
| `claude-tips.json` | `CLI-TIPS.md` (없으면 placeholder) | (현재 사용 안 함) |

### 1-A. 정적 데이터는 빌드 대상 아님

다음 파일들은 매니페스트가 아니라 **수동 갱신**:

- `Home/harness-view/data/news.json` — Dashboard "Recent Updates"
- `Home/harness-view/data/pdsa-insight.json` — Dashboard "PDSA Learning" (PDSA UPDATE 모드로 재생성).
  **자동 트리거 금지** — operator 가 의미 있는 활동(미션 묶음 마감, 릴리즈 게이트 통과 등)이
  쌓였다고 판단하고 명시적으로 호출할 때만 갱신한다. publish 시 자동 재합성하지 않음.
- `Home/harness-view/data/workflow-graph.json` — Workflow 메뉴의 mermaid 그래프 7개

이 파일들은 콘텐츠가 손으로 결정되는 영역이라 빌드 인덱스에 안 들어간다.

### 1-B. Contributors 통계 + velocity 카드 + top-files 는 자동 갱신

`harness-docs.json` 가 빌드 시점에 다음 4종을 모두 git log 로부터 산출한다
(scope = `harness/`, `Docs/`, `Home/harness-view/` union, SHA 로 dedup):

- `contributorsAll` / `contributorsRecent` — per-author breakdown (`gitContributors()`)
- `commitStats.{totalAllTime,last7d,last30d}` — 시간축 velocity 카드용 (`gitCommitCount()`)
- `topChangedFiles` — 최근 30일 가장 많이 변경된 파일 top 5 (`gitTopChangedFiles()`)

Scope 가 release-note 디렉토리 한 곳만 보던 옛 동작이 도큐 활동 전반을
놓쳐서 항상 1명으로 보이던 문제는 2026-05-03 union 으로 해결. 추가로
2026-05-04 단일 contributor 환경 가시성 강화 — hero stat 을 contributors
count 에서 commit count 로 교체, 7d/30d 활동 카드 + top-changed-files
패널 추가.

CI 가 doc-v* 태그 push 마다 build 를 다시 돌리므로 **publish 할 때마다
이 4종은 자동으로 최신**. 별도 손작업 필요 없음.

### 1-C. Pre-publish — 미게시 변경분 한 줄 보고

운영 게시 (`doc-v*` 태그 push) 직전, 마지막 publish 이후 도큐 영역에서
얼마나 변동이 있었는지 한 줄로 보고하면 operator 가 "지금 publish 가치가
있는지" 즉시 판단할 수 있다. 캐노니컬 명령:

```bash
LAST=$(git tag -l "doc-v*" | sort -V | tail -1)
git log --oneline "$LAST..HEAD" -- harness Docs Home/harness-view | wc -l
```

출력 = 미게시 commit 수. 0 이면 publish 의미 없음, ≥3 이면 publish 권장
정도가 휴리스틱. PDSA UPDATE / 큰 마일스톤 / Build Log 새 버전 추가
같이 publish 할 때 함께 안내한다.

### 2. 결과 확인

스크립트 출력에서 각 매니페스트의 항목 수를 확인한다:

```
✓ harness-docs.json       (7)
✓ harness-agents.json     (5)
✓ claude-skills.json      (3)
✓ harness-knowledge.json  (8)
✓ document-tree.json     (25)
✓ harness-logs.json      (31)
✓ design-index.json       (1)
✓ harness-engine.json     (2)
✓ claude-tips.json        (2)
✓ _meta.json  (trigger=manual, ~300ms)
done.
```

새로 추가한 파일이 카운트에 반영됐는지 확인할 것.

### 3. (선택) 로컬 개발 서버

원본 트리에서 즉시 확인하려면:

```bash
node Home/harness-view/scripts/serve.js
```

접속: `http://127.0.0.1:8765/Home/harness-view/`

서버 기동 시 preflight 가 자동으로 staleness 판정 → 필요시 자동 재빌드.
즉, 단순히 `serve.js` 만 띄우면 매니페스트 빌드도 함께 처리된다.

### 4. GitHub Pages 배포 — 자동

이 스킬은 git commit / push 를 직접 수행하지 않는다 (사용자 권한).
**Pages 워크플로우 (`.github/workflows/pages.yml`) 가 자동 처리** 한다:

| 시나리오 | 사용자가 push 할 것 | CI 가 하는 것 |
|---|---|---|
| 새 .md 추가 (`harness/agents/foo.md`) | 그 파일만 | `build-indexes.js` 로 indexes 갱신, repo 전체 artifact 업로드 |
| 기존 .md 본문만 수정 | 그 파일만 | 동일 — viewer 는 upstream MD 를 직접 fetch 하므로 인덱스 업데이트조차 필요 없음 |
| 인덱스 파일이 stale 한 채 push | 무관 | CI 가 인덱스 새로 덮어써 artifact 에 반영 (Pages 는 항상 신선) |

push 후 ~1분 내 Pages 재배포 완료.

**커밋 메시지 컨벤션**: 콘텐츠 변경은 `docs(<scope>): ...` 또는
`feat(harness): ...` 형식. 인덱스만 별도 커밋이 필요한 경우는 거의 없지만,
필요하면 `[harness-view] indexes refresh`.

## 사용자에게 보고

빌드 결과 출력의 변경된 카운트를 짧게 요약한다. 예:

> harness-view 인덱스 9개 재생성 완료. Build Log 7 → 8개로 증가 (v1.1.6.md 반영됨).
> 운영 반영은 사용자가 commit / push 시.

## 관련 파일

- 빌더 스크립트: `Home/harness-view/scripts/build-indexes.js`
- 매니페스트 출력: `Home/harness-view/indexes/*.json`
- 스캔되는 리소스 디렉토리: `harness/{docs,agents,knowledge,logs,engine}`, `Docs/`, `Docs/harness/template/`, `Docs/design/`

## 주의사항

- **로컬 BUILDER 의 의미**: Pages 반영용이 아니라 **로컬 dev 서버 미리보기용**.
  CI 가 push 시 동일한 빌드를 돌리므로 BUILDER 안 돌리고 push 해도 결과는 같다.
- 리소스(.md) 파일의 **본문 변경**은 어떤 빌드도 필요 없음 (뷰어가 fetch 시점에 직접 읽음).
- **Skills (`Docs/harness/template/`) 는 SKILL.md 본문이 인덱스에 인라인 임베드**되므로,
  본문 수정 시도 매니페스트 재생성이 필요. (CI 가 처리하니 push 만 하면 됨.)
- **`Home/_resources/` 는 절대 git add 금지** — `.gitignore:44` 가 막지만, 어떤
  스크립트나 사용자 실수로 unignore 되면 어제(`a55c781`)의 19k 줄 중복이 다시 들어감.
  CI-time 미러는 fresh 보장 + 단일 진실 원천 유지.
- 빌드 자체는 1초 이내, GitHub Pages 재배포는 30-60초.
- 콘텐츠 변경 commit 메시지: `docs(<scope>): ...` 또는 `feat(harness): ...` 권장.

## 정책 (이 변형의 차이)

- **언어**: 모든 산출물 (UI 텍스트, 본문, PDSA insight 등) 은 영문.
- **GitHub 자동화**: 이 스킬은 push / pages api 호출 안 함 — 사용자 권한.
- **외부 절대경로 참조 금지** — 모든 데이터는 in-repo. 외부 자원 (예: 플러그인 스킬 repo) 은 사용자가 in-repo 스냅샷으로 동기화.
- **public 여부**: 이 레포 / Pages 가 public 인지 private 인지는 사용자가 관리.
