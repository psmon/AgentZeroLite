---
date: 2026-08-29T14:10:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "B1을 진행해죠"
follows: harness/logs/tamer/2026-08-29-13-33-pdsa-cli-integration.md
---

# 개선 브리핑 B1 채택 — 신뢰성/코어 저비용 3건

최근 하네스 로그 브리핑에서 operator 가 **B1(신뢰성/코어)** 묶음을 채택 지시.
세 갈래를 처리했다.

## 실행 요약

### B1-1 · pr-review hazard 테이블 정정 (완료)
`gh` 로 #13·#14·#15 전부 **CLOSED** 확인. 해결 근거도 코드에서 실측:
- #13 WinExe stdout → `launch-self-smoke.ps1` 이 `Start-Process -NoNewWindow -Wait
  -PassThru -RedirectStandardOutput` 로 전환 (주석에 "GitHub #13, finding 1" 명시).
- #13 element-tree hang → `os element-tree --timeout-sec`(기본15, clamp 1~300).
- #15 NU1903 → `System.Security.Cryptography.Xml` 10.0.7 → **10.0.11** (`ZeroCommon.csproj`).
- #14 → 이슈 closed.

`harness/engine/pr-review.md` "Known gate hazards" 를 **"Currently open: none
(2026-08-29)"** + 해결내역 표로 갱신. stale 하던 `skip_e2e` 의 "#13 still open"
문구도 일반화. 엔진 자체 규칙("cry wolf 금지")을 준수.

### B1-2 · REST self-correction 잔여 갭 수정 (완료)
PR#12 Should-fix 가 실재함을 코드에서 확인 — `ExternalAgentLoop.cs` 의 세 "라우팅
불가" 실패 분류 중 **1개만** self-correction 을 태우고 있었다:
- no-JSON-envelope → 교정 O (기존)
- **unparseable JSON(non-done)** → done-repair 실패 시 그냥 `break` ✗
- **unknown tool** → 그냥 `break` ✗ (`BuildFormatCorrectionInstruction` 이
  KnownTools 를 나열하는데도 미사용)

수정: 두 경로에 `TryOfferFormatCorrection` 배선 (공유 per-instance 예산으로
여전히 fail-fast). XML doc 도 "unknown tool" 커버 명시하도록 갱신.

테스트: `Loop_falls_through_when_inner_payload_lacks_message_field` 를 자가교정
회귀 테스트로 리퍼포즈 + 신규 2건(unknown-tool 복구 / cap 소진 fail-fast) 추가.

### B1-3 · pr-review 첫 정식 수행 (보류)
`gh pr list --state open` **빈 결과** → 열린 PR 부재로 지금 수행 불가.
실 PR 생성 시 트리거 예정.

## 결과 / 검증

| 게이트 | 결과 |
|---|---|
| `dotnet test ZeroCommon.Tests --filter ExternalAgentLoopTests` | **23 통과 / 0 실패** |
| `dotnet test ZeroCommon.Tests` (전체 headless) | **517 통과 / 25 skip / 0 실패 (66s)** |
| 빌드 | 신규 경고 없음 (기존 CS8620/xUnit2013 무관) |

변경 파일:
- `harness/engine/pr-review.md` (hazard 테이블 + skip_e2e 문구)
- `Project/ZeroCommon/Llm/Tools/ExternalAgentLoop.cs` (2 경로 교정 배선 + doc)
- `Project/ZeroCommon.Tests/ExternalAgentLoopTests.cs` (테스트 3 신규/리퍼포즈)

## 평가 (3축)

| 축 | 결과 |
|----|------|
| 워크플로우 개선도 | **A** — stale 문서 정정 + 실증된 코어 버그 수정, 전 구간 측정 검증. |
| Claude 스킬 활용도 | **3/5** — gh(이슈 상태), dotnet test(test-runner 계약) 활용. |
| 하네스 성숙도 | **L4** — 엔진 문서 self-consistency 회복. B1-3(첫 수행)·hazard 자동 동기화 훅이 L5 잔여. |

## 다음 단계 제안
- **B1-3**: 다음 실 PR 에서 pr-review 트리거 → `harness/logs/pr-review/` 첫 로그.
- hazard 테이블 **자동 동기화 훅** — 이슈 종결 시 행 제거를 mission-dispatch/pr-review
  마감 절차에 거는 방안 검토 (브리핑 #20, L4→L5 게이트).
- 남은 브리핑 묶음(B2 orca/herdr, B3 지식/테스트 부채, B4 제품 폴리시)은 operator 채택 대기.
