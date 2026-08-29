---
date: 2026-08-29T15:30:00+09:00
agent: code-coach
type: improvement
mode: log-eval
engine: orca-adoption
phase: W9-followup
trigger: "B2 진행 (worktree feat/b2-adoption)"
follows: harness/logs/orca-adoption/2026-08-10-20-40-W9-cost-layer-done.md
---

# orca 영입 — 예산/가격표 UI (W9 비용 레이어 후속)

## 실행 요약

W9 비용 레이어(토큰→USD 계산, `TokenCostCalculator`)는 순수 계산만 있고
가격표는 하드코딩 상수, 예산/UI 부재였다. B2 채택으로 편집 가능한 가격표 +
월 예산 캡 + 초과 표시를 추가.

## 결과

### 순수 코어 (ZeroCommon, 테스트)
- `TokenCostCalculator` 에 **table-aware 오버로드**: `CostUsd(r, table)`,
  `TotalUsd(records, table)`, `TotalUsdSince(records, sinceUtc, table)`.
  (기존 `Lookup(model, table)` 재사용 — 계산식 무변경.)
- `BudgetSettings` — `MonthlyCapUsd` + `List<PriceOverride>`.
  `EffectiveTable()` = overrides(먼저 매치) + 기본표. `Evaluate(spent)` →
  `BudgetStatus{OverBudget, FractionUsed, NearingCap}`. `StartOfMonthUtc`.
- `BudgetSettingsStore` — JSON (`budget-settings.json`), LlmSettingsStore 패턴.
- **7 headless 테스트** (override 우선, 월 필터, 상태, 저장 라운드트립/복구).

### UI (WPF, 빌드 검증)
- Settings 에 **💲 Budget 탭** 신설(`SettingsPanel.Budget.cs` + XAML):
  - 월 캡 입력 + **month-to-date 지출 readout** (DB `TokenUsageRecords` 를
    이달 시작 이후로 필터 → `TotalUsd(records, EffectiveTable())`).
  - 초과 시 빨강 / 임박(≥80%) 주황 / 정상 초록 인디케이터.
  - 가격표 **DataGrid**(Key/Input/Output/Cache+/Cache-) 편집 → Save.
  - 빈 표 = 기본값 사용. DB 미가용 시 캡만 표시(graceful).

## 검증
- 헤드리스: 전체 **538 통과 / 0 실패**(+7 Budget). WPF 빌드 **오류 0**
  (Brush WinForms 모호성은 완전수식으로 해소).
- Budget 탭 실렌더/DB 지출 표시는 operator 데스크톱 스모크 대상.

## orca 영입 현황 (갱신)
- 비용 레이어: W9 계산(완료) → **가격표 편집 + 월 예산 UI(완료)**.
- 잔여(스코프 밖): 커맨드 팔레트(Cmd-J), per-account 예산, 스트리밍 알림 —
  W9 로그에서 "WPF 대공사"로 분리한 항목. 유지.

## 평가 (3축)

| 축 | 결과 |
|----|------|
| 코드 안전성 | A — 계산식 무변경, 오버라이드는 첫-매치, DB 실패 graceful. |
| 아키텍처 정합성 | Pass — 코어는 ZeroCommon 순수(테스트), 탭은 기존 Init/Load/Read/Save 패턴. |
| 테스트 가능성 | A — 예산 코어 7 테스트. UI 는 빌드 검증. |

## 다음 단계 제안
- Bot/토큰-모니터 대시보드(WebDevBridge)에 `cost.summary` op 추가해 달러 위젯 노출(선택).
- 가격표 기본값을 벤더 가격 변동 시 갱신하는 절차 문서화.
