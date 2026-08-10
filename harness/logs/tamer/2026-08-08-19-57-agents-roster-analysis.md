---
date: 2026-08-08T19:57:00+09:00
agent: tamer
type: explanation
mode: log-eval
trigger: "여기에 작동중 하네스 에이전트 종류 분석 harness/agents"
---

# harness/agents 작동 에이전트 종류 분석

## 실행 요약

operator 요청으로 `harness/agents/*.md` 8종 전원을 Read 하여 현재 작동 중인
하네스 에이전트의 종류/책임/상호관계를 분석·보고했다. 직전 세션에서 v0.16.0
릴리스를 출하한 직후이며, 그 릴리스의 변경(voice-note 화자분리, agent-band 루프백)
이 어느 큐레이터 관할인지도 연결해 보고했다.

## 결과

8종을 4개 군집으로 분류:

- **메타 (1)**: tamer — 정원 관리 + mission dispatch
- **릴리스 게이트 (3)**: security-guard(필수 게이트) → build-doctor(게이트 통과 전
  거부), code-coach(pre-commit 리뷰). security→build 하드 의존.
- **테스트 레인 (2)**: test-runner(dotnet test 실행 단독 소유, 기본 non-auto)
  vs test-sentinel(구조 감사, 실행 X). 실행/판정 의도적 분할.
- **도메인 큐레이터 (2)**: music-curator(AST AudioSet + WASAPI 루프백),
  voice-curator(Sherpa-ONNX 화자분리). 온디바이스 오디오 최신 성장 축.

주목 설계: (1) Rule 1 단독책임 — 에이전트 간 inline 호출 금지, 멀티-agent
시퀀스는 engine 소유. (2) security→build 방향성 있는 하드 게이트. (3) 테스트
레인 execution/audit 분리. (4) v0.16.0 변경이 voice/music 큐레이터 관할과 직결.

## 평가

정원지기 3축 평가 (읽기 전용 설명 활동):

| 축 | 결과 | 근거 |
|----|------|------|
| 워크플로우 개선도 | B | 신규 개선 없이 현황 매핑만. 로스터 자체는 단독책임/게이트 체인이 정합적으로 유지됨 |
| Claude 스킬 활용도 | 4/5 | agent-zero-build, skill-creator, pencil, harness-view-build 등과 연동 명시. 활발히 참조됨 |
| 하네스 성숙도 | L4 | knowledge/agents/engine 3층 모두 충실(에이전트 8·엔진 6·per-agent knowledge). 자동 트리거·평가 rubric·엔진 조율까지 완비. L5는 자동 피드백 루프 정착 여부 |

## 다음 단계 제안
- agents 8종에 비해 engine이 6종 — 큐레이터(music/voice) 전용 검수 엔진이
  없어 릴리스 시 오디오 회귀가 게이트 밖에 있음. `audio-regression-review`
  엔진 신설을 검토할 만함(단독책임 유지, 엔진이 두 큐레이터를 조율).
- test-runner/test-sentinel 분리는 잘 되어 있으나, 릴리스 파이프라인에 테스트
  단계가 없음(게이트는 security-guard만). 의도된 정책이나 문서화 재확인 권장.
