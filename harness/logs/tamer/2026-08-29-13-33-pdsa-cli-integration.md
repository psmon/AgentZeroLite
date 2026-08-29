---
date: 2026-08-29T13:33:00+09:00
agent: tamer
type: creation
mode: suggestion-tip
trigger: "pdsa cli 있음 ... 전문가가 이용할수 있도록 조사진행"
---

# PDSA CLI (`@webnori/pdsa`) 하네스 통합

## 실행 요약

operator 가 전역 설치한 npm CLI `@webnori/pdsa` (v0.0.5) 를 조사하고, 하네스
전문가들이 이용할 수 있도록 knowledge + tamer 레이어에 통합했다.

- **정체 파악**: `@webnori/pdsa` 는 Deming PDSA(Plan→Do→Study→Act) 지속개선
  루프를 코칭하고 각 사이클을 **프로젝트별 Kùzu 그래프 DB**에 누적하는 .NET
  Native AOT CLI. Plan 의 기대평가를 Study 에서 LLM 이 met/partial/unmet 판정,
  필요 시 `REINFORCES` 보강 사이클 자동 연결. (harness-view 의 `pdsa-insight.json`
  = 표시용 요약과는 **별개**.)
- **참고자료**: `pdsa init --lang en` 으로 `.claude/skills/pdsa/SKILL.md` 를
  설치해 도구 저자의 사용 의도(plan/do/study/act 호출 순서·출력 태그)를 확인한
  뒤, operator 지시에 따라 스킬을 **삭제** (별도 스킬로 남기지 않음).
- **레포 셋업**: `pdsa project set agentzero-lite` (DB 생성),
  `pdsa check` → LLM 왕복 OK(1309ms, apikey/gpt-5.6-terra).

## 결과

| 항목 | 변경 |
|---|---|
| `harness/knowledge/tamer/pdsa-cli.md` | 신규 — 명령 표면·인증·그래프 메모리·anti-pattern·PDSA 구분표 |
| `harness/agents/tamer.md` | 트리거 5종 + "PDSA 개선 사이클" 절차 + convention backlink |
| `harness/knowledge/README.md` | 트리에 `tamer/pdsa-cli.md` 추가 |
| `harness/harness.config.json` | version 1.11.0 → 1.12.0, lastUpdated |
| `harness/docs/v1.12.0.md` | 신규 — 버전 히스토리 |

통합 깊이 = **knowledge + tamer** (operator 확정). 전용 엔진은 신설하지 않음.

## 평가

| 축 | 결과 |
|----|------|
| 워크플로우 개선도 | **B** — 새 외부 도구를 재유도 없이 tamer 절차로 고정. 다만 실사이클 축적 전이라 효과는 잠재적. |
| Claude 스킬 활용도 | **3/5** — pdsa CLI + skill-creator(init 참고) 연동. harness-view PDSA 와 명시 분리. |
| 하네스 성숙도 | **L4** — knowledge/agent 2계층 충실, os-control.md 컨벤션 준수, Rule 1~4 통과. 엔진 미신설로 L5 아님(의도적). |

creator-rule 위반 자체검사: Rule 1(inline 호출 없음)/2(단일 agent, engine 불필요)/
3(knowledge 무트리거)/4(per-agent 배치) 모두 통과.

## 다음 단계 제안

- 실제 개선 사이클을 1~2회 돌려 그래프 메모리에 시드(예: 최근 릴리즈 게이트
  회고)를 남기면 recall 지표가 의미를 갖기 시작한다.
- 여러 전문가가 PDSA 를 공유해야 하면 `pdsa-retro` 엔진으로 승격 + `--project
  agentzero-lite-<role>` 역할 분리 (Rule 2).
