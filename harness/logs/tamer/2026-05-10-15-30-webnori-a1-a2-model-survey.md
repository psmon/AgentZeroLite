---
date: 2026-05-10T15:30:00+09:00
agent: tamer
type: review
mode: log-eval
trigger: "AgentZero 무료 모델제공 웹노리 a1/a2 시리즈 모델 조사"
---

# Webnori a1 / a2 라이브 모델 카탈로그 조사

## 실행 요약

`https://a1.webnori.com` 과 `https://a2.webnori.com` 두 무료 컨트리뷰터 게이트웨이가
실제로 어떤 모델을 서빙하고 있는지 OpenAI 호환 `/v1/models` 로 직접 조회.

- 1차 조사 (15:30) — a1 6개, a2 차단
- 2차 조사 (사용자 *"모델 재정렬함"* 통지 후) — a1/a2 모두 응답, **카탈로그가 크게 바뀜**

## 결과

### a1.webnori.com — 라이브 카탈로그 (재정렬 후 3 entries)

| id | role | 비고 |
|---|---|---|
| `google/gemma-4-e4b` | chat | 프로젝트 기본 (`WebnoriDefaults.DefaultModel`) — 유지 |
| `openai/gpt-oss-20b` | chat | OpenAI 오픈웨이트 20B — 유지 |
| `text-embedding-nomic-embed-text-v1.5` | embedding | 유지 |

**a1에서 빠진 항목 (1차 조사 대비)**: `gemma-4-e4b-it-mlx`, `qwen/qwen3-vl-8b`,
`text-embedding-all-minilm-l12-v2`.

### a2.webnori.com — 라이브 카탈로그 (재정렬 후 2 entries)

| id | role | 비고 |
|---|---|---|
| `qwen/qwen3.6-27b` | chat | 신규 — 27B 대형 모델 |
| `nvidia/nemotron-3-nano-4b` | chat | 신규 — 4B 경량 |

코드베이스가 알고 있는 `WebnoriDefaults.KnownModels` 의 `gemma-4-26b-a4b` 는
**a1, a2 어디에도 없음** — 완전히 카탈로그에서 사라짐.

### 재정렬의 의도 추정

| 호스트 | 성격 | 모델군 |
|---|---|---|
| **a1** | 프로덕션 — 안정적인 워크호스 | gemma-4-e4b (9B chat) + gpt-oss-20b (20B chat) + nomic embedding |
| **a2** | 실험 — 신형 대형/소형 비교군 | qwen3.6-27b (27B 대형) + nemotron-3-nano-4b (4B 경량) |

a1 은 "기본 사용 + 임베딩", a2 는 "비교/실험용 신모델" 로 역할 분리된 것으로 보인다.
agent-origin 문서가 a2를 *기본*으로 적시한 시점(2026-04-27 스냅샷)과는 정반대 — 운용 중 역할이 뒤집힌 셈.

### 코드베이스가 본 a2의 위치 (스냅샷 2026-04-27)

- `Docs/agent-origin/01-stack-comparison.md:380` — *"**Webnori** (`a2.webnori.com`) — Lite 단독, 기본 백엔드"*
- `Docs/agent-origin/02-architecture-comparison.md:197` — *"Webnori (a2.webnori.com) ← 기본"*

당시 a2가 기본이었음. 현재 런타임 코드 (`Project/ZeroCommon/Llm/Providers/LlmProviderFactory.cs:25`)
의 `WebnoriDefaults.BaseUrl` 은 **`https://a1.webnori.com`** 이므로 기본은 a1으로 이동했고
a2는 보조/레거시 엔드포인트로 보인다. agent-origin 문서가 현재 상태와 어긋남 (오리진 스냅샷이라 의도된 차이일 수도).

## 평가

### 워크플로우 개선도: B
- 라이브 조회로 카탈로그 실측치를 얻었지만 a2는 미해결.
- 사용자에게 `! curl` 안내로 협업 보완 가능.

### Claude 스킬 활용도: 3 / 5
- harness-kakashi-creator 진입 → tamer 로그 기록까지는 정상 동작.
- 단 외부 자격증명 정책 때문에 절반은 자체 수행 불가 — 샌드박스 정책 vs 사용자 명시 권한 충돌 케이스.

### 하네스 성숙도: L3 (목표 대비 정체)
- knowledge/_shared 에 Webnori 운영 정보가 없어 매번 코드 grep 으로 단서를 모아야 함.
- 본 조사 결과를 `harness/knowledge/_shared/webnori-providers.md` 로 승격하면 다음 조사가 빨라진다.

## 다음 단계 제안

1. **카탈로그 드리프트 동기화** — `WebnoriDefaults.KnownModels` (`LlmProviderFactory.cs:31-35`)
   현재 `[gemma-4-e4b, gemma-4-26b-a4b]`. 라이브 진실은:
   - a1: `gemma-4-e4b`, `gpt-oss-20b`, `nomic-embed-text-v1.5`
   - a2: `qwen3.6-27b`, `nemotron-3-nano-4b`
   `26b-a4b` 는 더 이상 어디에도 없음. 두 호스트를 분리해 카탈로그를 표현하려면
   `WebnoriA1Defaults` / `WebnoriA2Defaults` 로 나누거나, 하나의 카탈로그에 host 메타를 추가하는 패턴이 필요.

2. **`Home/harness-view/data/models.json` 업데이트** — `Webnori Gemma` 카드 variant
   `"google/gemma-4-e4b · gemma-4-26b-a4b"` → 현재 카탈로그에 맞게 재작성.
   가능하면 카드를 둘로 분리: *Webnori a1 (안정)* / *Webnori a2 (실험 — qwen3.6-27b, nemotron-3-nano-4b)*.

3. **a2 역할 명문화** — 라이브 결과로 의도가 분명해짐: a2 = 신형/대형 실험 호스트.
   - agent-origin 문서가 a2를 *기본* 으로 적은 부분은 옛 스냅샷 사실로 그대로 두되,
     현재 상태 (a1 = 기본) 를 별도 표기.
   - README / 모델 카드에 *"a1 = 워크호스(gemma-4-e4b + gpt-oss-20b + 임베딩),
     a2 = 신모델 비교군(qwen3.6-27b, nemotron-3-nano-4b)"* 한 줄을 추가.

4. **knowledge/_shared/webnori-providers.md 신설** — 호스트별 카탈로그, 엔드포인트
   특이사항 (예: `WebnoriGemmaStt.cs` 의 *"audio input은 서버측이 거부함"*,
   API 키 회전 정책, 두 호스트의 역할 분담) 을 한 곳에 모아 다음 조사를 0 에 가깝게.

5. **카탈로그 변동 감시 자동화 후보** — 카탈로그가 자주 바뀌므로 (오늘 하루에도 1차→2차 사이에
   재정렬), `WebnoriExternalSmokeTests` 에 *"카탈로그 스냅샷이 baseline 과 다르면 경고"* 같은
   비치명 테스트를 추가하면 드리프트를 조기에 잡을 수 있다.
