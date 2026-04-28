---
date: 2026-04-27T15:50:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "지금 리뷰 git issue 등록해죠 ..등록후 관련 플로우도 업데이트.. 코드코치의 리뷰의 개선권고 이상인 경우 이슈에 등록한다로"
---

# code-coach GitHub issue handoff policy — Harness v1.1.4

## 실행 요약

방금 끝난 code-coach Mode 2 리뷰(5 Suggestion)를 *실제 GitHub 이슈*로
등록하고, 동일 정책을 향후 모든 리뷰에 적용하도록 하네스 워크플로우를
갱신. 정책 핵심: **finding ≥ Suggestion → issue 1개 filing** (zero-finding
리뷰는 skip).

수행 단계:
1. `gh repo view`로 repo 슬러그 확인 (`psmon/AgentZeroLite`)
2. 리뷰 결과를 영문 이슈 본문으로 정리 → `gh issue create` → **#1 생성**
3. `harness/agents/code-coach.md` 갱신:
   - Mode 1 / Mode 2 절차에 "step 6: file issue" 추가
   - 신규 섹션 "GitHub issue handoff" — 절차 + 본문 템플릿 + 라벨 규칙 + severity→action 매핑
   - Evaluation rubric에 "Issue handoff" pass/fail 축 추가
4. `harness/engine/pre-commit-review.md` "Output" 섹션 갱신 — 이슈 URL을 엔진 로그에 기록
5. 메모리 `project_pre_commit_code_coach.md` 갱신 — step 5 + exit criteria
6. `harness.config.json` 1.1.3 → 1.1.4 + `harness/docs/v1.1.4.md` 신규
7. 본 로그 작성

## 결과

### 갱신 파일 (5개 + 1개 신규 + 1개 메모리)

| 파일 | 변경 |
|---|---|
| `harness/agents/code-coach.md` | Mode 1 step 6, Mode 2 step 6, 신규 GitHub issue handoff 섹션, rubric +1 axis |
| `harness/engine/pre-commit-review.md` | Output 섹션 issue 항목 + 실패 모드 정책 (`gh` 실패해도 commit 진행) |
| `harness/harness.config.json` | 1.1.3 → 1.1.4 |
| `harness/docs/v1.1.4.md` | **신규** 버전 히스토리 + 검증 체크리스트 + 롤백 절차 |
| `memory/project_pre_commit_code_coach.md` | step 5 + exit criteria + #1 first-issue marker |
| `harness/logs/tamer/2026-04-27-15-50-issue-handoff-policy.md` | 본 로그 |

### 등록된 이슈

