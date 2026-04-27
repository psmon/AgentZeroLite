---
date: 2026-04-27T14:15:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "AgentZero 오리진을 참고할 것이라는 지침 시 Docs/agent-origin 하위 문서를 참고하는 것으로 플로우 업데이트"
---

# Agent Origin 참고 프로토콜 — 하네스 v1.1.3 업데이트

## 실행 요약

직전 세션에서 산출한 `Docs/agent-origin/` 비교 문서 4개가 존재하지만,
*"오리진을 참고하라"* 같은 지침이 들어왔을 때 그 문서를 자동으로 사용하라는
구속력 있는 프로토콜이 없었다. 사용자 요청은 이 프로토콜을 하네스 워크플로우와
프로젝트 안내(CLAUDE.md)에 등록하는 것.

수행 단계:
1. 현재 `CLAUDE.md`, `harness/knowledge/`, `harness/agents/` 인벤토리 파악
2. 6곳에 변경을 분산 배치 (Single Source of Truth + 호출 지점 분산)
3. 메모리 시스템에도 동일 프로토콜 등록 (세션 간 지속)
4. 하네스 patch 버전 bump + 버전 히스토리 작성
5. 본 로그로 평가 기록

## 결과

### 변경된 파일 (7개 + 1개 메모리)

| 파일 | 변경 종류 | 핵심 |
|---|---|---|
| `CLAUDE.md` | 신규 섹션 추가 | "Ancestor reference — AgentWin (Origin)" — 모든 Claude Code 세션이 자동 로드 |
| `harness/knowledge/agent-origin-reference.md` | **신규** | 정식 프로토콜 정의 (lookup 순서, 6개월 freshness 정책, 안티패턴) |
| `harness/agents/tamer.md` | 트리거 6개 + 절차 1개 추가 | "오리진 비교해", "AgentWin 비교", "오리진 스냅샷 갱신" 등 |
| `harness/agents/code-coach.md` | "Owned convention sets" 항목 추가 | Mode 3 Research consult에서 오리진 솔루션 인용 |
| `harness/harness.config.json` | version 1.1.2 → 1.1.3, lastUpdated 2026-04-27 | patch bump |
| `harness/docs/v1.1.3.md` | **신규** 버전 히스토리 | 변경 요약 + 검증 체크리스트 + 롤백 절차 |
| `memory/reference_agent_origin_docs.md` | **신규** | 세션 간 지속 메모리 (사용자 메모리 시스템) |
| `memory/MEMORY.md` | 인덱스 1줄 추가 | 위 메모리 파일 등록 |

### 프로토콜 핵심 (한 문장)

> "오리진"/"AgentWin"/"조상"이 언급되면 **`Docs/agent-origin/` 4개 문서를
> 먼저 읽고**, 토픽이 커버되지 않거나 6개월 이상 경과한 경우에만
> `D:\Code\AI\AgentWin`을 직접 조사한 뒤 해당 스냅샷 파일을 in-place 갱신한다.

### Lookup 순서 (knowledge에 정식 정의)

```
1. Docs/agent-origin/README.md                        ← Executive summary + P0~P3 표
2. Docs/agent-origin/01-stack-comparison.md           ← NuGet/csproj/DB/CLI/IPC/build 1:1 표
3. Docs/agent-origin/02-architecture-comparison.md    ← 액터/LLM/터미널/하네스 다이어그램
4. Docs/agent-origin/03-adoption-recommendations.md   ← P0~P3 + REJECT 채택 로드맵
5. (위 1~4가 부족한 경우만) D:\Code\AI\AgentWin 직접 조사 + 스냅샷 갱신
```

### 등록 위치 분산 사유

| 위치 | 역할 |
|---|---|
| `CLAUDE.md` | Claude Code 세션 자동 컨텍스트 — 가장 빠른 진입점 |
| `harness/knowledge/agent-origin-reference.md` | 카카시 하네스의 정식 프로토콜 정의 (single source of truth) |
| `harness/agents/tamer.md` | 정원지기가 "오리진 비교" 트리거를 받았을 때 절차로 사용 |
| `harness/agents/code-coach.md` | 코드 리뷰 Mode 3에서 오리진 솔루션을 자동 인용 |
| `memory/reference_agent_origin_docs.md` | 세션 간 안전망 (사용자 메모리 시스템) |
| `harness/docs/v1.1.3.md` | 변경 추적 + 롤백 절차 |

