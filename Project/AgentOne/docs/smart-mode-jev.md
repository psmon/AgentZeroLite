# Smart mode — 기술검토: Jev (TypeSafe System One)

> 검토일: 2026-09-22 · 대상: [docs.typesafe.ai](https://docs.typesafe.ai/introduction) (Jev / System One API)
> 상태: **검토 완료, 구현 미착수.** API 키 미보유 → 라이브 호출·지연시간·비용은 **미검증**.
> 목적: agent-one 에 "계획을 먼저 세우고 선택지 중 하나를 자동 결정하는" 스마트 모드를 붙일 수 있는지.

## 한 줄 결론

**맞는 도구다.** 다만 "LLM 을 Jev 로 바꾸는" 것이 아니라, **LLM 이 만든 선택지를 Jev 가 고르는**
2-모델 구조여야 한다. Jev 문서가 이 점을 명시적으로 못박고 있다.

---

## 1. Jev 가 무엇이고, 무엇이 아닌가

Jev 는 TypeSafe 의 **System One 모델** — 텍스트를 생성하지 않고 **구조화된 판단**을 돌려준다.
상태(state)와 타입이 있는 질문(questions)을 보내면 타입이 있는 답과 **확률분포 + confidence** 가 온다.

| 질문 타입 | 묻는 것 | 돌려주는 것 |
|---|---|---|
| `choice` | 이 선택지 중 어느 것? | `choice`, `probabilities`, `confidence` |
| `score` | 어느 등급? | `score`, `legend`, `probabilities`, `confidence` |
| `noul` | 이 진술이 참인가? | `noul` (0~1) |

**문서가 직접 부정하는 것** (`/introduction/coding-agents`):

> Jev is **not** a drop-in replacement for the LLM behind Claude Code, Cursor, opencode, Copilot…
> It does not generate text, write code, or hold a conversation.
> There is no `model: "jev-latest"` setting that turns your coding agent into a Jev-powered agent.

같은 문서가 권하는 용법이 정확히 우리가 하려는 것이다:

> Use Jev **inside an app or agent you're building** — for routing, classification, scoring,
> guardrails, or any structured decision.
> · Route a request to one of a fixed set of destinations, **and know how confident that routing is**.
> · Replace a fragile prompt that asks an LLM to "return JSON" with a call that returns typed values.

즉 **`IChatProvider` 를 대체하는 게 아니라, 루프 안의 판단 지점에 꽂는다.**

## 2. 우리 요구사항과의 대조

| operator 요구 | Jev 적합성 |
|---|---|
| 계획을 먼저 세운다 (플래닝 모드) | Jev 는 계획을 **세우지 못한다.** 계획 생성은 기존 LLM 의 일 |
| 선택지 1~4개가 생긴다 | `choice` 의 `criteria` 가 정확히 "옵션명 → 설명" 맵. 딱 맞음 |
| 자동 결정 | `choice` = 최고확률 옵션. 파싱 불필요 |
| 자신 없을 때? | **`confidence` 가 바로 그 축.** 문서 권장: 높음=자동 실행 / 중간=확인 요청 / 낮음=실행 금지 |
| TUI 에서 별도 키 설정 + 헬스체크 | 별도 엔드포인트·별도 키. `GET /v1/models` 로 헬스체크 |
| chat 에서 on/off, Shift+Tab 전환 | 순수 클라이언트 측 상태. Jev 와 무관 |

**선택지가 1개면 Jev 를 부르지 않는다** — 고를 게 없다. Choice 는 2개 이상일 때만 의미가 있고,
호출 한 번을 아끼는 것이기도 하다. 이건 코드로 강제한다.

## 3. 검증된 API 계약

### 요청

```http
POST https://api.typesafe.ai/v1/systemone
Authorization: Bearer <API_KEY>
Content-Type: application/json
```

```json
{
  "state": "<판단 대상 컨텍스트: 사용자 요청 + 현재까지의 관찰>",
  "model": "jev-latest",
  "questions": {
    "plan": {
      "type": "choice",
      "instructions": "Which approach should the agent take to answer the user?",
      "criteria": {
        "read_local": "The answer is in files in this workspace; read them.",
        "search_web": "The answer needs current or external information; search the web.",
        "answer_now": "Enough is already known; answer without further tools."
      }
    }
  }
}
```

### 응답

```json
{
  "model": "jev-1.13.0",
  "answers": {
    "plan": {
      "type": "choice",
      "choice": "read_local",
      "confidence": 1.0,
      "probabilities": { "read_local": 1.0, "search_web": 0.0, "answer_now": 0.0 }
    }
  },
  "usage": { "input_tokens": 328, "output_tokens": 34 }
}
```

### 그 외 확인된 것

- 질문 여러 개를 **한 번의 호출로 병렬 평가**한다. 질문을 늘려도 응답시간이 거의 안 변한다고 문서가 주장 (미검증).
- 질문 id 는 모델에게 전달되지 않는다 → `instructions` 에 질문을 완전히 써야 한다.
- 환경변수 관례: `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL`.
- 모델 목록 API 존재 (`client.models.list()`) → **헬스체크 경로**로 쓴다. 경로는 `/v1/models` 로 추정, 구현 시 확인 필요.
- `confidence` 는 확률분포의 퍼짐에서 계산된 0~1 값. 분포가 평평할수록 낮다.

## 4. 제안 아키텍처

현재(기본 모드)와 스마트 모드의 차이는 **루프 한 턴의 앞단**에만 있다.

```
기본 모드      prompt ──> LLM ──> envelope ──> tool ──> …
스마트 모드    prompt ──> LLM(계획: 후보 2~4개) ──> Jev(choice+confidence) ──┐
                                                                          ├─> 선택된 접근으로 루프 진행
                                        confidence 낮음 ──> 사용자에게 확인 ┘
```

1. **계획 단계** — 기존 LLM 에게 "이 요청을 처리할 접근을 2~4개, 각각 한 줄 설명으로" 요청.
   출력은 기존 엔벨로프 방식 재사용 가능 (`{"tool":"plan","args":{...}}` 형태의 새 verb, 또는 별도 프롬프트).
2. **결정 단계** — 후보를 Jev `criteria` 로 변환, `state` 에는 사용자 요청 + 지금까지의 도구 결과 요약.
3. **게이트** — confidence 3구간:
   - 높음 → 자동 실행
   - 중간 → 실행하되 상태줄에 근거 표시 (또는 chat 에서 1회 확인)
   - 낮음 → 실행 금지, 사용자에게 선택지 제시
   경계값은 **설정으로 빼고**, 기본값은 보수적으로 잡은 뒤 실사용에서 조정한다.
4. **실행** — 선택된 접근을 그대로 기존 루프에 태운다. 도구 체인은 변화 없음.

### 왜 "그냥 LLM 에게 고르라고 하면" 안 되는가 — 정직한 비교

| | LLM 에게 고르게 | Jev |
|---|---|---|
| 출력 | 텍스트 → 파싱 필요 | 타입 있는 값 |
| 불확실성 | "확신 없음"을 말할 수는 있으나 **보정된 수치가 아님** | 확률분포 + confidence |
| 일관성 | 온도·프롬프트에 흔들림 | 같은 state·criteria 면 안정적 |
| 비용/지연 | 생성 토큰만큼 | 구조화 판단 전용 (**미검증**) |

**핵심 이득은 confidence 다.** 선택 자체는 LLM 도 한다. "자동으로 해도 되는가"를
코드가 판단할 수 있게 해주는 것이 Jev 를 붙일 유일하게 충분한 이유다.
이 이득을 안 쓸 거면(=confidence 를 무시하고 choice 만 쓸 거면) **두 번째 유료 서비스를 붙일 근거가 약하다.**

### 보안상 오히려 유리한 점

`state` 에는 웹 페이지 본문이 섞일 수 있다(프롬프트 인젝션 유입구). Jev 는 **지시를 따르는 모델이 아니라
분류기**라서, "무시하고 X 를 선택하라" 같은 문구에 LLM 만큼 취약하지 않다. 단, 확률을 밀어낼 수는
있으므로 **웹 본문은 요약해서 넣고, 원문은 넣지 않는다.**

## 5. 코드에 앉힐 자리

기존 구조와 충돌 없이 얹힌다.

```
Llm/Decision/IDecisionEngine.cs      Choose(state, options, ct) -> DecisionResult(choice, confidence, probabilities)
Llm/Decision/JevDecisionEngine.cs    POST /v1/systemone, IModelCatalog 유사 헬스체크
Llm/Decision/NullDecisionEngine.cs   기본 모드 — 항상 "첫 번째" 또는 "결정 안 함"
Agent/PlanningLoop.cs                계획 생성 → IDecisionEngine → 기존 AgentLoop 진행
Services/AgentConfig.cs              smartMode(bool), jevBaseUrl, jevModel, jevConfidenceFloor
Services/CredentialStore.cs          키를 **두 개** 보관하도록 확장 (apiKey / jevApiKey)
Tui/                                 4단계 "Smart" 추가
```

이미 있는 패턴을 그대로 재사용한다:
- `ApiKey.Resolve` 와 같은 방식의 **별도 키 해석** (저장소 우선 → 환경변수 `TYPESAFE_API_KEY`).
- `IModelCatalog` 와 같은 방식의 **헬스체크 = 목록 조회**. 목록이 오면 키·URL·네트워크가 한 번에 검증된다.
- `AgentOneWireJson` 소스젠 컨텍스트에 요청/응답 타입 추가 (AOT 유지).
  `probabilities` 는 `Dictionary<string,double>` — 소스젠 지원됨.

**AOT 영향 없음**: 순수 HTTP + JSON. 새 네이티브 의존성 없음.

## 6. TUI 설계 (operator 요구 반영)

4단계를 추가한다. 스택 구조는 그대로:

```
1. Connection  →  2. Model  →  3. Options  →  4. Smart
```

**4단계 Smart:**

```
╭─ agent-one config ──────────────────────────────────────────╮
│ 1. Connection →  2. Model  →  3. Options  → [4. Smart]      │
│                                                             │
│› smartMode       off  ←→                                    │
│  jevApiKey       (none)                                     │
│  jevModel        jev-latest                                 │
│  confidenceFloor 0.60                                       │
╰─────────────────────────────────────────────────────────────╯
 paste the TypeSafe key here — stored in credentials.json, never in config.json
 ✗ no key — smart mode cannot be turned on until the health check passes
 ↑↓ move · Enter edit · ←→ cycle · h health check · b back · s save · q quit
```

- `jevApiKey` 는 기존 `apiKey` 와 **같은 마스킹·같은 저장 방식** (credentials.json).
- **헬스체크가 통과해야 `smartMode` 를 on 으로 돌릴 수 있다** — 2단계의 모델 목록 조회와 같은 철학.
  키가 없거나 실패하면 `←→` 로 on 을 시도해도 거부하고 이유를 상태줄에 쓴다.
- 헬스체크 실패 메시지는 기존 규칙을 따른다: 용의자(URL·키)를 모두 지목.

## 7. chat 모드 전환 (operator 요구 반영)

- **Shift+Tab** 으로 일반 ↔ 스마트 토글. chat 은 지금 키 바인딩이 거의 없어 충돌 없음.
- **하단 상태줄**에 현재 모드 상시 표시. 기존 `ProgressDisplay` 와 같은 stderr 원칙을 따르되,
  chat 은 전체화면이 아니므로 프롬프트 앞에 모드 표시를 붙이는 방식이 단순하다:

```
[basic] > 질문
[smart] > 질문
        … planning (3 approaches)
        ✓ jev: search_web  (confidence 0.82)
        … searching the web for "…"
```

- 키가 없거나 헬스체크 미통과면 Shift+Tab 은 **전환하지 않고** 이유를 한 줄로 말한다.
- 진행 표시는 이미 있으므로, 계획·결정 단계도 같은 `ActivityStarted` 로 흘리면 된다
  (`planning`, `deciding`) — 추가 UI 없이 그대로 보인다.

## 8-A. 실측 결과 (2026-09-22, 유효 키로)

`agent-one jev choose` 로 실제 판단을 던져 얻은 값이다. **임계값은 이 관측에서 나온다.**

### 지연시간

| | 값 |
|---|---|
| 첫 호출 (커넥션 수립 포함) | 768 ms |
| 이후 연속 호출 min / median / max | **245 / 306 / 768 ms** |
| 토큰 | 입력 320~425, 출력 31~50 |

정상 상태는 **0.25~0.3초대**. 계획(LLM) + 결정(Jev) 2회 왕복이 되어도 Jev 쪽은 체감에 큰 부담이 아니다.

### confidence 가 실제로 떨어지는 지점

| 케이스 | choice | confidence |
|---|---|---|
| 명확한 요청 (영어) | read_local 0.94 | **0.91** |
| 명확한 요청 (**한국어 state + 한국어 criteria**) | search_web 1.00 | **0.99** |
| 모호한 요청("이거 어떻게 해?") + `ask_user` 옵션 제공 | ask_user 1.00 | **1.00** |
| state 가 거의 없음 (`"x"`) | answer_now 0.92 | **0.88** |
| **거의 같은 뜻의 옵션 2개** (read_files / inspect_source) | inspect_source 0.69 | **0.37** |
| **state 와 질문이 무관** (날씨 state 에 DB 인덱스 질문) | users_idx 0.45 | **0.19** |

**읽어낸 것 세 가지:**

1. **한국어는 문제없다.** 미검증으로 남겨뒀던 항목인데, 한국어 state·criteria 로 0.99 가 나왔다.
2. **confidence 는 "사용자 입력이 모호할 때"가 아니라 "옵션이 구분되지 않거나 질문이 state 로
   답할 수 없을 때" 떨어진다.** 이게 정확히 옳은 의미다 — 요청이 모호해도 *접근*은 명확할 수 있고
   (`ask_user` 를 1.00 으로 고른 케이스), 그때 막으면 오히려 방해가 된다.
3. **간격이 넓다.** 행동해도 되는 구간 0.88~1.00, 막아야 하는 구간 0.19~0.37. 중간이 비어 있다.
   → **floor 0.6~0.7 이 현재 관측 기준 깨끗하게 갈린다.** 다만 표본이 7건이므로 고정값으로
   박지 말고 설정으로 두고, 스마트 모드 실사용 로그로 재조정한다.

**설계 시사점:** 낮은 confidence 는 "사용자에게 물어라"가 아니라 **"옵션 설계가 잘못됐다"**는
신호일 때가 많다. 계획 단계가 서로 구분되는 옵션을 내놓도록 프롬프트를 잡는 것이,
임계값을 만지는 것보다 먼저다.

## 8. 미검증 항목 (구현 착수 시 가장 먼저 확인할 것)

1. ~~지연시간~~ — **실측 완료** (8-A). 연속 호출 median 306 ms.
2. **비용** — 요금 정보가 문서에 없음. 관측된 사용량은 호출당 입력 320~425 / 출력 31~50 토큰.
3. ~~`/v1/models` 경로~~ — **확인됨**. 키 없이 때려본 결과 `GET https://api.typesafe.ai/v1/models` → 401 (404 가 아님). 엔드포인트는 존재한다.
4. **`criteria` 개수 상한** — 2·3·4개 모두 정상 동작 확인. 그 이상은 미확인.
5. ~~한국어 state/criteria 품질~~ — **실측 완료** (8-A). confidence 0.99.
6. ~~confidence 실제 분포~~ — **실측 완료** (8-A). 0.19 ~ 1.00, 중간이 빈 이봉 분포.

### 키 없이 확인한 것 (2026-09-22)

가짜 키로 실제 엔드포인트를 호출해 **인증 이전 단계**를 검증했다:

```
POST https://api.typesafe.ai/v1/systemone  ->  HTTP 401 in 0.70s
GET  https://api.typesafe.ai/v1/models     ->  HTTP 401
```

401 이지 404·400 이 아니라는 것은 **URL·요청 본문 형태·인증 헤더가 모두 받아들여졌고 키만 거부됐다**는
뜻이다. 즉 구현한 `JevClient` 의 요청은 이미 옳다. 남은 것은 유효한 키로 **200 응답의 실제 모양**과
**진짜 지연시간**을 보는 것뿐이다 (위 0.70s 는 인증 거부까지의 시간이라 판단 시간이 아니다).

## 8-B. 3단계 구현 후 관측 (2026-09-22)

실제로 붙이고 돌려본 결과, **검토 시점에 예상하지 못한 것 두 가지**가 나왔다.

### ① 불확실한 계획으로 루프를 밀면 더 나빠진다

"이 저장소의 도구가 몇 개냐"를 물었을 때:

| 모드 | 결과 |
|---|---|
| 기본 | 1스텝에 정답 (시스템 프롬프트에 이미 도구 목록이 있음) |
| 스마트 (초판) | confidence 0.51 로 `search_manifests` 선택 → 디렉토리를 헤매다 **스텝 예산 소진** |

원인 둘: 플래너가 "에이전트가 이미 아는 것"을 몰라서 탐색을 제안했고, 코드가
**불확실한데도 그 방향으로 몰았다.**

고친 것:
- `run` 은 **confident 일 때만 steering 한다.** 미달이면 말만 하고 루프를 건드리지 않는다.
- 플래너 프롬프트에 *"이미 아는 것으로 답할 수 있으면 `answer_now` 를 내놓아라"* 추가.

고친 뒤 같은 질문 → 플래너가 접근 **1개**만 내놓음 → 결정 불필요 → 1스텝에 정답.

### ② 플래너가 만든 옵션은 손으로 쓴 옵션보다 덜 분리된다

8-A 의 손으로 쓴 옵션은 confidence 0.88~1.00 이었는데, 플래너가 만든 옵션은
**0.40 ~ 0.68** 에 몰린다. 표본은 작지만 방향은 분명하다 — LLM 이 내놓는 접근들은
서로 겹치는 경향이 있다.

**시사점:** floor 0.60 은 "대부분의 턴에서 steering 하지 않음" 을 뜻하게 된다.
이건 안전한 기본값이지만, 스마트 모드의 가치를 떨어뜨린다. 개선 방향은 임계값을
낮추는 게 아니라 **옵션 분리도를 올리는 것** — state 를 요청만이 아니라 "지금까지
관찰한 것" 까지 포함하도록 넓히고, 플래너 프롬프트를 더 조이는 것.

### ③ 승인 옵션은 의도대로 작동한다

`needs_review` 를 **코드가 항상 마지막에 붙인다** (플래너에 맡기지 않음 — 잊거나
문구가 매번 달라진다). "이 저장소의 파일들을 전부 정리해줘" 에서:

```
⚠ this needs you (confidence 0.68)
  analyze_local_structure  (0.22)
  generate_documentation   (0.00)
  identify_refactoring_tasks (0.01)
  answer, approve or redirect on the next line
```

"정리" 가 요약인지 삭제인지 모호한 요청 — 정확히 이 옵션이 있어야 할 자리다.
`run` 은 exit 3 으로 **아무것도 실행하지 않고** 멈추고, `chat` 은 턴을 세워두고
다음 줄을 답으로 받아 이어간다.

## 8-C. 채팅 로그 분석과 후속 조치 (2026-09-22, 세 번째 실행)

`20260922-183214-chat.jsonl` (안녕 → 기본 MSA 질문 → 스마트 "msa 도입전략 제안"):

| 턴 | 모드 | 관측 |
|---|---|---|
| 1 | 기본 | "안녕" → final 3.0s |
| 2 | 기본 | web_search 3.9s → web_read 7.5s → final 29s. nudge 없음, 깨끗함 |
| 3 | 스마트 | 계획 **~15s** (플래너 LLM ~14s + Jev 0.6s) → `search_best_practices` 0.90 → 모델은 도구 없이 29s 만에 직접 답변 |

턴 3 이 문제다. 플래너는 **대화에 이미 들어와 있는 자료**(턴 2 에서 읽은 페이지)를
몰라서 "검색하라"를 제안했고, Jev 는 그 제안에 0.90 을 줬고, 정작 모델은 그 조언을
무시하고 문맥에서 답했다. 스마트 모드가 15초를 더 쓰고 얻은 것이 없다.

### 고친 것

- **플래너와 엔진 둘 다 대화 요약을 받는다.** `ChatSession.Digest()` 가 지금까지의
  도구 결과(`[tool:web_read] 제목…` 첫 줄)와 이전 질문들을 최근 8줄로 추려
  `Planner.PlanAsync(request, toolScope, context)` 와 Jev 의 `state` 에 함께 넣는다.
  플래너 프롬프트에는 "이미 대화에 있는 것으로 답할 수 있으면 `answer_now`" 를 명시.
- **계획 시간을 잰다.** `SmartPlan.PlanningMs` — 로그의 `plan` 항목 `elapsedMs` 는
  이제 플래너 + 결정의 합, 즉 그 턴에서 스마트 모드가 쓴 전체 비용.

### 고친 뒤 실측 (`chat --plain --smart`, gemma-4-e4b)

| 턴 | 계획+결정 | 결과 |
|---|---|---|
| "MSA 핵심 5줄" | 14.7s | `answer_now` **0.94** → 8.2s 에 직접 답변 (도구 없음) |
| "그럼 우리 팀 도입 전략" | 12.6s | unsure **0.31** (search_web 0.27 / read_local 0.54) → 사람에게 멈춤 |

두 가지가 보인다.

1. **`answer_now` 가 살아났다** — 8-B ① 에서 넣은 규칙이 첫 턴부터 작동하고, Jev 도
   그것을 0.94 로 확신한다. 플래너가 "가진 것으로 답하라"를 선택지로 내놓기만 하면
   Jev 는 잘 고른다.
2. **후속 턴의 옵션은 여전히 갈리지 않는다** (0.31). 요약을 넣어도 "웹을 찾을까,
   로컬을 볼까"는 둘 다 그럴듯하고, 그건 사실 사람이 정할 문제다 — 멈춘 것이 맞다.
   8-B ② 의 관찰("플래너 옵션은 손으로 쓴 것보다 덜 분리된다")은 그대로다.

**남는 비용은 플래너 지연 12~15초**로, 이 엔드포인트의 모델이 계획 JSON 하나에 그만큼
쓴다. Jev 자체는 0.3~0.6s. 다음 손댈 곳은 여기다: 플래너 호출에 `max_tokens` 를
걸거나(옵션 설명이 길어지는 걸 막음), 계획 전용으로 더 작은 모델을 지정할 수 있게
하는 것(`jevModel` 처럼 `plannerModel`). 스마트 모드의 가치는 결정의 질에 있고, 그
결정은 이미 빠르다 — 느린 것은 선택지를 만드는 쪽이다.

### 같이 고친 창 문제

같은 실행에서 채팅 창이 긴 답을 보여주지 못했다. 원인 둘:
`StreamingTextNode.ScrollUp` 의 둘째 인자는 **줄바꿈 폭**인데 높이를 넘겼고, 텍스트가
붙을 때마다 `ScrollToBottom()` 을 불러 읽는 중에도 끌어내렸다. 지금은 버퍼의
`AutoScroll` 이 끝에 있을 때만 따라가고, PageUp/PageDown/Ctrl+End 와 스크롤바가 있다.
그 과정에서 하나 더 — 2,300자짜리 한 줄이 창을 9초 얼렸다(`StreamingTextNode.Render`
가 셀마다 줄 전체를 다시 잰다, 스택 덤프로 확인). `Tui/SoftWrap` 이 창 폭으로 미리
접어서 넣는다.

## 8-D. 패턴 교체: 플래너 없이, 고정 선택지 두 번 (2026-09-22)

8-B/8-C 의 결론은 하나로 모였다 — **느린 건 Jev 가 아니라 선택지를 만드는 LLM 이고,
LLM 이 만든 선택지는 잘 갈리지도 않는다.** 그래서 플래너와 `needs_review` 패턴을 걷어내고,
Jev 에게 **고정 선택지**로 두 번 묻는 패턴으로 바꿨다 (`Agent/SmartRouter`).

```
요청 (10자 이상)
   ├─ ① 라우팅  search_web · read_workspace · answer_directly
   │            → 루프는 그 패밀리만 쓸 수 있다 (거부, 권고가 아님)
   └─ 기본 모델이 도구 결과로 초안을 쓴다
        └─ ② 에스컬레이션  keep_draft · escalate
             escalate → 추론 모델이 같은 자료로 생각 → 기본 모델이 최종 답을 씀
```

설계 포인트:

- **10자 미만은 Jev 를 부르지 않는다.** "안녕", "네" 에 라우팅은 낭비다.
- **라우팅은 강제한다.** 작은 모델은 프롬프트의 권고를 "여러 선택지 중 하나"로 읽는다.
  `AgentLoop.RunAsync(…, families)` 가 패밀리 밖 호출을 실행하지 않고 거부 메시지를 돌려준다.
- **Jev 는 항상 모델 이름을 안다.** "이 초안이 충분한가"는 4B 가 쓴 것인지 27B 가 쓴 것인지에
  따라 답이 다르다. state 에 두 모델과 그 특성(작고 빠름 / 강하고 느림)을 매번 넣는다.
- **에스컬레이션 판단은 도구 결과 뒤에 한다.** 자료 없이 초안만 보고 판단하면 추측이다.
  state = 요청 + 도구 결과(6000자까지) + 초안(4000자까지) + 모델 정보.
- **추론 모델의 답은 사용자에게 직접 가지 않는다.** `[reasoning:<model>]` 로 기본 모델의 대화에
  들어가고, 기본 모델이 최종 답을 쓴다 — 같은 톤, 같은 언어, 빌려온 생각.
- 추론 모델이 없거나(설정 비움) 죽어 있으면 초안이 그대로 답이다. 업그레이드지 의존성이 아니다.

### 실측 (gemma-4-e4b → qwen3.8-27b)

"MSA 전환 시 데이터 일관성 보장, 설계 관점 분석":

| 단계 | 시간 | 결과 |
|---|---|---|
| ① 라우팅 | 0.65s | `answer_directly` **0.97** — 도구 없이 답하라 |
| 초안 | 34s | 3,036자 (Saga, Outbox, CQRS 표까지) |
| ② 에스컬레이션 | 0.24s | `keep_draft` 0.60 — 초안 유지 |

스마트 모드의 오버헤드가 **15초 → 0.9초**. 이 질문은 4B 로도 충분하다고 Jev 가 봤고, 답을 보면
동의할 만하다.

두 번째 질문 — 리틀의 법칙으로 서버 수를 단계적으로 계산하고 p99 여유율까지 제안하라 —
에서는 Jev 가 `escalate` 를 골랐는데 **confidence 0.25** 였다. 선택지 둘짜리 판단 문제는
0.60 을 거의 못 넘는다(8-A 의 "구분 안 되는 옵션은 0.19~0.37" 과 같은 현상). 그래서
**에스컬레이션은 floor 를 보지 않고 choice 만 본다**: floor 는 steering 을 지키는 장치인데
(약한 판단으로 루프를 밀면 예산을 태운다) 에스컬레이션은 틀릴 위험이 아니라 시간의 비용이고,
Jev 의 choice 는 이미 "그럴 가능성이 더 높다"는 뜻이다. 그 기준이 느린 두 번째 검토에 맞다.

부수 발견 하나: gemma 가 `final` 봉투 안 문자열에 **줄바꿈을 그대로** 넣어서 JSON 파서가 실패하고,
그 원문(중괄호 포함)이 "산문 답변"으로 채택돼 **답이 두 번**(스트림으로 한 번, 원문으로 또 한 번)
찍혔다. `AgentLoop.TryDecodeBrokenFinal` — final 모양이면 스트림용 관대한 디코더로 `text` 를 꺼낸다.
깨진 도구 호출은 여전히 nudge 를 받는다.

## 9. 단계별 제안

| 단계 | 내용 | 가치 |
|---|---|---|
| ~~**0**~~ | ~~왕복 1회 + 지연시간 측정~~ | **완료** — 8-A |
| ~~**1**~~ | ~~`IDecisionEngine` + 엔진 + CLI 검증 명령~~ | **완료** — `agent-one jev choose` |
| ~~**2**~~ | ~~TUI 4단계 + 헬스체크 + 키 저장~~ | **완료** (2026-09-22) — operator 가 TUI 로 키를 넣으면 그 값을 구현이 읽는다 |
| ~~**3**~~ | ~~계획 + 결정 게이트 + Shift+Tab~~ | **완료** — 8-B |
| **4** | **옵션 분리도 개선** (state 확장 + 플래너 프롬프트) 후 경계값 재조정 | 8-B ② 가 남긴 과제 |

1~2단계까지만 해도 **기본 모드는 전혀 영향을 받지 않는다** — 스마트 모드는 순수 추가다.

## 10. 곁가지로 쓸 만한 곳 (지금은 범위 밖)

Jev 를 한 번 붙이면 `noul`·`score` 로 값싸게 얻을 수 있는 판단들:

- "이 웹 페이지가 질문에 실제로 답하고 있는가" (`noul`) → 더 읽을지 말지
- "지금까지 모은 정보로 답할 수 있는가" (`noul`) → 루프 조기 종료, 토큰 절약
- "이 도구 결과가 관련 있는가" (`score`) → 컨텍스트에 넣을지 판단

전부 confidence-gated routing 패턴이다. 다만 **호출이 늘면 비용도 는다** — 1~4단계가 자리잡은 뒤
실측으로 판단할 것.
