# Loop Engineering — a procedure the AI *can't* skip

**English · [한국어](#한국어)**

> The theory behind the PDSA loop this project ships, and why a **graph** —
> not a prompt — is what makes an AI agent actually follow a procedure.

---

## The failure mode: instruction-only flow control

Most agent "harnesses" steer the model with **instructions**: a system prompt or
a skill file that says *"first do A, then B, and only then C."* This works — right
up until it doesn't.

An agent's context window is finite, and over a long run it gets **compressed**:
summarized, truncated, rolled forward. Compression is **lossy by definition**, and
the step it silently drops is disproportionately the *load-bearing* one — the gate,
the verification, the "don't skip this" clause — because those are terse and easy
to summarize away, while the noisy transcript around them survives.

So the model, acting in good faith, runs A → C and skips B. Nothing errored. The
prompt still "said" to do B; there just wasn't a B left in the context that
survived to the moment of decision.

### Why procedures are the worst case

This is tolerable for open-ended work ("summarize these files") where order barely
matters. It is **corrosive** for a **procedure-bound flow** where order and gates
are the whole point:

- *verify before you commit*
- *security gate before you release*
- *Study the result before you Act on it*

Enforcing those with prose alone is a bet that the model will still be holding the
prose at the exact instant it matters. Under compression, that bet loses — quietly,
and usually in the cases you least want.

---

## The principle: procedure as a graph, not a prompt

Stop trusting prose for **sequencing**. Move the order and the gates out of the
prompt and into a **graph** whose edges are owned by the runtime:

```
        ┌──────┐     ┌──────┐     ┌───────┐     ┌──────┐
   ┌───►│ Plan │────►│  Do  │────►│ Study │────►│ Act  │───┐
   │    └──────┘     └──────┘     └───────┘     └──────┘   │
   │                                                       │
   └──────────────── reinforce (only if unmet) ◄───────────┘
```

The division of labor is the whole idea:

| Concern | Owner |
|---|---|
| **What order** the steps run in | the **graph** (edges) — deterministic, can't be summarized away |
| **What gates** must pass between steps | the **graph** (junctions / conditional edges) |
| **The creative work inside each step** | the **LLM** (fills the node) |

The model **cannot** run Act before Study, because there is no edge that permits
it. The graph is the part of the system that **does not forget**. The LLM is still
doing the intelligent work — writing the plan, doing the task, judging the result —
but it does that work *at a node the graph put it in*, not in an order it has to
remember.

This is not "constrain the model so it can't think." It's the opposite: the model
is freed to think hard **inside** each step precisely because it no longer has to
also remember, unaided, the shape of the whole procedure.

---

## The loop this project engineers: PDSA

The canonical improvement loop is **PDSA** — W. Edwards Deming's
*Plan → Do → Study → Act* cycle (the rigorous ancestor of "PDCA"; *Study*, not
*Check*, because the point is to **learn**, not merely to pass/fail):

- **Plan** — the LLM commits to a **verifiable expected outcome** (a metric), not a
  vibe. "I expect X, measured by Y."
- **Do** — the work is carried out toward testing that expectation.
- **Study** — the LLM judges the result against the expectation it set:
  **met / partial / unmet** — plus the measured actual.
- **Act** — learnings are recorded; if the expectation was unmet, the next `Plan`
  is auto-linked as a **reinforcement cycle** so the process corrects itself.

Every cycle accumulates into a **per-project graph memory** (an embedded Kùzu
graph DB): nodes for cycles and verdicts, `REINFORCES` edges linking a follow-up to
what it follows up. Over time that graph *is* the agent's long-term memory of how
its own process behaves — an **expectation hit-rate** (met ÷ judged) you can read
back. The loop improves the **process**, not just the immediate task.

---

## Why a graph engine (Akka Streams graph DSL)

The loop is built with the **Akka Streams graph DSL** in the sibling project
[`akka-graph-loop`](https://github.com/psmon/akka-graph-loop), shipped as a single
**.NET Native-AOT** binary, `@webnori/pdsa`.

A streams graph DSL is a natural fit for loop engineering because:

- **The topology is a blueprint, not a runtime hope.** Plan→Do→Study→Act and the
  reinforcement feedback edge are declared *once* as graph structure and
  materialized; the sequence is a property of the wiring, not of any prompt.
- **Junctions model gates.** Conditional edges / merge points express "only proceed
  when …" as first-class graph elements.
- **Backpressure and lifecycle come for free.** The stream runtime owns
  advancement, so a step can't race ahead of the one feeding it.
- **It composes.** New stages and side-loops graft onto the blueprint without
  rewriting the flow — which is exactly what you want when you *engineer* a loop
  for a new domain.

The LLM is attached at the nodes (Plan coaching, Study judging); the graph owns
everything between them.

---

## How AgentZero exposes it

AgentZero Lite hosts real agent CLIs in its terminals, so the most direct way to
give a hosted agent a graph-enforced loop is a **CLI it can call from its own
terminal**:

```bash
npm i -g @webnori/pdsa       # one Native-AOT binary for your platform
pdsa project set my-repo     # a per-project graph DB
pdsa plan  "what & why & how"  # → the LLM sets a verifiable expectation
pdsa do    "what you did"
pdsa study "results / metrics" # → met | partial | unmet
pdsa act   --note "memo"       # → learnings + auto-linked reinforcement
pdsa status                    # progress + expectation hit-rate
pdsa view                      # the accumulated graph, in a local viewer
```

The harness documents the full contract — command surface, LLM auth modes, the
graph-memory model, and how it differs from the dashboard's display-only "PDSA
insight" — at
[`harness/knowledge/tamer/pdsa-cli.md`](../harness/knowledge/tamer/pdsa-cli.md), and
the `tamer` agent knows how to drive a cycle from a harness trigger.

---

## Applying it to your own project

The point of documenting the **concept** — not just the tool — is that you can
**engineer your own loop**:

1. **Name the procedure that must not be skipped** (yours may not be PDSA — it could
   be intake → triage → fix → verify, or plan → review → apply → rollback-guard).
2. **Draw it as a graph**: nodes = steps the LLM performs, edges = the order and the
   gates the runtime enforces.
3. **Put the LLM at the nodes, the guarantees at the edges.** Anything that *must*
   happen becomes an edge, not a sentence.
4. **Give each cycle a memory** so the loop improves the process across runs.

`akka-graph-loop` / `@webnori/pdsa` is open source — fork it and tune the graph to
your domain. AgentZero is one worked example of hosting such a loop next to real
agent terminals; the concept is portable to any harness.

## References

- Sibling engine + full docs (EN/KO): <https://github.com/psmon/akka-graph-loop>
- PDSA — history, theory, PDCA vs PDSA (fact-checked): the `PDSA.md` in that repo
- Harness integration contract: [`harness/knowledge/tamer/pdsa-cli.md`](../harness/knowledge/tamer/pdsa-cli.md)
- Deming, W.E. — *Out of the Crisis* (the PDSA / System of Profound Knowledge source)

---

<a name="한국어"></a>
# 루프 엔지니어링 — AI가 *건너뛸 수 없는* 절차

**[English](#loop-engineering--a-procedure-the-ai-cant-skip) · 한국어**

> 이 프로젝트가 배포하는 PDSA 루프의 이론과, AI 에이전트가 절차를 실제로 준수하게
> 만드는 것이 왜 프롬프트가 아니라 **그래프**인지에 대한 글.

## 실패 모드: 지침만의 flow 제어

대부분의 에이전트 "하네스"는 모델을 **지침**으로 조종합니다 — 시스템 프롬프트나
스킬 파일에 *"먼저 A, 다음 B, 그다음에야 C"* 라고 적습니다. 잘 작동합니다 —
안 될 때까지는요.

에이전트의 컨텍스트 창은 유한하고, 긴 실행에서 **압축**됩니다: 요약·절단·이월.
압축은 **정의상 손실적**이며, 조용히 누락되는 그 단계가 하필 *핵심(load-bearing)*
단계인 경우가 압도적으로 많습니다 — 게이트, 검증, "이건 건너뛰지 마" 조항.
이런 것들은 짧아서 요약에 쉽게 사라지고, 정작 주변의 시끄러운 트랜스크립트는
살아남기 때문입니다.

그래서 모델은 선의로 A → C 를 실행하고 B 를 건너뜁니다. 아무 에러도 없습니다.
프롬프트는 여전히 B 를 "하라고 했"지만, 결정하는 그 순간까지 살아남은 B 가
컨텍스트에 없었을 뿐입니다.

### 왜 절차가 최악의 경우인가

순서가 거의 중요하지 않은 열린 작업("이 파일들 요약해줘")에서는 견딜 만합니다.
하지만 순서와 게이트가 전부인 **절차 준수 flow**에서는 **치명적**입니다:

- *커밋 전 검증*
- *릴리스 전 보안 게이트*
- *Act 하기 전에 결과를 Study*

이것들을 문장만으로 강제하는 것은, 정작 중요한 그 순간에 모델이 그 문장을 아직
쥐고 있으리라는 **도박**입니다. 압축 아래에서 그 도박은 집니다 — 조용히, 그리고
대개 가장 원치 않는 경우에.

## 원칙: 절차를 프롬프트가 아니라 그래프로

**순서**를 문장에 맡기지 마세요. 순서와 게이트를 프롬프트 밖으로 꺼내, 엣지를
런타임이 소유하는 **그래프**로 옮깁니다:

```
        ┌──────┐     ┌──────┐     ┌───────┐     ┌──────┐
   ┌───►│ Plan │────►│  Do  │────►│ Study │────►│ Act  │───┐
   │    └──────┘     └──────┘     └───────┘     └──────┘   │
   │                                                       │
   └──────────── reinforce (unmet일 때만) ◄─────────────────┘
```

역할 분담이 핵심 전부입니다:

| 관심사 | 소유자 |
|---|---|
| 단계가 실행되는 **순서** | **그래프**(엣지) — 결정적, 요약으로 사라지지 않음 |
| 단계 사이에 통과해야 하는 **게이트** | **그래프**(정션 / 조건부 엣지) |
| 각 단계 **안의** 창의적 작업 | **LLM**(노드를 채움) |

모델은 Study 보다 Act 를 먼저 실행할 수 **없습니다** — 그것을 허용하는 엣지가
없으니까요. 그래프가 곧 시스템에서 **잊지 않는 부분**입니다. LLM은 여전히 지적인
작업을 합니다 — 계획을 쓰고, 작업을 하고, 결과를 판정 — 다만 *그래프가 넣어준
노드에서* 그렇게 하지, 스스로 기억해야 하는 순서로 하지 않습니다.

이는 "모델이 생각 못 하게 제약한다"가 아닙니다. 정반대입니다: 모델은 절차 전체의
형태를 홀로 기억할 필요가 없어졌기에, 각 단계 **안에서** 더 깊이 생각할 자유를
얻습니다.

## 이 프로젝트가 엔지니어링하는 루프: PDSA

정전(正典)적 개선 루프는 **PDSA** — W. Edwards Deming의
*Plan → Do → Study → Act* 사이클입니다("PDCA"의 엄밀한 조상; *Check* 가 아니라
*Study* 인 이유는 목적이 통과/실패가 아니라 **학습**이기 때문):

- **Plan** — LLM이 분위기가 아니라 **검증 가능한 기대 결과**(측정지표)를 약속.
  "나는 X 를 기대하고, Y 로 측정한다."
- **Do** — 그 기대를 검증하는 방향으로 작업 수행.
- **Study** — 스스로 세운 기대 대비 결과를 판정: **met / partial / unmet** +
  측정된 실제값.
- **Act** — 학습을 기록; 기대가 미충족이면 다음 `Plan`이 **보강 사이클**로 자동
  연결되어 프로세스가 스스로 교정.

각 사이클은 **프로젝트별 그래프 메모리**(임베디드 Kùzu 그래프 DB)에 누적됩니다:
사이클·판정 노드, 후속을 앞 사이클에 잇는 `REINFORCES` 엣지. 시간이 지나면 그
그래프가 곧 에이전트가 자기 프로세스의 거동을 기억하는 장기 기억이 됩니다 —
되읽을 수 있는 **기대 충족률**(met ÷ 판정). 루프는 눈앞의 작업만이 아니라
**프로세스**를 개선합니다.

## 왜 그래프 엔진인가 (Akka Streams 그래프 DSL)

루프는 자매 프로젝트
[`akka-graph-loop`](https://github.com/psmon/akka-graph-loop)에서 **Akka Streams
그래프 DSL**로 만들어졌고, 단일 **.NET Native-AOT** 바이너리 `@webnori/pdsa`로
배포됩니다.

스트림 그래프 DSL이 루프 엔지니어링에 잘 맞는 이유:

- **토폴로지가 런타임의 희망이 아니라 청사진입니다.** Plan→Do→Study→Act 와 보강
  피드백 엣지를 그래프 구조로 *한 번* 선언해 materialize 합니다; 순서는 배선의
  속성이지 어떤 프롬프트의 속성이 아닙니다.
- **정션이 게이트를 모델링합니다.** 조건부 엣지 / 병합점이 "…일 때만 진행"을
  일급 그래프 요소로 표현합니다.
- **백프레셔와 수명주기가 공짜입니다.** 스트림 런타임이 진행을 소유하므로, 한
  단계가 자기를 먹여주는 단계보다 앞서 달릴 수 없습니다.
- **조합됩니다.** 새 스테이지와 사이드 루프가 flow 를 다시 쓰지 않고 청사진에
  접붙습니다 — 새 도메인용 루프를 *엔지니어링*할 때 정확히 원하는 성질입니다.

LLM은 노드(Plan 코칭, Study 판정)에 붙고, 그 사이의 모든 것은 그래프가 소유합니다.

## AgentZero가 제공하는 방식

AgentZero Lite는 터미널에 실제 에이전트 CLI를 호스팅하므로, 호스팅된 에이전트에게
그래프로 강제되는 루프를 주는 가장 직접적인 방법은 **자기 터미널에서 호출하는
CLI**입니다:

```bash
npm i -g @webnori/pdsa       # 플랫폼별 Native-AOT 바이너리 하나
pdsa project set my-repo     # 프로젝트별 그래프 DB
pdsa plan  "무엇을 왜 어떻게"   # → LLM이 검증 가능한 기대를 수립
pdsa do    "실제로 한 것"
pdsa study "결과 / 수치"       # → met | partial | unmet
pdsa act   --note "메모"       # → 학습 + 보강 사이클 자동 연결
pdsa status                    # 진행 + 기대 충족률
pdsa view                      # 누적된 그래프를 로컬 뷰어로
```

하네스는 전체 계약 — 명령 표면, LLM 인증 방식, 그래프 메모리 모델, 그리고
대시보드의 표시 전용 "PDSA insight"와의 차이 — 를
[`harness/knowledge/tamer/pdsa-cli.md`](../harness/knowledge/tamer/pdsa-cli.md)에
문서화했고, `tamer` 에이전트가 하네스 트리거로 사이클을 구동할 줄 압니다.

## 여러분의 프로젝트에 적용하기

도구가 아니라 **개념**을 문서화하는 이유는, 여러분이 **자신만의 루프를
엔지니어링**할 수 있게 하기 위함입니다:

1. **건너뛰면 안 되는 절차를 명명하세요**(PDSA가 아닐 수 있습니다 — 접수→분류→
   수정→검증, 또는 계획→리뷰→적용→롤백가드 일 수도).
2. **그래프로 그리세요**: 노드 = LLM이 수행하는 단계, 엣지 = 런타임이 강제하는
   순서와 게이트.
3. **LLM은 노드에, 보장은 엣지에.** *반드시* 일어나야 하는 것은 문장이 아니라
   엣지가 됩니다.
4. **각 사이클에 기억을 주세요** — 루프가 실행을 거쳐 프로세스를 개선하도록.

`akka-graph-loop` / `@webnori/pdsa`는 오픈소스입니다 — fork 해서 그래프를 여러분
도메인에 맞게 조정하세요. AgentZero는 그런 루프를 실제 에이전트 터미널 옆에서
호스팅하는 하나의 완성 예시이며, 개념 자체는 어떤 하네스로도 이식 가능합니다.

## 참고

- 자매 엔진 + 전체 문서(한/영): <https://github.com/psmon/akka-graph-loop>
- PDSA — 역사·이론·PDCA vs PDSA(사실검증): 그 저장소의 `PDSA.md`
- 하네스 통합 계약: [`harness/knowledge/tamer/pdsa-cli.md`](../harness/knowledge/tamer/pdsa-cli.md)
- Deming, W.E. — *Out of the Crisis* (PDSA / 심오한 지식의 체계 원전)
