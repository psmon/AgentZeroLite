---
date: 2026-08-08T20:10:00+09:00
agent: tamer
type: creation
mode: suggestion-tip
trigger: "1번 항목만 새 엔진 추가해"
---

# 새 엔진 추가 — audio-regression-review

## 실행 요약

직전 로스터 분석(`2026-08-08-19-57-agents-roster-analysis.md`)의 개선 제안 1번
("오디오 회귀가 릴리스 게이트 밖에 있음")을 operator가 승인 → `creator-rule.md`
Rule 1/2/3/6 내재화 후 `audio-regression-review` 엔진을 신설했다.

두 오디오 도메인 큐레이터(music-curator + voice-curator)를 릴리스 시점에
조율하는 자문(advisory) 리뷰 레인. security→build 하드 게이트와 달리
recommend-block까지만 하고 릴리스 결정은 operator가 소유.

## 결과

- 신규: `harness/engine/audio-regression-review.md`
  - agents: [music-curator, voice-curator], 독립 실행 후 엔진이 aggregate
  - 변경 감지 라우팅: diff가 각 큐레이터 watched path를 건드릴 때만 해당
    큐레이터 발동, 아니면 no-op(값싼 종료)
  - 판정: Pass/Fail축 Fail → recommend-block, A/B/C/D 약점 → advisory
- config: version 1.9.0 → 1.10.0, engine 배열에 등록, lastUpdated 2026-08-08
- docs: `harness/docs/v1.10.0.md`
- 두 큐레이터 파일·기존 엔진 파일 무수정 (순수 추가)

## 평가

위반 자체 검사 (Mode B 5문항):

- [x] knowledge 파일에 트리거 없음 (엔진 frontmatter에만) — Rule 3 OK
- [x] agent 파일이 다른 agent inline 호출 안 함 (큐레이터 무수정) — Rule 1 OK
- [x] 멀티-agent 시퀀스를 engine으로 표현 — Rule 2 OK
- [x] 새 knowledge 추가 없음 (엔진만) — Rule 4 무관
- [x] backlink: engine → 두 agent "Files this agent watches", release-build-pipeline,
      creator-rule Rule 1/2/6 — Rule 5 OK

정원지기 3축:

| 축 | 결과 | 근거 |
|----|------|------|
| 워크플로우 개선도 | A | 릴리스 파이프라인의 오디오 사각지대를 값싼 no-op 라우팅으로 메움. v0.16.0이 실제 미검수 통과한 change class를 정확히 커버 |
| Claude 스킬 활용도 | 4/5 | release-build-pipeline / agent-zero-build와 연동 경로 명시. 자동 발동 wiring은 후속 |
| 하네스 성숙도 | L4 | 엔진 7종으로 확장, 도메인 큐레이터가 워크플로우에 편입. 큐레이터 전용 검수 레인 확보 |

## 다음 단계 제안
- `release-build-pipeline.md`에 auto-invoke 실제 wiring (현재는 선언만). 보안
  게이트 파일 무손상을 위해 이번 사이클에선 분리 — operator 별도 승인 필요.
- 첫 실전 실행 시 `harness/logs/audio-regression-review/` 첫 로그 생성 →
  라우팅 정확도/no-op 규율 rubric 실측.
