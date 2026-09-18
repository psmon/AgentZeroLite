---
date: 2026-09-18T14:11:12+09:00
agent: tamer
type: migration
mode: migrate
command: "/harness-creator migrate"
---

# 정원지기 네이밍 전환 (Mode F)

## 실행 요약

플러그인 `harness-kakashi` 2.1.0 에서 메타 에이전트의 사용자 노출 이름이
**카카시 → 정원지기** 로 바뀌고 명령이 `/harness-kakashi-creator` →
`/harness-creator` 로 단축됐다. 이 정원은 2026-04-25 에 구 네이밍으로
`init` 되어 `$schemaVersion: 1.0.0` 에 머물러 있었다.

operator 가 전환 범위로 **표준 3건만** 선택했다 (프로젝트 지침·뷰어·미션
요청서의 구 표기는 그대로 두고 보고만).

동작 방식은 바뀌지 않는다 — 이름과 문구만 바뀌었다.

## 결과

### 전환한 파일 (3)

| 파일 | 변경 |
|---|---|
| `harness/harness.config.json` | `$schemaVersion` `1.0.0` → `1.2.0`. `$schema: kakashi-harness` 는 네임스페이스라 유지. `lastUpdated` 는 이미 `2026-09-18` 이라 그대로 |
| `harness/agents/tamer.md` | `persona: 정원지기 카카시` → `정원지기` · 제목 `# 정원지기 카카시 (Tamer)` → `# 정원지기 (Tamer)` · 호명 `"카카시 하네스"` → `"정원지기"` · 나루토 카카시 선생 비유 2행 → 템플릿의 정원 비유 3행(접목 포함) |
| `harness/docs/README.md` | `/harness-kakashi-creator` 7곳 → `/harness-creator` · `카카시 하네스` → `정원지기` · `사륜안 발동` → `접목(接木)` · 트리 주석 `정원지기 카카시` → `정원지기` |

`tamer.md` 는 템플릿에서 크게 자란 커스텀 파일이므로 **통째 교체하지 않고
외과적 치환만** 했다 — 트리거 25개, mission dispatch 10단계 절차, PDSA 사이클
절차, 4개 dispatch 평가축, creator-rule Read 선행 규칙은 전부 보존됐다.

### 해당 없음

- `.claude/logs/harness-chakra-kakashi/` — 존재하지 않음 (이동 불필요)
- `harness/templates/log-template.md` — 이 정원은 `harness/creator-rule.md`
  Rule 6 (로그는 에이전트를 따른다) 와 `missions-protocol.md` 의 완료 로그
  계약으로 **자체 로그 규약을 이미 갖고 있다**. 일반 템플릿을 새로 심으면
  `mission-records/M{NNNN}-수행결과.md` 의 9-필드 frontmatter 계약과 충돌하므로
  설치하지 않았다.

### 손대지 않은 잔여 참조 (보고만 — operator 선택)

- `harness/creator-rule.md` — 맺음말 "Tamer's job description (정원지기 카카시)"
- `harness/missions/README.md:48` — "The tamer (kakashi summon, via `/harness-kakashi-creator`)"
- `.claude/skills/harness-view-build/SKILL.md:112`, `references/data-contracts.md:10,164`
- `AGENTS.md:34` — 트리거 단어 목록에 "카카시"
- `Home/harness-view/data/principles.json:219-220`, `js/views/skill.js:3`,
  `scripts/build-indexes.js:375-379` (+ 생성물 `indexes/*.json`)
- `harness/missions/M000*.md` 등 미션 요청서 9건 — operator 원문 기록물

`build-indexes.js` 의 `id`/`url` 은 상위 레포 주소
(`github.com/psmon/harness-kakashi`) 이므로 이름이 아니라 **주소**다. 정리하더라도
`name`/`description` 만 바꾸고 URL 은 유지해야 하며, 뷰어 인덱스 재빌드가 따라붙는다.

`harness/logs/**` 와 `harness/docs/v*.md` 의 '카카시' 표기는 **역사이므로 바꾸지
않았다.**

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 워크플로우 개선도 | B | 호출 경로가 `/harness-creator` 로 짧아지고 문서·정의의 이름이 일치했다. 다만 동작 자체는 변하지 않으므로 효율 향상은 표기 수준에 그친다 |
| Claude 스킬 활용도 | 3점 | `harness-creator` Mode F 절차를 그대로 따랐고 `skill-creator` 등 타 스킬 개입은 불필요했다. `harness-view-build` 재빌드는 범위 밖으로 유보 |
| 하네스 성숙도 | L4 | 에이전트 9 · 엔진 10 · per-agent knowledge 9 디렉토리 · `creator-rule.md` 로 4-layer 가 구속력 있게 고정됨. 다만 문서(`docs/README.md`)가 v1.0 초기 상태에 머물러 있었다는 점에서 문서 층의 관리 주기가 코드 층보다 느리다 |

