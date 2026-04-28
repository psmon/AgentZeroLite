---
date: 2026-04-27T15:00:00+09:00
agent: tamer
type: evaluation
mode: log-eval
trigger: "오리진 ReActActor 가드 패턴이 무엇인지 분석해줄래? 장단점 도입 검토"
---

# Origin ReActActor 가드 패턴 정밀 분석 + 스냅샷 갱신

## 실행 요약

사용자는 오리진(AgentWin)의 `ReActActor` 가드 패턴을 분석하고 도입 여부를
판단할 근거를 요청했다. v1.1.3 프로토콜에 따라 먼저 `Docs/agent-origin/`
스냅샷을 확인했으나 **가드 *목록*만 다루고 구현 메커니즘·발동 동작은
표면만**이라 도입 검토에는 깊이가 부족했다. → 스냅샷 갱신 트리거 발동.

수행 단계:
1. 스냅샷의 ReActActor 섹션(`02-...#1.2`) 깊이 부족 확인
2. 두 프로젝트 코드를 병렬 Explore로 정밀 조사:
   - Origin: `ReActActor.cs` 전수 (상수 11개, 카운터 자료구조, 적응형 알고리즘, 메시지 시그니처)
   - Lite: `AgentReactorActor.cs` + `AgentToolLoop.cs` + `ExternalAgentToolLoop.cs` (현재 가드 부재 지점, 통합 후보 위치)
3. 발견 사항을 사용자에게 한국어로 보고 (가드별 가치 매트릭스 + 권장 작업 단위 + 거부 항목)
4. 스냅샷 in-place 갱신:
   - `02-architecture-comparison.md` §1.2 — 가드 11개 표 + 일방향 적응형 도식 + Lite 무방비 지점
   - `03-adoption-recommendations.md` P0-1 — 3종 세트만 발췌 / 거부 항목 / 정량 평가 / Trade-off
   - `README.md` — Snapshot timeline 섹션 + P0 표 행 갱신
5. 본 로그로 평가 기록

## 결과

### 핵심 발견 — 직전 스냅샷이 빠뜨린 디테일

| 발견 | 이전 스냅샷 | 갱신 후 |
|---|---|---|
| 가드 상수 개수 | 7개로 표기 | **11개** (적응형 대기 구성 4개 추가: `MinAiWaitSeconds=5`, `AdaptiveReductionSeconds=3`, `ShellWaitSeconds=3`, `TimeoutGraceSeconds=12`) |
| 적응형 대기 메커니즘 | "비동기 도구 적응형 대기 cap" — 모호 | DONE 도착 시 `-3초`, 최소 5초, **일방향 감소만** (느려지는 터미널 회복 안 됨) |
| 카운터 키 정규화 | 미언급 | `funcName + ":" + argsJson` 그대로 (정규화 없음) — 인자 순서 바꿔서 가드 우회 가능 |
| 카운터 reset 정책 | 미언급 | 세션 시작 1회 reset, **라운드별 reset 없음** — 긴 세션 누적 false positive 위험 |
| 가드 위반 동작 | 단일 패턴으로 표기 | **2단 방어 발견**: `MaxSameCallRepeats` 위반 → LLM 피드백(액터 안 죽음), `MaxConsecutiveBlocks` 위반 → 즉시 종료 |
| transient 분류 | "transient LLM 에러 재시도" | HTTP 502/503/504/timeout/connection reset 문자열 contains, **backoff 없음** (오리진 자체 약점) |
| stage_send 라운드 1회 제한 | 미언급 | 메시지 폭주 방지용 별도 가드 |

### Lite 시간 모델 차이 (이식 거부 사유의 핵심)

| 측면 | Origin | Lite |
|---|---|---|
| 액터 상태 | 5단계 (`Idle/Thinking/Acting/Waiting/Complete` + Error) | 2단계 (`Idle/Running`) — thin |
| 메인 루프 | 액터 상태 머신 | `for (iter = 0; iter < MaxIterations; iter++)` |
| 도구 완료 신호 | 외부 메시지 (`CompletionSignal`/`TerminalDoneSignal`) | `await Task<string>` |
| 비동기 도구 대기 | `Waiting` 상태 + 적응형 timeout + DONE 신호 | LLM이 `wait(seconds=N)` 도구를 자발적으로 호출 |

