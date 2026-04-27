---
date: 2026-04-28T09:30:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "워크플로우에서도 유닛테스트시 유의사항 반영"
---

# dotnet test 실행 규칙을 하네스 정전(canon)으로 승격

## 실행 요약

같은 날 phase 2 (Voice 서브시스템) 빌드 검증 중 발생한 **testhost orphan 12 GB
사고**의 교훈을 운영자(Claude) 개인 메모리에서만 보존하지 않고, 하네스 정전으로
승격해 모든 에이전트/엔진이 강제 참조하도록 만들었다.

직전에 동일 사고가 사용자 시스템에 실질적 부하(IDE/빌드 영향, 사용자 중단 요청)를
일으켰기에 **두 번째 사고를 막는 것**이 이번 변경의 명확한 동기다.

## 변경 내역

### Layer 1 (knowledge) — 신규

- `harness/knowledge/dotnet-test-execution.md` 신규 작성
  - 사례 보고: 2026-04-28 testhost 12 GB 사건 (5.3+3.4+3.4 GB / 21분 런타임)
  - 핵심 규칙 R1–R6:
    - R1: 같은 프로젝트 대상 병렬 호출 금지
    - R2: 출력 0바이트 ≠ 실패 (재호출 전 진단)
    - R3: testhost 잔존 정리 절차 (taskkill 다중 PID)
    - R4: 디버깅 사이클은 `--filter`로 좁히기
    - R5: WPF-deps 테스트는 desktop 세션에서만
    - R6: 보고 직전 `tasklist | grep testhost` 정리 확인
  - 어겼을 때 블래스트 반경 명시 + 호출측 체크리스트

### Layer 2 (agents) — 갱신

- `harness/agents/test-sentinel.md`
  - Procedure 상단에 canon 배너 추가 — 이 에이전트가 정전의 **owner**
  - Steps 1/2/6에 구체 규칙 인용(R1/R2/R4/R5/R6)
  - 새 평가축 추가: **Test-execution hygiene** (Pass/Fail)

- `harness/agents/build-doctor.md`
  - Procedure 상단에 canon 배너 추가 — 정전을 **inherit**
  - Steps 2/3/6에 규칙 인용(R1/R3/R5/R6)
  - 새 평가축 추가: **Test-execution hygiene** (Pass/Fail)

### Layer 3 (engine) — 갱신

- `harness/engine/release-build-pipeline.md`
  - Step 2 (build-doctor) 본문에 canon 인용을 한 줄 추가 — 미래 독자가 엔진 →
    에이전트 → 정전까지 한 호흡에 추적 가능

### 메타

- `harness.config.json`: 1.1.4 → **1.1.5** (patch — 정전 신설 + 절차 보강)
- `harness/docs/v1.1.5.md`: 변경 기록 + 검증 체크리스트 + 롤백 절차

## 결과

5개 파일 갱신, 1개 신규 — 모두 운영 규칙을 1차 매핑(knowledge)에 두고 호출측은
인용으로만 강제하는 DRY 구조. 두 에이전트가 동일 정전을 가리키므로 향후 룰 수정
시 한 곳만 고치면 된다.

빌드/테스트 회귀 가능성 없음(문서 + 절차 문서만 변경). EF migration 영향 없음.

## 평가 (정원지기 3축)

| 축 | 결과 | 근거 |
|----|------|------|
| 워크플로우 개선도 | **A** | 사고가 재발하면 즉시 procedure 평가에서 잡히는 구조. 이전엔 운영자 사적 메모리에만 있었다. |
| Claude 스킬 활용도 | **3/5** | 이번 작업은 하네스 내부 정전 강화로, 외부 Claude 스킬 연동(/skill-creator, /code-coach 등)은 사용하지 않았다. 다만 향후 build-doctor / test-sentinel 호출 시 정전 인용이 그대로 활용된다. |
| 하네스 성숙도 | **L4** | knowledge → agents → engine 3계층이 한 룰을 관통하도록 설계됐고 평가축까지 연결됨. L5(자가 진화)까지는 1단계 더 필요 — 정전 위반 자체를 자동 검출하는 메타 에이전트가 있다면 L5. |

## 다음 단계 제안

- **메타 에이전트 검토(P3)**: `tasklist | grep testhost`를 build-doctor procedure가
  스스로 실행하도록 했지만, 외부에서 검출하는 watcher는 아직 없다. 사고 빈도가
  낮으므로 당장은 불필요하지만 두 번째 사고가 발생하면 그 시점에 검토.
- **`harness/knowledge/` 인덱스 보강**: knowledge 디렉토리에 누적된 문서가 점차
  많아지고 있다. README 인덱스를 추가해 정전 vs 사례기록을 구분하면 미래 에이전트
  검색이 빨라진다 — 다음 patch에서 검토.
- **사적 메모리 ↔ 정전 동기화**: `memory/feedback_dotnet_test_serialization.md`
  (운영자 사적)와 이번 정전이 중복된다. 두 문서가 서로를 강화하는 관계이므로
  삭제하지 않는다 — 단, 정전 변경 시 사적 메모리도 같이 업데이트해야 한다는
  점을 다음 운영자에게 인계.
