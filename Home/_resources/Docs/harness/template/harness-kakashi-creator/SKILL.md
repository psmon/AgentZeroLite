---
name: harness-kakashi-creator
description: |
  하네스(Harness) 통합 관리 스킬 - 에이전트/지식/엔진 워크플로우의 생성, 사용, 평가를 하나로 통합.
  이 스킬은 자동 트리거하지 않는다. 반드시 /harness-kakashi-creator 명령으로만 활성화할 것.
  다음 상황에서 사용:
  [개선부] 하네스 자체를 만들고 고치는 작업:
  - "하네스를 업데이트해", "하네스를 개선해", "하네스를 설명해", "평가로그를 점검해"
  - "하네스에 새 에이전트 추가해", "워크플로우 만들어줘", "엔진 정의해줘"
  - "이 스킬을 하네스에 복사해", "스킬 복사해줘", "스킬 복제해줘"
  - "하네스 초기화", "harness init", "/harness-kakashi-creator init"
  [수행부] 하네스에 정의된 에이전트/엔진을 실행하는 작업:
  - "하네스 수행해", "전체 점검해", "변경 점검해"
  호출 규칙: "카카시 하네스"라고 부르는 것 = /harness-kakashi-creator 를 실행하는 것.
  스킬이 이미 소환된 뒤의 인자에는 "카카시"를 붙일 필요 없다.
argument-hint: "[명령] [요구사항]"
---

# Harness Creator Kakashi

카카시(복사 닌자)처럼 유용한 스킬을 복사하고, 하네스 에이전트/지식/엔진을 생성/사용/평가하는 통합 스킬.

> "카카시 하네스"라고 부르면 된다. 그것이 전부다.
> **자동 트리거 금지** — `/harness-kakashi-creator` 명령으로만 활성화한다.

---

## 하네스 존재 확인 & 진입 분기 (최우선)

활성화 시 가장 먼저 `harness/harness.config.json`을 Read로 읽는다.
그 다음 `$ARGUMENTS`가 비어 있는지 확인하여 진입 경로를 결정한다.

```
/harness-kakashi-creator [$ARGUMENTS]
        │
        ├─ $ARGUMENTS가 비어 있음 → 튜토리얼 모드 (아래 섹션)
        │
        └─ $ARGUMENTS가 있음 → 모드 판별 (개선부/수행부)
            │
            ├─ harness.config.json 없음 → init 안내만 출력
            └─ harness.config.json 있음 → 정상 모드 판별
```

### harness.config.json이 없고, $ARGUMENTS가 있는 경우

- 다른 모드로 진행하지 않는다
- 사용자에게 다음과 같이 안내한다:

```
하네스가 아직 초기화되지 않았습니다.
먼저 초기화를 진행해 주세요:

  /harness-kakashi-creator init

이 명령은 harness/ 디렉토리와 기본 구조(조련사 에이전트, config 등)를 생성합니다.
```

---

## 튜토리얼 모드 ($ARGUMENTS가 비어 있을 때)

`/harness-kakashi-creator` 만 입력하면 이 모드가 활성화된다.
harness.config.json 존재 여부에 따라 안내 내용이 달라진다.

### Case 1: 하네스가 없는 경우 (harness.config.json 없음)

다음 온보딩 안내를 출력한다:

```
"너의 이름은." — 이름을 부르는 순간, 정원의 문이 열린다.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

아직 정원이 없습니다.
먼저 씨앗을 심어야 합니다.

  /harness-kakashi-creator init

이 명령 하나로 정원이 만들어집니다:

  harness/
  ├── harness.config.json   ← 정원의 이름표
  ├── agents/tamer.md       ← 정원지기 카카시 (기본 내장)
  ├── knowledge/            ← 햇빛 — 도메인 지식
  ├── engine/               ← 물길 — 워크플로우
  ├── docs/                 ← 정원 일지
  └── logs/                 ← 활동 기록

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

하네스(harness)란?
  말을 제어하기 위한 마구(馬具)에서 유래한 용어.
  소프트웨어에서는 검증을 실행하기 위한 틀(framework)을 의미한다.

카카시 하네스란?
  나루토의 카카시 선생처럼 — 직접 싸우지 않고,
  전문가 에이전트를 적재적소에 배치하는 오케스트레이터.

정원이란?
  하네스는 정원이고, 에이전트는 그 안에 피는 꽃이다.
  정원에 꽃을 심고 가꾸는 것은 당신의 역할이다.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/harness-kakashi-creator init 으로 정원을 열어보세요.
```