이 구조 차이 때문에 적응형 대기·5상태 머신·`CompletionSignal`은 직접 이식 불가.

### 도입 권고 — 3종 세트만

| 가드 | 결론 |
|---|---|
| `MaxSameCallRepeats=3` + (func,args) Dict | ✅ HIGH |
| `MaxConsecutiveBlocks=3` | ✅ HIGH (위와 짝) |
| `MaxLlmRetries=1` + transient 분류 + **backoff 추가** | 🟡 ExternalAgentToolLoop만, 오리진보다 개선해서 |
| JSON 인자 정규화 (오리진 약점 보강) | 🟡 가드 도입 시 함께 |
| 적응형 대기 4종 / 액터 5상태 머신 / CompletionSignal / 도구 분류 HashSet / 즉시 재시도 | ❌ 거부 (Lite 시간 모델과 충돌) |

### 정량 평가 (P0-1 갱신 후)

| 항목 | 값 |
|---|---|
| 추가 LOC | ~120 (Options +6, AgentToolLoop +30, ExternalAgentToolLoop +30, helper +20, tests +50) |
| 영향 파일 | 4개 |
| 위험도 | 낮음 — failure 경로만 추가, 백워드 호환 100% |
| 작업 시간 | 2~3일 |

### 갱신된 스냅샷 파일

| 파일 | 갱신 내용 |
|---|---|
| `Docs/agent-origin/README.md` | Snapshot timeline 섹션 추가, P0 표의 ReActActor 행 정정 (3종 세트만, S~M) |
| `Docs/agent-origin/02-architecture-comparison.md` §1.2 | 가드 11개 전수 표 + 적응형 일방향 도식 + 카운터 약점 명시 + Lite 무방비 지점 명시 + 시간 모델 차이 |
| `Docs/agent-origin/03-adoption-recommendations.md` §P0-1 | "이식 대상(3종)" + "거부 항목 표" + 패치 스케치 코드 + JSON 인자 정규화 + 정량 평가 + 본 분석 로그 link |

## 평가 (정원지기 3축)

| 축 | 평가 | 근거 |
|---|---|---|
| **워크플로우 개선도** | **A** | 도입 검토라는 사용자 요구를 *가드별 가치 매트릭스 + 거부 사유*로 분해. 단순 Yes/No가 아니라 **어느 가드를 어떤 비용으로 어떻게 이식하는지**를 명시. v1.1.3 프로토콜의 "스냅샷 갱신 정책"이 즉시 발동되어 미래 세션도 같은 깊이로 시작 가능. |
| **Claude 스킬 활용도** | **5/5** | 병렬 Explore 2명으로 양쪽 코드 동시 정밀 조사 → 메인 컨텍스트 보호. 분석 결과가 사용자 보고와 스냅샷 갱신에 모두 재활용. |
| **하네스 성숙도** | **L4** | v1.1.3 프로토콜의 *첫 실전 테스트* — 스냅샷 미흡 발견 → 직접 조사 → in-place 갱신 → 로그 기록의 4단계가 정확히 작동. 단, 갱신 빈도가 잦으면 README의 timeline 섹션이 길어질 수 있음 → v1.2.0에서 별도 `Docs/agent-origin/CHANGELOG.md` 분리 검토 가치. |

### 잘한 점
- 직전 스냅샷의 약점(가드 4개 누락, 적응형 메커니즘 모호, 시간 모델 차이 미언급)을 정확히 짚어 갱신
- 거부 항목을 *명시적 표*로 만들어 미래 세션이 잘못된 채택을 시도하지 않도록 차단
- 오리진 자체 약점(JSON 정규화 부재, backoff 부재)을 발견하여 *Lite 이식 시 개선*으로 권고
- Snapshot timeline 섹션 도입 → 갱신 이력이 README에서 즉시 보임

