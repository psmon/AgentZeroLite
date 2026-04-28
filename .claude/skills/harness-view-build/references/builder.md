# BUILDER — 인덱스 매니페스트 부분 재생성

> 운영 모드. 이미 만들어진 하네스뷰의 데이터를 최신화한다.
> 코드는 건드리지 않는다.

## 목적

`Home/harness-view/` 정적 페이지가 보여주는 카드/트리/게시판은 모두
`Home/harness-view/indexes/*.json` 매니페스트에서 데이터를 읽어온다.

리소스(.md / .pen) 파일이 추가/삭제/이름 변경되어도 매니페스트가 갱신되지 않으면
뷰어에는 반영되지 않는다. 이 모드는 매니페스트를 한 번에 부분 재생성한다.

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
- `Home/harness-view/data/pdsa-insight.json` — Dashboard "PDSA Learning" (PDSA UPDATE 모드로 재생성)
- `Home/harness-view/data/workflow-graph.json` — Workflow 메뉴의 mermaid 그래프 7개

이 파일들은 콘텐츠가 손으로 결정되는 영역이라 빌드 인덱스에 안 들어간다.

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

### 4. (선택) GitHub Pages 배포

이 스킬은 git commit / push 를 직접 수행하지 않는다.
GitHub Pages 연동은 이미 별도 구성되어 있어, 사용자가 수동으로:

```bash
git add Home/harness-view/indexes/
git commit -m "[harness-view] indexes refresh"
git push
```

push 후 GitHub Pages 가 자동 재배포한다 (보통 30-60초).

## 사용자에게 보고

빌드 결과 출력의 변경된 카운트를 짧게 요약한다. 예:

> harness-view 인덱스 9개 재생성 완료. Build Log 7 → 8개로 증가 (v1.1.6.md 반영됨).
> 운영 반영은 사용자가 commit / push 시.

## 관련 파일

- 빌더 스크립트: `Home/harness-view/scripts/build-indexes.js`
- 매니페스트 출력: `Home/harness-view/indexes/*.json`
- 스캔되는 리소스 디렉토리: `harness/{docs,agents,knowledge,logs,engine}`, `Docs/`, `Docs/harness/template/`, `Docs/design/`

## 주의사항

- 리소스(.md) 파일의 **본문 변경**은 빌드 불필요 (뷰어가 fetch 시점에 직접 읽음).
- 단, **GitHub Pages 반영을 위해 git push 는 필요** — 본문이 수정되었는데 뷰어에 안 보이면 push 여부부터 확인.
- **파일 추가/삭제/이름 변경** 시 인덱스 빌드 + git commit + push 가 모두 필요.
- **Skills (`Docs/harness/template/`) 는 SKILL.md 본문이 인덱스에 인라인 임베드**되므로,
  본문 수정도 매니페스트 재생성이 필요. (다른 매니페스트는 본문 수정 시 재빌드 불필요.)
- 빌드 자체는 1초 이내, GitHub Pages 재배포는 30-60초.
- 인덱스 변경 commit 메시지 컨벤션: `[harness-view] indexes refresh` 권장.

## 정책 (이 변형의 차이)

- **언어**: 모든 산출물 (UI 텍스트, 본문, PDSA insight 등) 은 영문.
- **GitHub 자동화**: 이 스킬은 push / pages api 호출 안 함 — 사용자 권한.
- **외부 절대경로 참조 금지** — 모든 데이터는 in-repo. 외부 자원 (예: 플러그인 스킬 repo) 은 사용자가 in-repo 스냅샷으로 동기화.
- **public 여부**: 이 레포 / Pages 가 public 인지 private 인지는 사용자가 관리.