[`psmon/AgentZeroLite#1`](https://github.com/psmon/AgentZeroLite/issues/1)
— "code-coach review: ReActActor guard rollout (P0-1) — 5 advisory suggestions"
- Label: `enhancement`
- Body 구조: Source / Verdict / Findings (S-1~S-6) / Recommendation / Closes-when
- 본 정책 하의 첫 사례 — 미래 issue 형식의 reference로 활용 가능

### 정책 핵심 (severity → action 매핑)

| Severity | Issue filed? | Commit blocked? |
|---|:---:|:---:|
| Must-fix | yes | yes (until applied or explicitly waived) |
| Should-fix | yes | no, but commit message must reference the issue |
| Suggestion | yes | no |
| (no findings) | no | no |

### 정책의 핵심 의도 (왜 Suggestion부터?)

이전엔 *Should-fix 미만은 로그에만 남았다* → 사용자가 "ignore and commit"
하면 Suggestion이 사라짐. 정책을 **Suggestion부터** filing으로 잡아 *opt-out*
모델로 만들었다. 노이즈 우려가 생기면:
- 임계 상향 (Suggestion → Should-fix)
- 또는 enhancement 라벨 이슈 자동 close 정책

으로 *완화 가능* (롤백 절차는 v1.1.4 doc에 명시).

## 평가 (정원지기 3축)

| 축 | 평가 | 근거 |
|---|---|---|
| **워크플로우 개선도** | **A** | 단일 사용자 지시 → 즉시 실 행동(이슈 #1 filing)으로 변환 + 동일 흐름이 향후 모든 리뷰에 자동 적용. *advisory finding이 휘발되는 hole*을 1개 patch로 봉합. |
| **Claude 스킬 활용도** | **5/5** | `gh` CLI 직접 사용으로 추가 도구 도입 0. 이슈 본문이 영어(R-1 준수), 라벨 규칙은 기본 라벨만 사용해 라벨 지옥 회피. |
| **하네스 성숙도** | **L4+** | code-coach가 *advisory 흐름의 출구*까지 가진 완결된 에이전트로 격상. v1.1.3 reference protocol과 v1.1.4 issue handoff가 **두 개의 독립적 외부 채널**(스냅샷 + GitHub)을 하네스에 binding하여 정원이 외부 시스템과 본격 통합. |

### 잘한 점
- 이슈를 *지금 한 번* 만들고 끝내는 게 아니라 *정책*으로 등록 — 미래 모든 리뷰가 동일 흐름 자동 적용
- 본문 템플릿 명문화 (Source / Verdict / Findings / Recommendation / Closes-when) — 이슈 형식 일관성 보장
- 실패 모드 명시 (`gh` 실패해도 commit 진행) — 정책이 commit blocker가 되지 않도록 안전장치
- 메모리에 첫 이슈 마커 (`psmon/AgentZeroLite#1`) — 정책 시행 시점 추적 가능
- 롤백 절차 문서화 (v1.1.4 doc) — 노이즈 발생 시 안전하게 되돌릴 수 있음

### 부족한 점 / 후속 검토
- **이슈 close 자동화 미정** — 사용자가 Suggestion을 적용했을 때 이슈가 자동으로 닫히는 메커니즘 없음. 수동 close 또는 PR `Closes #N` 키워드 의존. 운영 데이터로 노이즈 측정 후 자동화 검토.
- **Should-fix와 Must-fix가 한 이슈에 섞일 수 있음** — 현재 정책은 *리뷰당 이슈 1개*. Severity 별로 분리할지는 향후 결정.
- **commit message에 이슈 참조 의무화는 Should-fix/Must-fix만** — Suggestion-only 이슈는 commit과 자동 연결되지 않음 (수동 `Refs #N` 가능). 운영해 보고 정책 강화 검토.

## 다음 단계 제안

### 즉시
- 본 변경 + TOP 1 코드 변경을 **함께** commit (이번 commit은 리뷰의 issue handoff 정책 적용 후 첫 commit이지만, 이슈는 이미 #1로 filing되었으므로 정책상 충돌 없음)
- commit message에 `Refs #1` 추가 검토

### 분기 단위
- 1~2개월 후 issue list 점검 — close 비율 / aging 분포 / Suggestion-only vs Should-fix+ 구성비
- close 비율 < 30% 면 *임계 상향* (Suggestion → Should-fix) 검토
- close 비율 > 70% 면 정책 유지 + close 자동화 추가 검토

### 메타 (하네스 자체)
- security-guard / build-doctor / test-sentinel에도 동일한 issue handoff 정책 확장 검토 (v1.2.0?)
- 현재는 code-coach 단독 — 다른 에이전트는 finding 형식이 다르므로 일률 적용은 시기상조

---

## 트리거 매칭 로그

| 항목 | 값 |
|---|---|
| 매칭된 트리거 | 사용자 직접 지시 ("리뷰 git issue 등록 + 정책화") |
| 진입 모드 | 개선부 Mode A: Log & Eval (정책 갱신 + 외부 시스템 binding) |
| 사용한 도구 | Bash (gh CLI) ×3, Edit ×6, Write ×2, Read ×1 |
| 외부 호출 | `gh issue create` 1회 — `psmon/AgentZeroLite#1` 생성 (사용자 권한으로 실행) |
| 안전 게이트 | 코드 변경 0 (정책 + 문서만), 외부 게시는 사용자 명시 지시 후 수행 |

## 관련 산출물

- 등록된 이슈: https://github.com/psmon/AgentZeroLite/issues/1
- 정책 정의: [harness/agents/code-coach.md](../../agents/code-coach.md) "GitHub issue handoff"
- 엔진 갱신: [harness/engine/pre-commit-review.md](../../engine/pre-commit-review.md)
- 버전 히스토리: [harness/docs/v1.1.4.md](../../docs/v1.1.4.md)
- 메모리: `memory/project_pre_commit_code_coach.md`
- 직전 리뷰: [harness/logs/code-coach/2026-04-27-1545-precommit-react-actor-guards.md](../code-coach/2026-04-27-1545-precommit-react-actor-guards.md)
