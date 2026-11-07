---
date: 2026-11-07T07:50:00+09:00
agent: agentone-observer
type: improvement
mode: execution
trigger: "agent-one 뮤비 제작 관찰 → 1~6 개선, 분석→작업 연속 완수, 끊김 후 재개 점검"
---

# agent-one — 분석 → 작업이 이어질 때 작업을 완수하게 + 끊김 후 재개

## 실행 요약

operator가 music-harness AO 탭에서 agent-one으로 뮤비 제작을 진행하며 관찰을 요청.
관찰 → 개선점 1~6 → operator 정정("이전 세션 자동 접근은 하지 말 것, 핵심은 분석→분석 기반 작업 요청을 완수") →
같은 세션 2단계 e2e를 3회 반복하며 드러난 실패를 하나씩 수정 → 밤사이 끊긴 턴의 재개 점검·개선.
워크트리 `C:\code\psmon\AgentZeroLite-browser`, 브랜치 `feat/agent-one-workspace-grounding`.

> 관찰자 역할 경계 메모: 소스 수정·AO 재기동은 operator가 명시 요청.

## 결과

### 1. 최초 관찰 (세션 20261106-164415)

| 턴 | 요청 | 행동 |
|---|---|---|
| 분석 | 채널 /videos 열어 역사·과학 분류 | `browser_open` 1회(20K 잘림) → 11편 표 → 0.94로 에스컬레이션(169 s) → 같은 표 |
| 작업 | "종말의 그날 1,2 를 통합해 뮤비작성" | 설계(qwen 493 s)가 워크스페이스를 안 보고 `assets/in/*.mp4 (recommended)`를 지어냄 → 빈 폴더 생성 → `Test-Path a -and Test-Path b` 문법 오류 → "파일을 넣어 달라"로 반환 |

AGENTS.md(디스패치: 뮤비 → `harness/engine/mv-forge.md`)와 todo(두 편 ID·장르·화풍·금지사항)가 이미 있었다.

### 2. 개선 (테스트 747 → 789)

| 영역 | 변경 |
|---|---|
| 워크스페이스 지침 | `WorkspaceGuide`: 루트 AGENTS.md/CLAUDE.md를 시스템 프롬프트(6 000자, 헤딩 컷 + 나머지 목차)와 설계(4 000자)에 |
| 요청어 탐색 | `WorkspaceProbe`: 조사 제거 한국어·IDF 가중, 텍스트 파일 ≤6 000개/3 s → `[workspace]` 자료 (music-harness 1 097개 0.3 s) |
| 분석→작업 연속 | `EarlierAnswers`: 직전 답 2개(각 2 500자)를 설계·에스컬레이션에 (기존엔 첫 줄만) |
| 반환 검사 | `HandBackAsync`: 탐색이 찾은 파일을 안 읽고 "넣어 달라"면 1회 되돌림 |
| 기억 | memory.md 결과를 마크업 제거 600자로, worth-saving에 "조회한 사실(제목·날짜·ID·링크)" 명시, 에스컬레이션 후에도 도구 기록 유지 |
| 조회형 에스컬레이션 | 웹 경로 + 자료가 있으면 ≥ 0.95일 때만, 초안이 페이지 단어 ≥ 60 %면 안 함 |
| 빌드 반복 | `AgentLoop.Building`: 설계 수행 중 반복 넛지 3회 + "다음 단계를 써라" |
| 파서/프롬프트 | gemma `key:="…"` 문자열 처리, 읽기는 read_file, 설계 ≤ ~3 000자, PowerShell `(Test-Path a) -and (Test-Path b)` 힌트, yt-dlp 파이썬 모듈 감지 |
| 끊김/재개 | 스트림 중 IOException → `Stalled{Dropped}` 자동 재시도 2회, 그 외 예외는 단계 보존한 `ProviderError`, `/resume`이 미완료 턴(요청·이유·단계·설계 머리) 복원, 짧은 "재개"는 빌드 허용치 |
| 언어 | `WrongScript`를 산문 비율(코드 제외, ≥ 10 %)로 — 제목만 한글인 영어 답이 통과하던 구멍 |

operator 정정으로 되돌린 것: 세션 로그 자동 검색(이전 세션은 `/resume`으로만).

### 3. e2e (같은 세션: 분석 → 작업)

| 회차 | 분석 | 작업 결과 |
|---|---|---|
| 1 | 5분(에스컬레이션) | 설계는 AGENTS.md·mv-forge·4파일 근거로 바뀜. 실행 9단계 후 todo 재읽기 2회 → Repeat 종료, 산출 0 |
| 2 | 76 s (에스컬레이션 생략, 지식 저장됨) | 설계 1 002 s(7 351자), gemma `:="Get-Content…"` 3연속 실패 → Repeat |
| 3 | 70 s | 설계 단계 qwen 스트림 무응답(535 s) → 설계 없이 진행 → lore 작성 후 연결 끊김으로 턴 소실(결과·기억 없음) → operator "재개" → 같은 프로세스 문맥으로 이어서 `lyrics.md`·`guide.md`·`mv/plan.md` 작성, Final |

실측 산출(3회차): `harness/knowledge/lore/apocalypse-day-combined.md`, `songs/2026-11-07-apocalypse-day/{lyrics,guide}.md`, `…/mv/plan.md`.
재개 점검: 실제 끊긴 세션 파일을 `Resume`하면 미완료 턴이 요청·진행 단계와 함께 복원됨(검증).

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| Guidance adherence | B | 3회차에서 AGENTS.md 디스패치·mv-forge·todo 제약을 반영해 산출물 작성. 단 mv-forge의 입력(채택 테이크 `pick.mp3`)·검문소 G1~G7은 아직 실행 대상 아님(곡 생성 전) |
| Completion stability | C | 3회 중 1회만 Final(재개 포함). 실패 원인 3종(반복 넛지·gemma 인자·연결 끊김) 모두 수정·테스트됨, 수정 후 재관찰 필요 |
| Safety-gate behaviour | Pass | 명령은 엔진 safe 판정 후 실행, 워크스페이스 밖 쓰기 없음, 사용자 브라우저는 attach/detach |
| Real progress | B | 분석 → 작업 연속으로 lore·가사·지침·MV 계획 4파일. "재개" 답이 영어였음(언어 검사 수정) |

## 다음 단계 제안

- 머지·릴리스 후 같은 2단계 체인을 끊김 없이 1회 재관찰: Building 넛지·설계 길이·read_file 사용이 실제로 반복 종료를 없애는지.
- 설계(qwen) 지연: 475–1 002 s, 1회는 무응답. 설계 타임아웃/모델 선택 재검토 후보.
- mv-forge 본 경로(곡 생성 → pick.mp3 → 온셋 → G1~)는 다음 관찰 대상.

### Plan
분석 → 분석 기반 작업 요청이 이어질 때 작업이 끝까지 가게 하고, 끊겨도 진행 지점에서 이어지게 한다.

### Do
관찰 → 개선 1~6 → 정정 반영 → e2e 3회(회차마다 드러난 실패 수정, 테스트 추가) → 실제 끊김 세션으로 재개 경로 검증·개선.

### Study
근거 있는 설계: met. 연속 맥락 전달: met. 작업 완수: partial(3회 중 1회, 재개 후). 끊김 복구: met(자동 재시도·단계 보존·resume 복원, 실세션 검증).

### Act
1. 커밋·머지·릴리스 여부를 operator에게 확인.
2. 릴리스 후 같은 체인 재관찰로 Completion stability 재평가.