### 부족한 점 / 후속 검토
- README timeline은 갱신마다 누적 → 5건 이상 시 별도 파일로 분리해야 가독성 유지
- `MaxSameCallRepeats=3`이 Lite 도구 카탈로그(send_to_terminal/read_terminal/send_key/list_terminals/wait)에도 최적인지는 운영 1~2주 모니터링 필요 — 도입 후 *재평가* 계획을 별도 task로 등록 권장
- Lite의 라운드별 reset 옵션 도입 가치는 미해결 — Origin은 안 하지만, Lite의 긴 세션 시나리오에서는 가치 있을 수 있음

## 다음 단계 제안

### 즉시 실행 가능 (도입 결정 시)
1. `Project/ZeroCommon/Llm/Tools/AgentToolLoopOptions.cs`에 필드 3개 추가
2. `AgentToolLoop.cs` for 루프 라인 113 직후 가드 삽입 (스냅샷 §P0-1 수행 절차 2번)
3. `ExternalAgentToolLoop.cs` 동일 + transient 재시도 + exponential backoff (1s, 3s)
4. `NormalizeArgsJson` 헬퍼 (오리진 약점 보강)
5. `ZeroCommon.Tests/AgentToolLoopGuardTests.cs` 신규 — MockHost로 가드 검증 2개 케이스
6. PR 전 `code-coach` Mode 2 review 통과 확인

### 분기 단위
- 도입 후 1~2주 운영 데이터(`harness/logs/`)로 false positive 빈도 측정 → `MaxSameCallRepeats` 튜닝
- 라운드별 reset 옵션 도입 가치 재평가
- 오리진의 transient 분류를 `HttpRequestException.StatusCode` 기반으로 *개선해서* 이식 — 오리진 자체에도 역방향 제안 가능

### 메타 (하네스 자체)
- v1.2.0에서 `Docs/agent-origin/CHANGELOG.md` 분리 검토 (timeline 가독성)
- 본 케이스를 `harness/knowledge/cases/`에 best practice로 정착 — *"스냅샷 미흡 발견 → 정밀 조사 → in-place 갱신"* 4단계 패턴

---

## 트리거 매칭 로그

| 항목 | 값 |
|---|---|
| 매칭된 트리거 | "오리진 비교해" / "오리진 참고해" (v1.1.3에서 추가됨) — 본 분석 자체가 v1.1.3 프로토콜의 첫 실전 |
| 진입 모드 | 개선부 Mode A: Log & Eval (정밀 분석 + 스냅샷 갱신) |
| 사용한 도구 | Read ×4, Edit ×4, Write ×2, Agent/Explore ×2 (병렬), Bash ×0 |
| 외부 호출 | 없음 (양쪽 모두 로컬 코드) |
| 안전 게이트 | 코드/DB/빌드 영향 0 (문서만), 사용자 승인 후 도입 결정 — 강제 진행 안 함 |

## 관련 산출물

- 갱신된 스냅샷:
  - [Docs/agent-origin/README.md](../../../Docs/agent-origin/README.md)
  - [Docs/agent-origin/02-architecture-comparison.md (#1.2)](../../../Docs/agent-origin/02-architecture-comparison.md)
  - [Docs/agent-origin/03-adoption-recommendations.md (#P0-1)](../../../Docs/agent-origin/03-adoption-recommendations.md)
- 직전 세션 로그:
  - [2026-04-27-14-04-agent-origin-comparison-doc.md](./2026-04-27-14-04-agent-origin-comparison-doc.md) — 스냅샷 초안 작성
  - [2026-04-27-14-15-flow-update-agent-origin-reference.md](./2026-04-27-14-15-flow-update-agent-origin-reference.md) — v1.1.3 프로토콜 등록
- 관련 프로토콜:
  - [harness/knowledge/agent-origin-reference.md](../../knowledge/agent-origin-reference.md)