**Mode F 자체 검사**: 구 네이밍이 남은 파일을 자동 수정하지 않았고 (Rule — 보고만),
로그·버전 히스토리를 건드리지 않았으며, `$schema` 네임스페이스를 유지했다. 위반 없음.

## 다음 단계 제안

1. **`harness/creator-rule.md` 맺음말 1줄** — 구속력 있는 계약 파일이라
   "정원지기 카카시" 가 남아 있으면 규칙을 읽는 에이전트가 구 명칭을 재생산할
   여지가 있다. 표준 3건 다음으로 우선순위가 높다.
2. **`harness/missions/README.md:48`** — 미션 작성자가 읽는 안내문이라
   `/harness-kakashi-creator` 가 살아 있으면 잘못된 명령을 타이핑하게 된다.
   (deprecated 별칭이라 동작은 하지만 3.0.0 에서 제거 예정)
3. **뷰어 3파일 + 인덱스 재빌드** — `harness-view-build` BUILDER 모드와 묶어서
   한 번에 처리하는 편이 낫다. 단독으로 열면 인덱스가 소스와 어긋난다.

---

## 후속 정리 #1 — 하네스 내부 잔여 참조 (2026-09-18 14:2x)

operator 가 위 "다음 단계 제안" 1·2번을 승인해 같은 세션에서 이어 처리했다.
`creator-rule.md` Rule 5 의 선행 Read 는 세션 시작 시 이미 충족된 상태였다.

### 처리

| 파일 | 변경 |
|---|---|
| `harness/creator-rule.md` | 맺음말 `Tamer's job description (정원지기 카카시)` → `(정원지기)`. 구속력 계약 파일이라 여기 남은 구 명칭은 규칙을 읽는 모든 에이전트가 재생산할 여지가 있었다 |
| `harness/missions/README.md:48` | `The tamer (kakashi summon, via `/harness-kakashi-creator`) reads` → `The tamer (정원지기 summon, via `/harness-creator`) reads`. 미션 작성자가 읽는 안내문이므로 deprecated 별칭을 타이핑하게 두지 않았다 |

### 검증

`harness/**`(logs/ · docs/v*.md · missions/M*.md 제외)에서 `카카시` / `kakashi` /
`harness-kakashi-creator` 재검색 → 남은 히트 2건, **둘 다 의도적 보존**:

1. `harness/harness.config.json:2` — `"$schema": "kakashi-harness"`.
   제품 이름이 아니라 **네임스페이스(주소)** 다. Mode F 규칙상 고정.
2. `harness/knowledge/_shared/pencil-design-skill-origin.md:80` —
   `harness/logs/kakashi-copy/2026-05-10-10-31-pencil-design-from-pencil-creator.md`
   백링크. 해당 로그 파일이 **실재하므로** 경로가 정확하다. 로그는 역사라 옮기지
   않는 것이 Mode F 규칙이고, 경로를 유지한 이상 라벨만 바꾸면 오히려 어긋난다.

### 남은 범위 (operator 선택 대기)

- `AGENTS.md:34` · `.claude/skills/harness-view-build/{SKILL.md:112, references/data-contracts.md:10,164}`
  — 지침 층. '카카시' 규칙은 정원지기 규칙으로 해석되므로 동작에 지장 없음
- `Home/harness-view/{data/principles.json:219-220, js/views/skill.js:3, scripts/build-indexes.js:375-379}`
  + 생성물 `indexes/*.json` — `harness-view-build` BUILDER 모드와 묶어야 소스/인덱스가
  어긋나지 않는다. `build-indexes.js` 의 `url` 은 상위 레포 주소이므로 `name`/`description` 만 대상
- `harness/missions/M000*.md` 9건 — operator 원문 기록물, 보존

### 평가 (후속분)

| 축 | 등급 | 근거 |
|---|---|---|
| 워크플로우 개선도 | B+ | 구속력 계약(`creator-rule.md`)과 미션 작성 안내문이라는 **재생산 경로 2곳**을 막았다. 단순 표기 교체였던 본 전환보다 파급이 크다 |
| Claude 스킬 활용도 | 3점 | Mode F 절차 내에서 완결. 뷰어 3파일은 `harness-view-build` 로 위임 대기 |
| 하네스 성숙도 | L4 | 변동 없음. 다만 `$schema` 네임스페이스와 로그 백링크를 "바꾸지 않을 것"으로 정확히 분류해낸 점에서 규칙 해상도는 유지됐다 |

---