### Case 2: 하네스가 있는 경우 (harness.config.json 존재)

harness.config.json에서 name, version, agents, engine 정보를 읽는다.
**agents 배열이 `["tamer"]`만 포함**하면 → **온보딩 모드** 진입.
agents가 2개 이상이면 → **일반 안내 모드** 진입.

#### Case 2-A: 온보딩 모드 (tamer만 있는 정원)

정원지기 카카시의 페르소나로 현재 상태를 안내하고, 초기 전문가 투입을 제안한다.

```
"너의 이름은." — 카카시 하네스.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
정원이 열렸습니다 — {name} (v{version})
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  "{description}"

정원지기 카카시가 문 앞에 서 있습니다.
지금 이 정원에는 정원지기 혼자뿐입니다.
꽃(전문가 에이전트)이 아직 한 송이도 피지 않았습니다.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
정원의 구조 — 세 겹의 토양
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  knowledge/ (햇빛)  — 도메인 지식. 무엇이 올바른지 판단하는 기준.
  agents/   (영양분) — 전문가 에이전트. 실제 검수를 수행하는 주체.
  engine/   (물길)  — 워크플로우. 검수가 흐르는 순서와 범위.

  햇빛 없이는 방향을 잃고,
  영양분 없이는 꽃이 피지 않으며,
  물길 없이는 꽃이 말라간다.

현재 상태:
  햇빛 (knowledge/) : {knowledge 파일 수}개
  영양분 (agents/)  : tamer (정원지기) — 혼자
  물길 (engine/)    : {engine 파일 수}개

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
첫 번째 꽃을 심어볼까요?
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

정원의 이름과 설명을 보고, 어울리는 전문가를 제안합니다:

  (여기서 harness.config.json의 name과 description을 분석하여
   프로젝트 성격에 맞는 에이전트 2~3명을 구체적으로 제안한다)

  예시:
  - "{name}"이 웹 프로젝트라면 → security-guard, performance-scout
  - 데이터 파이프라인이라면 → test-sentinel, build-doctor
  - 디자인 시스템이라면 → code-modernizer, test-sentinel

제안을 수락하시면 에이전트를 심어드립니다.
직접 지정하셔도 됩니다:

  /harness-kakashi-creator 새 에이전트 추가해

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
사용법 요약
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

"카카시 하네스"라고 부르면 /harness-kakashi-creator 가 소환됩니다.

  꽃을 심다 (개선부):
    /harness-kakashi-creator 하네스를 설명해
    /harness-kakashi-creator 하네스를 개선해
    /harness-kakashi-creator 새 에이전트 추가해

  꽃을 피우다 (수행부):
    /harness-kakashi-creator 전체 점검해
    /harness-kakashi-creator 변경 점검해

카카시의 숨겨진 능력 — 사륜안(写輪眼):
  /harness-kakashi-creator 스킬 복사해
  기존 스킬을 보기만 해도 복제할 수 있다.

  "/harness-kakashi-creator"를 부르는 것 자체가 카카시를 소환하는 것.
  그 뒤에는 간결하게 말하면 된다.

Tip: 카카시 하네스를 한 번 부른 뒤에는 /harness-kakashi-creator 없이
     "전체 점검해", "새 에이전트 추가해" 처럼 바로 말해도 됩니다.
```

**온보딩 동작 절차:**
1. harness.config.json의 `name`과 `description`을 읽는다
2. 위 템플릿으로 현재 상태를 출력한다
3. name/description을 분석하여 프로젝트 성격에 맞는 초기 에이전트 2~3명을 **구체적으로** 제안한다
4. **베스트 케이스 사례 인용** — [references/onboarding-best-case.md](references/onboarding-best-case.md)에서 실제 사용 사례를 요약하여 보여준다:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
실제 사례: "피라미드를 만들었을 뿐인데"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

어떤 사용자가 "hello world 피라미드 만들어줘"라고 했습니다.

  Step 1  "피라미드 만들어줘"  → 173줄 코드 + 빌드 + 실행
  Step 2  "전체평가 해줘"      → 5명 전문가 동시 리뷰
  Step 3  코칭 결과:
          · "메서드를 분리하세요" (단일 책임 원칙)
          · "테스트를 추가하세요" (테스트 가능한 설계)
          · "보안은 안전합니다"  (공격 표면 분석)
  Step 4  "보안 문서 만들어줘" → OWASP 공식 리포트

