---
name: herdr-adoption
agents: [tamer, code-coach, test-sentinel]
triggers:
  - "herdr 영입"
  - "herdr 도입"
  - "herdr 분석"
  - "herdr adoption"
description: |
  herdr(herdrdev/herdr, Rust ADE) 유용 기능을 AgentZero Lite에 영입하는 로드맵.
  참조본 E:\git-other\herdr (읽기 전용). 정본 Docs/agent-herdr/. 각 항목 착수/완료를
  harness/logs/herdr-adoption/ 에 로그 + 3축 평가.
---

# herdr Adoption

## Why this engine exists

herdr는 AgentZero와 같은 멀티-CLI 에이전트 셸이나, **에이전트 상태를 1급으로** 다루는
데 강하다(화면 기반 상태 감지, 롤업, 세션 복원, agent 대기 프리미티브). 그 아이디어만
선별 이식한다(구현은 Rust라 재사용 불가).

## Source of truth
- 분석/계획: `Docs/agent-herdr/README.md`
- 참조본: `E:\git-other\herdr` (읽기 전용)

## 영입 항목 (operator 선택)

| # | 기능 | 상태 |
|---|------|------|
| H1 | 스크린 매니페스트 상태 감지 (`AgentStateDetector` + 매니페스트) | 코어 완료 |
| H2 | 상태 롤업 + 미확인 done (`AgentStateRollup`) | 코어 완료 |
| H3 | 네이티브 세션 복원 (`AgentResumeCatalog`) | 코어 완료 |
| H5 | wait --until + 에이전트 별칭 + stall 가드 | 예정 |
| H4 | 훅 인스톨러 다중 CLI 확장 + 권위 우선순위 | 예정 |

## Steps (per item)
1. 착수 로그 `harness/logs/herdr-adoption/{ts}-{H}-start.md`.
2. 순수 로직 ZeroCommon(헤드리스) 우선, WPF 글루 분리.
3. 헤드리스 테스트.
4. 완료 로그 + 3축 평가.

## Evaluation rubric
| 축 | 측정 | 등급 |
|---|---|---|
| 코드 안전성 | 세션ID 주입 방지, 상태 오탐 최소 | A/B/C/D |
| 아키텍처 정합성 | 순수 로직 ZeroCommon, 액터 WPF 비의존 | Pass/Fail |
| 테스트 가능성 | 감지/롤업/복원 헤드리스 검증 | A/B/C/D |
| 이식 충실도 | herdr 개념 이식(재사용 아님) | Pass/Fail |

## Cross-references
- 정본: `Docs/agent-herdr/README.md`
- 참조본: `E:\git-other\herdr`
- 관련 코드: `Project/ZeroCommon/Agents/AgentStateDetector.cs`, `AgentManifestCatalog.cs`,
  `AgentStateRollup.cs`, `AgentResumeCatalog.cs`
- 기존 연관: `ApprovalParser`(승인 감지, H1 blocked와 상보), W1 `AgentHookInstaller`(H4 확장 대상)
