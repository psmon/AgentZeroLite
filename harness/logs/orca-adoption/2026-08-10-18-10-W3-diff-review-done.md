---
date: 2026-08-10T18:10:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W3 diff 리뷰 + 인라인 주석 재투입"
engine: orca-adoption
phase: W3
---

# W3 — Diff 리뷰 + 인라인 주석 → 에이전트 재투입 완료

## 실행 요약

활성 워크스페이스의 working-tree git diff를 렌더링하고, 라인에 인라인 주석을 달아
에이전트에 후속 프롬프트로 재투입. **설계 변경**: Monaco(대용량 오프라인 번들 필요)
대신 **자체 완결 HTML diff 뷰**(vanilla JS + WebView2 메시지 브리지)로 오프라인·CSP 안전하게.

## 결과 (편집 지점)

- `Project/ZeroCommon/Module/GitDiffReader.cs` **(신규, 순수 파서)** — unified diff → 파일/hunk/라인
  모델. 라인번호(old/new) 추적, add/del/context, binary·new·deleted 플래그.
- `Project/ZeroCommon/Data/Entities/DiffComment.cs` **(신규)** + `AppDbContext` DbSet+인덱스.
- `Project/ZeroCommon/Data/Migrations/20260810111027_AddDiffComments.cs` — `dotnet ef`(빌드 후,
  --no-build 금지). CreateTable 검증됨(비어있지 않음).
- `Project/AgentZeroWpf/Services/GitDiffService.cs` **(신규)** — `git diff HEAD -U3 --no-color`
  shell-out(commit 없는 repo는 plain diff 폴백) → `GitDiffReader` 파싱.
- `Project/AgentZeroWpf/UI/Components/DiffReviewPanel.xaml(.cs)` **(신규)** — WebView2 자체완결
  HTML diff, 라인 클릭→인라인 주석박스→`postMessage`→C# 영속(`DiffComment`), "Ship N to agent" 버튼.
- `Project/AgentZeroWpf/UI/APP/MainWindow.xaml(.cs)` — ActivityBar `btnActivityDiff` + 오버레이 +
  `OnActivityDiffClick`/`CloseDiffReview`(Harness 미러) + 형제 핸들러 `CloseDiffReview()` 연동 +
  `ConfigureDiffReviewPanel`(워크스페이스 루트 provider + ship→`PostAiRequest`).
- `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` — `PostAiRequest(text)` public — ship 경로가
  타이핑 입력과 동일한 `SendThroughAiToolLoopAsync`(바인딩 와이어링 포함)로 진입.

## 검증

- `dotnet test ...ZeroCommon.Tests --filter Category=GitDiff` → **7/7 통과**(파서: 단일/다중 파일,
  라인번호·kind, new/binary 플래그, 멀티 hunk, empty). 초기 3 실패(trailing-newline artifact를
  context로 오인) → length-0 라인은 hunk 종료로 수정 후 전부 통과.
- 전체 회귀: `dotnet test ...ZeroCommon.Tests` → **353 통과, 24 스킵(모델 의존), 0 실패**.
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.
- ⚠️ **런타임 UI 검증 미수행** — WebView2 + 데스크톱 세션 + git 워크스페이스 필요(헤드리스 환경 밖).
  operator 데스크톱 스모크 필요: ActivityBar Diff 버튼 → diff 렌더 → 라인 주석 → Ship → 봇 AI 수신.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | git shell-out은 워크스페이스 루트 스코프, 읽기 전용(diff). WebView2 자체완결(외부 리소스 0). |
| 아키텍처 정합성 | Pass | 파서 ZeroCommon(헤드리스), git 실행 WPF. ship은 기존 AI-loop 경로 재사용(중복 배선 없음). |
| 테스트 가능성 | B+ | 파서·엔티티·마이그레이션 헤드리스 검증. UI/브리지는 런타임 검증 불가(환경 제약). |
| 이식 충실도 | Pass | orca diff-comments 개념(라인 앵커 주석→에이전트) 이식, Monaco 대신 경량 자체 구현. |
| 스코프 규율 | Pass | Monaco 번들 회피로 Lite 경량 유지. |

## 다음 단계 제안

- **operator 데스크톱 스모크** — UI/WebView2/git 경로는 실기기 확인 필요.
- 주석 side가 old/new 혼재 시 라인 정합 정교화 여지(현재 del=old, 그 외=new).
- Staged/Working 토글, 파일별 접기, 커밋 생성은 후속.
- Sprint 3(W6 오케스트레이션) 남음 — 사용자 선택 마지막 항목.