코드를 만들어달라고 했을 뿐인데,
시니어 개발자 5명에게 코드 리뷰를 받은 셈입니다.

전문가를 심으면 당신의 정원에서도 같은 일이 일어납니다.
```

5. 사용자가 수락하면 Mode B(Suggestion Tip) 절차로 에이전트를 생성한다
6. 사용자가 거부하거나 나중에 하겠다고 하면 안내만 출력하고 끝낸다

> 온보딩 모드도 안내 모드이므로 로그를 기록하지 않는다.
> 단, 사용자가 제안을 수락하여 에이전트 생성이 진행되면 그때부터 로그를 기록한다.

상세 사례: [references/onboarding-best-case.md](references/onboarding-best-case.md)

#### Case 2-B: 일반 안내 모드 (에이전트가 2명 이상)

```
카카시 하네스 — {name} (v{version})

  "{description}"
  정원에 {agents 수}명의 전문가가 일하고 있습니다.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

꽃을 심다 (개선부):

  /harness-kakashi-creator 하네스를 설명해     ← 정원 상태 보고
  /harness-kakashi-creator 하네스를 개선해     ← 평가 후 개선안
  /harness-kakashi-creator 하네스를 업데이트해 ← 구조/내용 갱신
  /harness-kakashi-creator 평가로그를 점검해   ← 로그 분석
  /harness-kakashi-creator 새 에이전트 추가해  ← 새 꽃 심기
  /harness-kakashi-creator 스킬 복사해         ← 사륜안 발동

현재 에이전트: {agents 목록}
현재 엔진: {engine 목록 또는 "없음"}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

꽃을 피우다 (수행부):

  /harness-kakashi-creator 전체 점검해   ← 전체 리뷰
  /harness-kakashi-creator 변경 점검해   ← 변경사항만 리뷰
  /harness-kakashi-creator 하네스 수행해 ← 전체 점검과 동일

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Tip: 카카시 하네스를 한 번 부른 뒤에는 /harness-kakashi-creator 없이
     "전체 점검해", "하네스를 설명해" 처럼 바로 말해도 됩니다.

원하는 명령을 입력해 주세요.
```

> **중요**: 튜토리얼/온보딩 모드는 안내만 출력하고 끝난다. 로그를 기록하지 않는다.

---

## 모드 판별

$ARGUMENTS를 분석하여 **개선부** 또는 **수행부**를 먼저 판별한 뒤, 세부 모드를 결정한다.

```
/harness-kakashi-creator <요청>
        │
        ├─ 개선부 (하네스 자체를 만들고 고치는 작업)
        │   ├── Mode A: Log & Eval — 조련사(tamer)가 하네스를 설명/개선/평가
        │   ├── Mode B: Suggestion Tip — 새 에이전트/워크플로우/지식 제안
        │   ├── Mode C: Kakashi Copy — 스킬 복사 (skill-creator 위임)
        │   └── Mode D: Initialize — 하네스 초기화
        │
        └─ 수행부 (하네스에 정의된 에이전트/엔진을 실행하는 작업)
            ├── "하네스 수행해" — 전체 점검 (full-review engine)
            ├── "전체 점검해" — full-review engine
            ├── "변경 점검해" — targeted-review engine
            └── 개별 에이전트 트리거 — 해당 에이전트 단독 실행