→ Single source of truth는 `harness/knowledge/agent-origin-reference.md`,
나머지 5곳은 진입점/위임자/안전망. 모든 곳에서 정식 문서로 link back.

## 평가 (정원지기 3축)

| 축 | 평가 | 근거 |
|---|---|---|
| **워크플로우 개선도** | **A** | 직전 세션이 만든 자산(`Docs/agent-origin/`)이 즉시 휘발될 위험을 제거. 6개월 freshness 정책으로 자동 노후화 방지. 안티패턴 명시로 잘못된 사용도 차단. |
| **Claude 스킬 활용도** | **5/5** | CLAUDE.md(자동 컨텍스트) + harness knowledge(정식 정의) + tamer/code-coach(트리거) + memory(세션 간 지속) — 4개 채널 동시 등록으로 어떤 진입 경로에서도 발견 가능. |
| **하네스 성숙도** | **L4 → L4+** | knowledge 6번째 문서, agents 2명에 신규 트리거/절차, version 히스토리/롤백 절차까지 갖춘 완결된 patch. 단, 신규 엔진(`origin-snapshot-refresh`) 도입은 보류 — 6개월 후 갱신 시점에 검토. |

### 잘한 점
- 정보가 한 곳에만 있지 않고 사용 진입 경로마다 분산 배치 — 빠뜨리기 어려움
- 6개월 freshness 정책으로 스냅샷이 영원히 유효한 척 하지 않음
- 롤백 절차가 v1.1.3 doc에 명시 — 이 프로토콜이 해로운 것으로 판명되면 안전하게 되돌릴 수 있음
- Memory와 harness knowledge가 서로 cross-reference (양쪽 모두 다른 쪽을 가리킴)

### 부족한 점 / 후속 검토
- 오리진 스냅샷 갱신을 자동화할 엔진(`origin-snapshot-refresh`)은 v1.2.0에서 도입 검토 — 지금은 수동 트리거에만 의존
- `Docs/agent-origin/README.md`에 *"snapshot date: 2026-04-27"* 마커가 있긴 하지만, 갱신 시 실수로 누락될 위험 → CI 체크 또는 build-doctor가 검증하는 옵션 검토
- code-coach Mode 3가 오리진을 인용하는 패턴이 실제로 작동하는지 검증 테스트 필요 (다음 research consult 발생 시 확인)

## 다음 단계 제안

### 즉시 실행
- 본 변경을 commit (사용자 지시 시) — 모두 문서 변경이라 code-coach 적용 대상 아님
- 향후 *"오리진"* 언급 시 본 프로토콜이 실제로 작동하는지 dry-run 검증

### 분기 단위
- v1.2.0에서 `origin-snapshot-refresh` 엔진 도입 검토 — `tamer` + 6개월 cron 트리거
- `Docs/agent-origin/03-adoption-recommendations.md`의 P0 항목(ReActActor 가드 이식, AppLogger IDE 분기) 실제 작업 단위로 분해 → 별도 Mode A 호출

### 메타 (하네스 자체)
- 본 케이스를 `harness/knowledge/cases/`에 best practice로 정착할 가치 있음 — *"외부 산출물(`Docs/...`)을 하네스 워크플로우에 binding하는 패턴"*

---

## 트리거 매칭 로그

| 항목 | 값 |
|---|---|
| 매칭된 트리거 | "하네스를 업데이트해" (개선부 Mode A — 정원지기 지침 갱신) |
| 진입 모드 | 개선부 Mode A: Log & Eval |
| 사용한 도구 | Read ×3, Edit ×4, Write ×4, Bash ×1 |
| 외부 호출 | 없음 |
| 안전 게이트 | secret/ 미접근, 코드 변경 없음(문서만), DB/빌드 영향 없음 |

## 관련 산출물

- [CLAUDE.md (Ancestor reference 섹션)](../../../CLAUDE.md)
- [harness/knowledge/agent-origin-reference.md](../../knowledge/agent-origin-reference.md)
- [harness/agents/tamer.md](../../agents/tamer.md)
- [harness/agents/code-coach.md](../../agents/code-coach.md)
- [harness/docs/v1.1.3.md](../../docs/v1.1.3.md)
- 직전 세션 로그: [2026-04-27-14-04-agent-origin-comparison-doc.md](./2026-04-27-14-04-agent-origin-comparison-doc.md)
