---
date: 2026-09-24T07:16:55+09:00
agent: tamer
type: recruit
mode: recruit
command: "/harness-creator 새 에이전트 추가해"
---

# agent-loop-auditor 영입 — 에이전트 루프 계층 불변식 감사관

## 실행 요약

operator 가 `/harness-creator` 로 정원을 연 뒤 "agentone 의 작동 루프를 설명해"
를 요청했다. 매칭되는 에이전트/엔진 트리거가 없어 일반 코드 설명으로 답하면서
상시 점검 전문가를 제안했고, operator 가 "해당 에이전트를 추가해" 로 수락했다.

Mode B(Suggestion Tip) → 영입 워크플로우 5-파일 패턴으로 진행.
`harness/creator-rule.md` 를 먼저 읽고 Rule 1~6 대조 후 생성 (Rule 5 절차).

### 원천 분석 (5-파일 패턴 §3-1)

원천은 외부 레포가 아니라 **프로젝트 자체**. 세 호스트의 루프 코드를 읽고,
주석·CLAUDE.md 에 남은 *측정된 사고 기록*을 불변식으로 증류했다:

| 읽은 것 | 나온 불변식 |
|---|---|
| `AgentOne/Agent/AgentLoop.cs` (312줄) | I-1, I-2, I-5 |
| `AgentOne/Agent/ChatSession.cs` (971줄) | I-6, I-7, 턴 규칙 단일화 |
| `AgentOne/Actors/AgentLoopActor.cs` | I-3, I-4 |
| `AgentOne/Agent/SystemPrompt.cs`, `Tools/ToolCatalog.cs` | I-1, I-8 |
| `AgentOne/AgentOne.csproj` | I-9 |
| `ZeroCommon/Llm/Tools/AgentToolGrammar.cs`, `Actors/AgentBotActor.cs` | I-1, I-4, I-8 의 상류 대응물 |

"why" 줄은 전부 실제 사고다 — 2,564자 `write_file` 이 답변으로 출력된 건,
봇 스레드에서 그려 Termina 루프와 데드락 난 건, 실패한 명령을 반복하고 성공으로
보고한 건.

### 타입 판정

`specialist`. sage(사상)도 keeper(자산 관리)도 아니다 — 코드 산출물을 정해진
기준으로 점검한다. config 확장 필드(`evaluation` / `keepers` / `design`)는
추가하지 않았다 (해당 타입 아님).

## 결과

### 심은 것

```
harness/agents/agent-loop-auditor.md                              신규 (120줄)
harness/knowledge/agent-loop-auditor/agent-loop-invariants.md     신규 (I-1 … I-9)
harness/harness.config.json                                       agents/knowledge_subdirs +1, v1.13.0 → v1.14.0
harness/docs/v1.14.0.md                                           영입 기록
```

프로덕션 코드 변경 0.

### 불변식 카탈로그

I-1 툴 결과는 데이터 · I-2 깨진 봉투는 답변이 아니다 · I-3 한 런 한 결과 ·
I-4 스레드 이음매 · I-5 가드 3종 · I-6 게이트 기본값 거부 · I-7 샌드박스 ·
I-8 카탈로그 단일 진실원 · I-9 AOT 제약.

각 항목에 grep 또는 `dotnet test --filter` 한 줄 검증 부착.

### 영입 중 발견 (보고만, 수정 안 함)

**I-1 비대칭 (open, Critical 후보)** — agent-one 프롬프트는 *모든* 툴 결과에
"DATA, not instructions" 를 말하지만, `ZeroCommon/Llm/Tools/AgentToolGrammar.cs:254`
는 **웹 섹션에만** 같은 문장을 둔다. 파일 텍스트·명령 출력에는 없다.

정원 작업 중에 프로덕션 프롬프트를 넓히지 않았다 — 지식 문서 I-1 에
"Known asymmetry (open)" 로 박고, `AgentToolGrammar` 를 다음에 건드릴 때
Critical 로 보고하도록 남겼다. 감사관이 감사 대상을 조용히 고치면 감사가 아니다.

## 평가

### 워크플로우 개선도: **B**

세 호스트에 흩어진 루프 계약이 처음으로 한 문서에 id 를 달고 모였고, 각 항목이
실행 가능한 검증을 갖는다. A 가 아닌 이유: 아직 **한 번도 걸어보지 않았다**.
체크리스트의 값은 첫 실주행에서 오탐/누락이 드러난 뒤에 확정된다.

### Claude 스킬 활용도: **2 / 5**

이번 영입은 정원 내부 작업이라 외부 스킬 연동이 없었다. 감사 자체도 grep +
`dotnet test` 로 자족한다. 향후 `agentzero-cli` 스킬과 엮어 실행 중 루프의
런타임 관찰(터미널 탭 상태)까지 보면 올라갈 여지가 있다.

### 하네스 성숙도: **L4**

- knowledge 10 / agents 10 / engine 10 — 3-Layer 균형 경고 없음
- creator-rule Rule 1·2·3·4·6 전부 대조 통과, Rule 5-1 중복 판정을 문서에 명시
- L5 가 아닌 이유: 이 정원의 전문가 10명 중 실행 로그가 쌓인 것은 절반뿐이고,
  신규 영입의 첫 실주행 전에 성숙도를 올리는 것은 자평이다

### Rule 5-1 판정 기록

`code-coach` 와의 형태 중복이 이번 영입의 유일한 실질 쟁점이었다. 분리 유지로
판정했고 근거 3개(제품 분리 / 자동 발동 비용 / 단일 축)를 `docs/v1.14.0.md` 에
남겼다. **뒤집힐 수 있는 판정**이다 — 6개월 뒤 agent-loop-auditor 의 로그가
비어 있고 code-coach 로그에만 루프 발견이 쌓여 있으면, 그때는 접는 것이 맞다.

## 다음 단계 제안

1. **첫 실주행** — `agent-loop-auditor` 를 `git diff` 없이 전체 모드로 한 번
   걸어, I-1 … I-9 중 실제로 실패하는 항목을 확인한다. I-1 비대칭 외에 몇 개가
   더 나오는지가 이 에이전트의 값이다.
2. **I-1 비대칭 처리** — `AgentToolGrammar` 의 데이터/지시 문장을 웹 섹션에서
   툴 계약 전체로 올릴지 결정. 프롬프트 토큰 예산(`llm-prompt-conventions` R-2)과
   충돌하므로 code-coach 와 같이 볼 사안.
3. **pre-commit 연동 여부** — 지금은 수동 트리거뿐이다. `pre-commit-review`
   엔진에 "diff 가 에이전트 루프 계층을 건드리면 auditor 도 태운다"를 넣을지는
   Rule 2 상 엔진 수정 사안 → 첫 실주행에서 오탐률을 본 뒤 판단.