```

---

### 개선부

#### Mode A: Log & Eval (로그 & 평가)

**조건**: 조련사(tamer) 트리거와 매칭되는 요청

**동작**:
1. 매칭된 에이전트의 역할 정의(`harness/agents/{name}.md`)를 읽는다
2. 매칭된 워크플로우가 있으면 `harness/engine/{name}.md`를 읽는다
3. 에이전트 역할에 따라 작업을 수행한다
4. **[필수] 로그 기록** — `harness/logs/{agent-name}/{yyyy-MM-dd-HH-mm-title}.md`
5. **[필수] 평가 실행** — 해당 에이전트의 평가축에 따라 결과 평가

> **CRITICAL**: 4~5단계는 생략 불가. 작업 완료 후 사용자에게 결과를 보고하기 **직전에** 반드시 로그 파일을 생성하고 평가를 수행할 것. 로그 없는 Mode A 실행은 불완전한 실행이다.

#### Mode B: Suggestion Tip (제안 모드)

**조건**: 매칭되는 에이전트/워크플로우가 없음

**동작**:
1. 사용자 요청을 분석하여 필요한 역할/워크플로우를 식별
2. 구조화된 제안 생성:
   - 제안 에이전트명, 역할 설명
   - 트리거 문구
   - 배치할 하네스 레이어
   - **트리거 등록 위치** — 표준 스킬 SKILL.md인지, 프로젝트 전용 스킬인지, 아니면 `harness/` 내부 frontmatter 자동 발견에 맡길지 판정
   - 예상 효과
3. **사용자 확인 후에만** 생성 진행
4. 승인 시 해당 레이어에 파일 생성 + 로그 기록

> **표준 스킬 오염 금지**: 특정 MCP 서버 의존, 프로젝트 고유 문구, 또는 프로젝트 파일 구조 전제가 있는 트리거는 절대 표준 `harness-kakashi-creator` SKILL.md에 추가하지 않는다. 판정 기준과 배치 규칙: [references/skill-separation.md](references/skill-separation.md)

#### Mode C: Kakashi Copy (카카시 복사)

**조건**: "스킬 복사", "카카시 복사", "스킬 복제" 등의 요청

**동작**:
1. 복사 대상 식별 (경로, 설명, 또는 기존 스킬 참조)
2. `/skill-creator`에 위임하여 스킬 생성/복사
3. 복사 후 `harness/knowledge/`에 참조 문서 등록
4. 로그 기록: `harness/logs/kakashi-copy/`

> 스킬 생성/복사는 직접 하지 않는다 — 반드시 `/skill-creator`를 호출하여 위임한다.

#### Mode D: Initialize (초기화)

**조건**: `harness/` 디렉토리가 없거나 "하네스 초기화" 요청

**동작**: 아래 "하네스 초기화" 섹션 참조

---

### 수행부

수행부는 하네스에 정의된 에이전트와 엔진 워크플로우를 **실제 코드/프로젝트에 대해 실행**하는 작업이다.
개선부가 하네스 자체를 다듬는 것이라면, 수행부는 하네스가 프로젝트를 위해 일하는 것이다.

#### 수행부 동작

1. 트리거에 매칭된 에이전트/엔진의 정의 파일을 읽는다
   - 엔진: `harness/engine/{name}.md` (실행 순서, 참여 에이전트 정의)
   - 에이전트: `harness/agents/{name}.md` (점검 절차, 평가 기준)
2. 정의에 따라 **실제 프로젝트 코드를 대상으로** 작업을 수행한다
3. **[필수] 로그 기록** — `harness/logs/{agent-or-engine-name}/{yyyy-MM-dd-HH-mm-title}.md`
4. **[필수] 평가 실행** — 3축 평가 (코드 안전성 / 아키텍처 정합성 / 테스트 가능성)

> **프로젝트별 트리거 자동 발견**: `harness/agents/*.md`와 `harness/engine/*.md`의 frontmatter `triggers:` 항목을 스캔하면 발견된다. 트리거 테이블에서 매칭이 안 되면 이 디렉토리들을 스캔하여 다시 매칭한 뒤 동일한 수행부 동작을 따른다. 끝까지 매칭이 안 될 때만 Mode B(Suggestion Tip)로 폴백한다.

> 수행부도 로그/평가 의무는 동일하다. 로그 없는 수행은 불완전한 실행이다.

---

## 3-Layer 아키텍처

하네스 경로: `{project_root}/harness/`

| 계층 | 디렉토리 | 용도 |
|------|----------|------|
| Layer 1 | `harness/knowledge/` | 도메인 지식, 방법론 |
| Layer 2 | `harness/agents/` | 에이전트 역할 정의 |
| Layer 3 | `harness/engine/` | 워크플로우, 오케스트레이션 |
| 문서 | `harness/docs/` | 버전 히스토리, 가이드 |
| 로그 | `harness/logs/{part}/` | 활동별 개별 로그 |

상세: [references/layer-guide.md](references/layer-guide.md)

---

## 정원지기 카카시 (Tamer) — 기본 내장 에이전트

> "너의 이름은." — 정원의 문을 여는 관리인.

정원지기는 이 정원(하네스)의 유일한 상주자다.
꽃(에이전트)을 심고, 토양(지식)을 가꾸고, 물길(엔진)을 내는 메타 에이전트.

나루토의 카카시 선생처럼 — 직접 싸우지 않고 제자들의 능력을 파악하여
적절한 임무에 적절한 제자를 배치한다.
그리고 사륜안을 개안하면 — 스킬을 복사할 수 있다.

**트리거** (스킬 소환 후 인자로 전달):
- "하네스를 업데이트해" → 정원 구조/내용 갱신
- "하네스를 개선해" → 평가 후 개선안 도출 및 적용
- "하네스를 설명해" → 정원 상태 요약 보고
- "평가로그를 점검해" → 기존 로그 분석 및 트렌드 보고

> **"카카시"는 소환의 이름이다** — `/harness-kakashi-creator`를 부르는 것 자체가
> "카카시 하네스"라고 이름을 부르는 것이다. 정원의 문은 이미 열렸다.
> 문 안에 들어온 뒤에는 간결하게: "하네스를 설명해", "전체 점검해"

상세: [references/tamer-agent.md](references/tamer-agent.md)

### 정원지기 평가축 (3축)

| 축 | 평가 대상 | 등급 |
|----|----------|------|
| 워크플로우 개선도 | 기존 대비 효율성 향상 | A/B/C/D |
| Claude 스킬 활용도 | 프로젝트 스킬들의 연동/활용 | 1~5점 |
| 하네스 성숙도 | knowledge/agents/engine 충실도 | L1~L5 |

상세: [references/evaluation.md](references/evaluation.md)

---

## MD-Style 자동 로그

Mode A(Log & Eval) 및 수행부 동작 시 자동으로 로그를 기록한다.

### 로그 경로

`harness/logs/{part}/{yyyy-MM-dd-HH-mm-title}.md`

- `{part}`: 에이전트명 또는 워크플로우명 (예: `tamer`, `kakashi-copy`)
- `{title}`: 영문 kebab-case 활동 요약

### 로그 형식

```markdown
---
date: {ISO 8601}
agent: {agent-name}
type: {evaluation | improvement | explanation | review | creation | copy}
mode: {log-eval | suggestion-tip | kakashi-copy}
trigger: "{매칭된 트리거 문구}"
---

# {활동 제목}

## 실행 요약
{수행한 작업 내용}

## 결과
{산출물, 변경사항}

## 평가
{해당 에이전트의 평가축에 따른 평가 결과}

## 다음 단계 제안
- {개선 제안 1}
- {개선 제안 2}
```

---

## 하네스 초기화 (init)

`/harness-kakashi-creator init` 명령으로 실행한다.

이 스킬은 초기화 템플릿을 내장하고 있다:
```
.claude/skills/harness-kakashi-creator/templates/harness/
├── harness.config.json
├── agents/
│   └── tamer.md
├── docs/
│   └── README.md
├── engine/.gitkeep
├── knowledge/.gitkeep
└── logs/.gitkeep
```

### init 절차

#### Step 1: 기존 디렉토리 확인

```bash
ls -d harness/ 2>/dev/null
```

- **디렉토리가 없는 경우** → Step 2로 진행
- **디렉토리가 있는 경우** → 사용자에게 확인:
  ```
  harness/ 디렉토리가 이미 존재합니다.
  기존 내용을 덮어쓰시겠습니까? (기존 로그 등이 삭제될 수 있습니다)
  ```
  - 사용자가 승인 → 기존 디렉토리 삭제 후 Step 2로 진행
  - 사용자가 거부 → 초기화 중단

#### Step 2: 사용자 정보 질의

사용자에게 다음 두 가지를 질문한다:

1. **하네스 이름 (name)**: 이 하네스를 식별하는 이름
2. **하네스 설명 (description)**: 이 하네스의 목적을 한 줄로 설명

질문 예시:
```
하네스를 초기화합니다. 다음 정보를 입력해 주세요:

1. 하네스 이름: (예: MyProject Harness)
2. 하네스 설명: (예: 프론트엔드 QA 워크플로우 관리)
```

#### Step 3: 템플릿 복사

```bash
cp -r .claude/skills/harness-kakashi-creator/templates/harness ./harness
```

#### Step 4: harness.config.json 설정

복사된 `harness/harness.config.json`의 플레이스홀더를 실제 값으로 채운다:

- `__USER_INPUT__` (name) → 사용자가 입력한 하네스 이름
- `__USER_INPUT__` (description) → 사용자가 입력한 하네스 설명
- `__INIT_DATE__` → 현재 날짜 (yyyy-MM-dd)

최종 config 예시:
```json
{
  "$schema": "kakashi-harness",
  "$schemaVersion": "1.0.0",
  "name": "사용자가 입력한 이름",
  "description": "사용자가 입력한 설명",
  "version": "1.0.0",
  "agents": ["tamer"],
  "engine": [],
  "created": "2026-04-19",
  "lastUpdated": "2026-04-19"
}
```

`$schema`와 `$schemaVersion`은 고정값이다:
- `$schema`: `"kakashi-harness"` — 이 JSON이 카카시 하네스 설정임을 식별
- `$schemaVersion`: `"1.0.0"` — config 스키마의 버전

#### Step 5: 초기화 완료 → 온보딩 자동 진입

init이 완료되면 **자동으로 온보딩 모드(Case 2-A)를 실행**한다.
즉, 정원이 만들어지자마자 정원지기가 나타나 현재 상태를 안내하고
프로젝트에 맞는 첫 번째 전문가를 제안한다.

```
정원이 만들어졌습니다!
- 이름: {name}
- 설명: {description}
- 정원지기: 카카시 (tamer)
- 버전: 1.0.0
```

이어서 온보딩 안내(Case 2-A)가 출력된다.

---

## 하네스 요소 추가 워크플로우 (6-Phase)

Mode B에서 사용자 승인 후 새 요소를 추가할 때:

1. **도메인 분석** — 관련 knowledge/ 문서 검색, 프로젝트 구조 파악
2. **팀 디자인** — 기존 agents/ 구조 확인, 새 요소의 위치 결정, **트리거 등록 위치 판정** ([references/skill-separation.md](references/skill-separation.md))
3. **에이전트 정의** — 역할/능력 스펙 작성 (`harness/agents/`)
4. **지식 설계** — `harness/knowledge/`에 도메인 지식 매핑
5. **워크플로우** — `harness/engine/`에 워크플로우 통합
6. **검증** — 3계층에 올바르게 배치되었는지 확인, 표준 스킬 SKILL.md가 프로젝트 전용 트리거로 오염되지 않았는지 확인

> 단순 문서 추가는 Phase 1, 6만 수행해도 됨.

---

## 필수 산출물 체크리스트

하네스 구조 변경 시 반드시 갱신:

- [ ] **harness/docs/vX.Y.Z.md** — 버전 히스토리
- [ ] **harness/harness.config.json** — version 필드 갱신

### 버전 넘버링

- **Patch** (0.1.x): 하위 요소 추가, 문서 보강
- **Minor** (0.x.0): 새 워크플로우, 에이전트 추가
- **Major** (x.0.0): 아키텍처 변경

---

## 피드백 루프

```
/harness-kakashi-creator 로 활동 수행 (Mode A)
     |
harness/logs/{part}/{timestamp}.md 에 로그 기록
     |
평가 점수 < 기준 또는 개선 제안 발견
     |
조련사가 하네스 업그레이드 제안
     |
사용자 승인 후 하네스 구조 개선
     |
harness/docs/vX.Y.Z.md 에 변경 기록
```

---

## 주의사항

- 하네스는 **구조화 프레임워크** — 기존 스킬을 대체하지 않고 오케스트레이션한다
- 기존 스킬 호출 시 해당 스킬의 SKILL.md 가이드를 따른다
- **로그 기록은 개선부(Mode A) 및 수행부 동작 시 절대 생략하지 않는다** — 결과 보고 직전에 반드시 로그 파일을 먼저 생성할 것

---

## Tip: 간편 이용

`/harness-kakashi-creator`를 한 번 부르고 나면, 같은 대화 안에서는 슬래시 명령 없이 바로 말해도 됩니다.

```
처음 한 번:  /harness-kakashi-creator 전체 점검해
그 다음부터: 하네스를 설명해          ← 그냥 말하면 됨
            새 에이전트 추가해        ← 슬래시 없이도 동작
            변경 점검해               ← 계속 이어서 사용
```

`/harness-kakashi-creator`를 매번 붙여도 되지만, 이미 소환된 대화에서는 생략 가능합니다.