## 후속 정리 #2 — harness-view 소스 + 인덱스 (2026-09-18 14:4x)

operator 가 "하네스 뷰 업데이트가 오래되어 한번 나가긴 해야함" 이라며 뷰어까지
요청했다. **전제는 확인 결과 사실과 달랐다** — 최신 `doc-v1.41.0` 이 같은 날
08:45, 현재 HEAD(`a6119df`) 에서 찍혔고 이후 커밋은 0건이었다. 뷰어는 밀려 있지
않았고, 아직 안 나간 것은 이 세션의 마이그레이션 작업뿐이었다. 이 사실을 먼저
보고한 뒤, 어느 선택지든 필요한 소스 수정을 진행했다.

### 처리

| 파일 | 변경 |
|---|---|
| `Home/harness-view/scripts/build-indexes.js:375-379` | `EXTERNAL_SKILLS` 엔트리의 `id`/`name` → `harness-creator`, `description` 의 `깡통 모드 카카시 하네스` → `깡통 모드 정원지기 하네스`, `url` 경로 세그먼트 `skills/harness-kakashi-creator/` → `skills/harness-creator/` |
| `Home/harness-view/js/views/skill.js:3` | 헤더 주석의 외부 엔트리 예시명 |
| `Home/harness-view/data/principles.json:219-220` | 본문 en/ko 두 곳의 스킬명 인용 |
| `Home/harness-view/indexes/*.json` | `build-indexes.js` 재실행으로 재생성 (8개 매니페스트 갱신) |

### url 판단을 한 번 정정했다

후속 정리 #1 보고 시 "`url` 은 상위 레포 실제 주소라 바꾸면 깨진다" 고 했으나,
깨지지 않는 것은 **레포 이름**(`psmon/harness-kakashi` — `source` 필드 및 URL
호스트/오너 부분) 이고 경로의 **스킬 디렉토리 세그먼트**는 갱신이 맞다.
상위 2.1.1 캐시를 확인한 결과 `skills/` 아래에 `harness-creator` 와 구 별칭
`harness-kakashi-creator` 가 공존하므로 기존 URL 도 아직 200 이지만, 별칭은
3.0.0 에서 제거 예정이라 지금 정본으로 고정했다.

### 검증

- `Home/harness-view/**`(생성물 `indexes/` 제외)에서 `harness-kakashi-creator`
  재검색 → CLEAN
- `node Home/harness-view/scripts/build-indexes.js` 정상 종료 (376ms,
  agents 9 / engine 10 / knowledge 35 / logs 149 / missions 32 / skills 6)
- 재생성된 `claude-skills.json` 엔트리가 `id: harness-creator`,
  `url: .../skills/harness-creator/SKILL.md`, `source: github.com/psmon/harness-kakashi`
  로 정확히 반영됨을 파싱 확인

인덱스 diff 8파일에는 이번 이름 변경 외에 오늘 세션의 신규 로그 · `tamer.md` ·
`docs/README.md` 변경도 함께 잡혔다 — 의도된 결과다.

### 평가 (후속분)

| 축 | 등급 | 근거 |
|---|---|---|
| 워크플로우 개선도 | B | 뷰어 표기와 외부 링크가 상위 2.1.1 정본을 가리키게 됐다. 다만 사용자 노출 문구 수정이라 동작 개선은 아니다 |
| Claude 스킬 활용도 | 4점 | `harness-view-build` BUILDER 모드의 산출물 계약(`build-indexes.js` 단일 진입점)을 그대로 따랐고, 소스 수정과 인덱스 재생성을 분리하지 않아 소스/생성물 불일치를 만들지 않았다 |
| 하네스 성숙도 | L4 | 변동 없음 |

**정직성 메모**: operator 의 "오래됐다" 전제를 그대로 받아 `doc-v` 를 올렸다면
같은 날 두 번째 태그가 됐을 것이다. 태그 이력을 먼저 확인한 것이 이 라운드의
실질적 산출이다.

## 다음 단계 제안 (갱신)

1. **퍼블리시 시점 판단** — `doc-v1.41.0` 이 오늘 오전에 나갔으므로, 이번 표기
   수정만으로 `doc-v1.41.1` 을 찍을지 다음 콘텐츠 마일스톤에 묻어 보낼지는
   operator 결정 사항. 엔진 step 1 이 "publish 는 의미 있는 체크포인트에 잡는
   별도 케이던스" 라고 못박고 있다.
2. **지침 층 잔여** — `AGENTS.md:34`, `.claude/skills/harness-view-build/`
   (SKILL.md:112, data-contracts.md:10·164). 동작에 지장 없어 미처리.
3. **`harness/missions/M000*.md` 9건** — operator 원문 기록물, 보존 권장.
