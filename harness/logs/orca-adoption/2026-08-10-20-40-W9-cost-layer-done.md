---
date: 2026-08-10T20:40:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W9 $ 비용 레이어 + 커맨드 팔레트"
engine: orca-adoption
phase: W9
---

# W9 — $ 비용 레이어 완료 (커맨드 팔레트는 follow-up)

## 실행 요약

기존 토큰 텔레메트리(`TokenUsageRecord`) 위에 모델별 가격표를 얹어 $ 비용 추정.
캐시 read/write를 input/output과 별도 가격으로 계산. orca claude-usage 개념 이식.

## 결과

- `Project/ZeroCommon/Telemetry/TokenCostCalculator.cs` **(신규, 순수)** — `ModelPricing`(4-way
  per-MTok), substring 매칭 가격표(opus/sonnet/haiku/gpt/o3), `CostUsd`/`TotalUsd`/`ByModel`.
  미지 모델=0. 가격은 편집 가능 기본값(라이브 피드 아님).
- `Project/AgentZeroWpf/CliHandler.cs` — `-cli cost`(in-process DB 집계, 모델별 분해 출력) + usage.
- `Project/ZeroCommon.Tests/TokenCostCalculatorTests.cs` **(신규)** — 10 테스트.

## 검증

- `dotnet test --filter Category=TokenCost` → **10/10 통과**(substring 매칭, 토큰 클래스별 가격,
  캐시<input, 미지=0, Total 합산, ByModel 정렬).
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 순수 계산, 부작용 없음. 미지 모델 안전 0 처리. |
| 아키텍처 정합성 | Pass | ZeroCommon 순수. 기존 텔레메트리 재사용(중복 수집 없음). |
| 테스트 가능성 | A | 계산 전부 헤드리스. |
| 이식 충실도 | Pass | orca usage 개념 이식. |
| 스코프 규율 | Partial | **커맨드 팔레트(Cmd-J) 미구현** — WPF UI 대공사·저검증이라 follow-up 분리. |

## 다음 단계 제안 (follow-up)

- **커맨드 팔레트**(워크스페이스/에이전트/커맨드 퍼지 검색) — WPF UI. 별도 착수.
- 가격표를 Settings/JSON에서 편집 가능하게(현재 코드 상수).
- Bot/Settings에 비용 위젯(기존 토큰 위젯 옆).
