---
date: 2026-11-03T19:40:00+09:00
agent: tamer
type: recruit
mode: suggestion-tip
command: "/harness-creator"
---

# agentone-observer 영입

## 실행 요약

operator 요청: agent-one 중간평가를 전담하는 전문가를 만들고 그 지식으로 관리, 하네스 업데이트.
agent-one 런타임 행동을 **로그로 관찰·평가**하는 레인이 비어 있어(기존 `agent-loop-auditor`는 소스 불변식,
`code-coach`/`security-guard`는 landing 변경 담당) specialist 1명을 영입.

5-파일 패턴으로 생성:
1. `harness/agents/agentone-observer.md` — 정의(프로젝트 전용 트리거, frontmatter 자동발견)
2. `harness/knowledge/agentone-observer/capability-track.md` — canon(로그 경로·파싱 레시피·실패 카탈로그)
3. `harness/knowledge/agentone-observer/midterm-2026-11-03.md` — 1차 능력평가(7턴)
4. `harness.config.json` — agents·knowledge_subdirs 추가, 1.14.2 → **1.15.0**
5. `harness/docs/v1.15.0.md` — 영입 기록

## 결과

- 전문가 수 9 → 10. 트리거 중복 없음(기존 "루프 점검" 계열과 분리된 "agent-one 관찰/중간평가" 계열).
- 도구/지식 분리 준수: 정의엔 역할·4축, canon엔 경로·레시피·실패 카탈로그.
- PDSA 채택 루프로 선언(능력 육성은 개선 사이클이므로).

## 평가

- **워크플로우 개선도: B+** — 증분 관찰이 구조화됨(요청 시에만, 로그 근거 필수). 엔진화는 아직 불필요해 A는 보류.
- **Claude 스킬 활용도: 2/5** — 관찰은 로그 파싱 중심. 소스 개선 발견 시 code-coach/agent-loop-auditor 연동 여지.
- **하네스 성숙도: L4 유지** — specialist 1 + knowledge subdir 1 추가. 아키텍처 변화 없음.

## 다음 단계 제안

- 다음 증분 체크부터 `agent-one 중간평가` 트리거로 이 전문가 호출.
- midterm 기록의 Act(턴당 1 phase·완주 루틴 강제, Gemma 한글 arg 파싱, ProviderError 재시도, CDP 포트 충돌)를
  다음 관찰에서 대조.
