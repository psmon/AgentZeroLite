---
date: 2026-08-20T03:10:00+09:00
agent: tamer
type: creation
mode: suggestion-tip
trigger: "두가지 제안 진행해죠"
follows: harness/logs/code-coach/2026-08-20-02-52-pr12-p0-cleanup-verification-review.md
---

# pr-review 엔진 신설 + PR #12 범위 밖 발견 이슈 인계

## 실행 요약

직전 PR #12 검증 리뷰에서 도출된 두 갈래를 처리했다.

1. **범위 밖 발견 4건을 GitHub 이슈로 인계** — code-coach 의
   "GitHub issue handoff" 절차(`harness/agents/code-coach.md:105-160`)에 따라
   3개 이슈로 클러스터링해 발행.
2. **`pr-review` 엔진 신설** — PR 단위 검증 워크플로우가 정원에 없어
   code-coach / build-doctor / test-runner / security-guard 를 매번 수동
   조합하던 것을 파일로 고정.

## 결과

### 1. 이슈 인계 (3건)

| # | 제목 | 라벨 | 묶음 |
|---|---|---|---|
| [#13](https://github.com/psmon/AgentZeroLite/issues/13) | os-cli-e2e-smoke gate is non-functional — 2 Should-fix | `bug` | 리뷰 발견 #10 + #11 |
| [#14](https://github.com/psmon/AgentZeroLite/issues/14) | AgentTest suite under-reports — varying run count (143–151) | `bug` | 리뷰 발견 #12 |
| [#15](https://github.com/psmon/AgentZeroLite/issues/15) | Stale NU1903 pin: System.Security.Cryptography.Xml 10.0.7 | `bug` | 리뷰 발견 #13 |

클러스터링 근거 — #10/#11 은 **같은 게이트(M0014 acceptance probe)를 동시에
막고 있어** 하나로 묶었다. 나머지 둘은 소유 영역(테스트 하네스 / 의존성 핀)이
달라 분리. 발견 4건 → 이슈 3건.

`#15` 는 심각도를 **Suggestion 으로 낮춰** 기재했다. `PrivateAssets=all` 인
build-time-only 오버라이드라 win-x64 self-contained 산출물에 실리지 않기
때문. High advisory 5건이라는 raw 신호만 보고 Should-fix 로 올렸으면 오탐이
됐을 자리다. 대신 "Release publish 산출물에 해당 DLL 부재 확인"을
Closes-when 에 넣어 이 주장 자체를 검증 대상으로 남겼다.

### 2. `pr-review` 엔진 (v1.11.0)

| 경로 | 변경 |
|---|---|
| `harness/engine/pr-review.md` | 신규 — 엔진 정의 |
| `harness/harness.config.json` | `engine[]` += `pr-review`, 1.10.0 → 1.11.0 |
| `harness/docs/v1.11.0.md` | 신규 — 버전 히스토리 |

`pre-commit-review` 와의 경계를 의도적으로 유지했다. 후자를 확장하지 않은
이유: staged diff 게이트가 무거워지면 일주일 안에 우회당한다. 둘은 규모가
아니라 **증거의 종류**가 다르다 — 전자는 diff 판독, 후자는 빌드/테스트/
베이스라인/e2e 측정.

엔진에 못 박은 규율 3가지 (첫 수행에서 즉흥적으로 했던 것들):

- **베이스라인은 worktree, `git checkout` 금지** — 리뷰 대상 브랜치를
  변형시키고 중단 시 operator 를 고립시킨다.
- **PR 탓하기 전에 probe 를 의심** — 이번에 e2e 실패의 원인이 제품이 아니라
  스크립트였다(#13). 이 단계를 건너뛰는 엔진은 인프라 부패를 다음 PR
  작성자에게 뒤집어씌운다.
- **in-scope / out-of-scope 분리** — 섞으면 "이 머지를 막는 게 무엇인가"를
  읽을 수 없다.

**비파괴 계약**: `gh pr merge` / `gh pr close` / `gh pr review --approve`
금지를 엔진 본문에 명시. 산출물은 코멘트와 이슈뿐. 닫을 수 있는 자문
리뷰어는 자문이 아니다.

## 평가 (정원지기 3축)

| 축 | 판정 | 근거 |
|---|---|---|
| 워크플로우 개선도 | **A-** | 매 PR 마다 재유도하던 8단계를 파일로 고정. 첫 수행 실측(베이스라인 478 vs 본문 주장 473)이 엔진의 존재 이유를 자체 입증. A 가 아닌 이유는 아직 1회 수행분에서만 추출돼 일반화 검증이 없음 |
| Claude 스킬 활용도 | **4/5** | code-coach 의 issue handoff 절차를 그대로 재사용, os-cli-e2e-smoke 를 하위 단계로 호출. agentzero-cli 스킬의 WinExe 호출 규약이 #13 의 근본 원인 진단에 직결 — 스킬 간 연결이 실제로 작동 |
| 하네스 성숙도 | **L4** | agents 8 / engine 10 / knowledge 31. 엔진이 서로를 호출하고(pr-review → os-cli-e2e-smoke), 엔진이 자기 인프라의 결함을 이슈로 추적하는 단계. L5 는 hazard table 이 이슈 종결과 함께 자동 동기화될 때 |

## 다음 단계 제안

- **hazard table 유지보수 부채** — `pr-review.md` 의 "Known gate hazards" 는
  #13/#14/#15 종결 시 행을 빼야 한다. 낡은 hazard table 은 실제 발견을
  억제하므로 실패 방향이 더 나쁘다. 이슈 종결 시 엔진 갱신을 mission-dispatch
  마감 절차에 걸 수 있는지 검토.
- **첫 정식 수행** — 다음 PR 에서 `pr-review` 를 트리거로 돌려
  `harness/logs/pr-review/` 첫 로그를 남기고, 8단계가 실제로 재현 가능한지
  확인. 이번 건은 엔진 추출의 소재였을 뿐 엔진 수행이 아니다.
- **PR #12 본문 정정** — baseline 473 → 478, +10 → +5 는 아직 operator 미반영.
