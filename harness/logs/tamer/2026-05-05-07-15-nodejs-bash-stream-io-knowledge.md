---
date: 2026-05-05T07:15:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "node.js 유틸 만들때 bash 주의법..그리고 file io및 stream io 다루는방법 관련 전문가에 지식추가해죠"
---

# code-coach knowledge 추가 — Node.js wrapper · bash · stream/file IO pitfalls

## 실행 요약

operator 의 M0011 follow-up 성공 보고와 함께 들어온 지식 추가 요청.
"앞으로 이용될 중요 기술임" 이라는 명시 — 이번 한 번의 wrapper 개발에서
배운 것을 향후 모든 wrapper / IPC bridge / process pipe 작업에 자동 적용
될 수 있도록 codify.

operator 의 메타 의도:
1. M0011 종료 (성공 알림)
2. 이번 turn 의 trial-and-error 를 잃지 않게 knowledge 로 보존
3. owner agent (code-coach) 의 review 자동 트리거 대상에 등록

분석 결과 — 이번 wrapper 작업에서 노출된 함정의 갯수가 10건. wpf-xaml-pitfalls.md
와 동일한 "rare-trap catalogue" 형식으로 묶어 codify 가 자연스러운 양 +
일관성.

## 결과

### 산출물

1. **신규 — `harness/knowledge/code-coach/nodejs-bash-and-stream-io-pitfalls.md`**
   (10 pitfalls + paste-into-PR checklist)

   각 pitfall 의 출처는 `_wrapper.log` 의 실제 줄. 추측 0건.

2. **수정 — `harness/agents/code-coach.md`**: owned-conventions 섹션에
   새 knowledge entry 추가. pre-commit review 가 wrapper 코드 diff 시
   자동으로 10-pitfall 체크리스트 walk.

3. **수정 — `harness/missions/M0011-…md`** + 완료 로그: status `partial → done`,
   `## 후속 수정 #1 — claude-hud chain pipe-through 정상화` 섹션 추가.
   M0012 (chain pipe fix) + M0014 (pitfall codify) 두 카드 close.

4. **수정 — `harness/harness.config.json`**: version `1.6.0 → 1.6.1`,
   lastUpdated `2026-05-05`.

5. **신규 — `harness/docs/v1.6.1.md`**: Patch bump rationale + pitfall index
   + compatibility note.

### 변경 통계
- 신규 파일: 3 (knowledge + version doc + 이 로그)
- 수정 파일: 3 (config + agent + mission record)
- 삽입 라인: ~600

## 평가

조련사 평가축 (3축):

| 축 | 평가 | 비고 |
|---|---|---|
| 워크플로우 개선도 | **A** | 한 번의 미션에서 배운 함정을 자동 review 트리거에 묶음. 같은 함정이 향후 wrapper 코드에서 재현되면 code-coach 가 PR 단계에서 잡음. 운영 부하 0. |
| Claude 스킬 활용도 | 4/5 | TaskCreate/Update 로 단계별 추적, Read/Write/Edit 만으로 처리. tooling 추가 없음. |
| 하네스 성숙도 | **L5** | knowledge / agent / mission record / version doc 4개 레이어 동기 갱신. wpf-xaml-pitfalls 와 같은 격의 두 번째 catalogue 가 생기면서 "도메인별 pitfall 수집소" 패턴이 굳어짐. |

## 다음 단계 제안

- (즉시) 단일 commit 으로 묶기 — `docs(harness)` 또는 `feat(harness)` prefix.
  M0011 의 follow-up + knowledge + 버전 bump 가 한 묶음의 work product.
  operator 의 commit 승인 후 push.
- (다음 turn) M0013 (rate_limits 가 모델별 quota 인지 계정 단위인지 empirical
  검증) 진행. claude-hud 가 정상 동작하는 지금이 비교 가능한 상태.
- (잠재) 같은 "rare-trap catalogue" 형식으로 다른 agent 들도 자기 도메인의
  함정을 수집하도록 권장. security-guard 의 `prompt-injection-pitfalls.md`,
  build-doctor 의 `csproj-and-publish-pitfalls.md` 등.
