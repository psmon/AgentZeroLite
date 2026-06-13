---
date: 2026-06-13T22:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "월드컵 배경일때 캐릭터 위로 50px / 배경에 따른 위치 조정기능 최초 도입 / 설정화 적용, 나머지 유지"
---

# Agent Band — 배경별 위치 조정 기능 최초 도입 (v0.12.2)

## 실행 요약
배경마다 무대 "바닥선" 높이가 달라 캐릭터가 어색하게 놓이는 문제를, **배경→오프셋 설정 맵**으로 해결.
- 신규 설정: `STAGE_Y_OFFSET = { 'fifa26': -50 }` (px, 음수=위로). 미등록 배경은 0(그대로).
- `pickStage(name)`에서 `currentStage` 추적, `computeLayout`의 baseY에 `+ (STAGE_Y_OFFSET[currentStage]||0)` 적용.
- 결과: 월드컵(Stadium/fifa26) 배경에선 캐릭터가 **50px 위로** 올라가 피치 무대에 맞고, 나머지 배경은 무변경.
- "설정화": 향후 배경은 맵에 한 줄만 추가하면 됨(확장 가능).

## 결과 (변경 파일)
- `agent-band.js` — `STAGE_Y_OFFSET` 맵, `currentStage` 추적(pickStage), `computeLayout` baseY 보정, v0.12.2 changelog
- `manifest.json` — 0.12.1 → 0.12.2
- `node --check` 통과

## 평가 (3축)
| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | A | 변경 3곳, 기본값 0으로 미등록 배경 무영향. baseY 보정만이라 부작용 없음. syntax OK. |
| 아키텍처 정합성 | A | 하드코딩 대신 배경→오프셋 설정 맵으로 일반화(확장성). 단일 적용점(computeLayout). 노트/스펙트럼 등 "나머지"는 그대로. |
| 테스트 가능성 | A− | 오프셋 적용은 순수 산술이라 검증 단순. 실제 무대 정렬은 배경별 육안 확인 필요. |

## 다음 단계 제안
- 수동 검증: Stadium 선택 시 캐릭터가 피치 위에 자연스럽게 서는지 확인(50px가 맞는지 미세조정 여지).
- (선택) 향후 배경 추가 시 STAGE_Y_OFFSET에 등록. 필요하면 수평/스케일 보정 축도 같은 맵에 확장 가능.
