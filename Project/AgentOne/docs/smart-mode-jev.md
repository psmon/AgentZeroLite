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

### 에스컬레이션 전 구간 실측 (세 상자 논리 퍼즐)

"라벨 셋 중 정확히 하나만 참, 상금은 어디 있나 — 단계별로 논증":

| 단계 | 시간 | 결과 |
|---|---|---|
| ① 라우팅 | 0.6s | `answer_directly` 1.00 |
| 초안 (gemma-4-e4b) | 31s | **180자** — 전제를 다시 읊고 끝 (얕음) |
| ② 에스컬레이션 | 0.26s | `escalate` **0.85** |
| 추론 (qwen3.8-27b, 스트리밍) | 36s | 958자 — 세 경우 모두 따져 B 도출 |
| 최종 (gemma, 추론 결과로) | 18s | "상금은 **B**" + 단계별 논증 — **정답** |

전 구간 86초. 앞선 질문에서 90초 만에 504 를 냈던 a2 게이트웨이가 스트리밍으로는 36초짜리 응답을
그대로 넘겼다 — 바이트가 흐르면 프록시가 끊지 않는다. 얕은 초안(180자)에는 Jev 가 0.85 로
escalate 를 골랐고, 2,600자짜리 그럴듯한 초안에는 0.10~0.25 에서 갈렸다. 즉 이 질문의 분리도는
**초안이 얼마나 뻔히 부족한가**에 달려 있고, 그게 우리가 원하는 신호다.

부수 발견 하나: gemma 가 `final` 봉투 안 문자열에 **줄바꿈을 그대로** 넣어서 JSON 파서가 실패하고,
그 원문(중괄호 포함)이 "산문 답변"으로 채택돼 **답이 두 번**(스트림으로 한 번, 원문으로 또 한 번)
찍혔다. `AgentLoop.TryDecodeBrokenFinal` — final 모양이면 스트림용 관대한 디코더로 `text` 를 꺼낸다.
깨진 도구 호출은 여전히 nudge 를 받는다.

## 8-E. 손이 생긴 에이전트: 파일 생성·명령 실행과 Jev 의 두 질문 추가 (2026-09-22)

`write_file` 과 `run_command` 가 들어오면서 Jev 의 질문이 둘에서 넷이 됐다. 모두 고정 선택지, 모두 0.3초.

| 질문 | 시점 | 선택지 | 어떻게 쓰나 |
|---|---|---|---|
| ① 라우팅 | 루프 전 | `search_web` · `work_in_workspace` · `answer_directly` | floor 이상이면 그 패밀리만 허용 (workspace = files+edit+exec) |
| ② 규모 | workspace 작업일 때 | `small_task` · `needs_design` | choice 만 봄. `needs_design` → 추론 모델이 설계 → `[design:…]` 로 기본 모델이 구현 |
| ③ 안전 | `run_command` 직전 | `safe` · `unsafe` | **허용 쪽에 floor**: `safe` 이고 확신할 때만 묻지 않고 실행. 나머지는 사람에게 |
| ④ 에스컬레이션 | 초안 뒤 | `keep_draft` · `escalate` | choice 만 봄 (8-D) |

③ 앞에는 Jev 가 뒤집을 수 없는 바닥이 있다 — `Agent/CommandRisk` 의 패턴 (`rm -rf /`, `sudo`, `format`,
파이프 인스톨러, force-push, 레지스트리, 예약작업…) 은 무조건 사람에게 묻는다. Jev 가 아무리
`safe` 라 해도 `rm -rf /` 는 판단 문제가 아니다.

### 실측 (hello.py 만들고 실행)

| 단계 | 결과 |
|---|---|
| ① 라우팅 | `work_in_workspace` 0.85 |
| ② 규모 | `small_task` **1.00** |
| write_file | hello.py 생성 (0.0s) |
| ③ 안전 (`python hello.py`) | `safe` **0.37** → floor 미달 → **사람에게 물음** → y → 실행 1.2s |
| ④ 에스컬레이션 | `keep_draft` 1.00 |

세션 상태창: Jev 4회 1,345ms, 도구 2회, 승인 1/1, 문맥 ~948 토큰.

### 실사용에서 잡힌 것: "빌드가 되는지 수행해봐" 가 설계로 갔다

세션 `20260922-221944`: 게시판 API 를 만들다가(csproj 만 만들고 Program.cs 없이 빌드 → CS5001 → `dotnet run`
반복 → Repeat 정지) 사용자가 **"빌드가 되는지 수행해봐"** 를 넣자 라우팅 `workspace` 0.99 → 규모
**`needs_design` 0.55** → qwen 이 설계를 시작했다. 원인 둘:

1. 규모 질문에 **floor 를 안 걸었다** (에스컬레이션과 같은 "choice 만" 규칙). 그런데 설계는 턴의 성격을
   바꾸는 **steer** 다 — 기본 모델이 계획을 받아 만들기 시작한다. 라우팅처럼 floor 를 걸어야 맞다. 걸었다.
2. state 에 넣은 프로젝트 요약(반쯤 만들어진 프로젝트, 빌드 실패)이 "큰 일"로 읽혔다. 선택지 문구를
   "**요청 자체**의 크기" 로 고쳤고 — `small_task` 에 "빌드/테스트/명령 실행, 되는지 확인, 에러 하나 고치기"
   를 명시 — state 끝에 "지금 요청이 무엇을 시키는지로 판단하라, 프로젝트 상태로 판단하지 말라" 를 붙였다.

같은 세션에서 하나 더: "게시판 api 만들어줘" 는 라우팅이 `answer_directly` **0.58** (unsure) 로 나와
규모 질문이 아예 안 물어졌다 — 정작 설계가 필요했던 요청에 설계가 없었고, 4B 모델은 Program.cs 없는
csproj 를 만들었다. unsure 라우팅일 때도 규모 질문은 묻도록 되어 있으니(Web/Answer 확정만 제외)
이제는 물어진다; 라우팅 문구가 "만들어줘" 를 workspace 로 더 잘 보내는지는 다음 실행에서 본다.

### 다섯 번째 질문: 작업이 바뀌었나 (제목용) — Jev 를 쓸지 판단한 결과

세션에 "지금 하는 작업" 제목을 붙이기로 했다. 제목은 텍스트라 Jev 가 만들 수 없다(choice/score/noul 뿐).
그렇다고 매 턴 LLM 에게 이름을 다시 지어 달라면 턴마다 4B 호출 하나(3~8초)가 더 든다. 그래서
**Jev 는 스위치, LLM 은 작명**: 제목이 있으면 `same_task` / `new_task` 를 Jev 에 묻고(0.3초, choice 만 봄 —
제목이 낡는 건 미관 비용), `new_task` 일 때만 LLM 이 새 이름을 짓는다. 제목이 없으면(첫 턴) 바로 짓는다.
둘 다 **턴이 시작될 때 백그라운드**에서 돈다 — 답변으로 가는 길에 서지 않고, 긴 빌드 턴 중에도 몇 초
안에 헤더가 바뀐다. Jev 키가 없으면 세션당 한 번만 짓는다.

실사용에서 바로 잡힌 것: "안녕" 에 **"상담 시작 및 문의 응대"** 라는 제목이 붙었고, 이어진 "보드API
개발해줘" 는 턴이 끝날 때까지 제목이 안 바뀌었다. 인사는 작업이 아니다 — 라우팅과 같은 10자 문턱
(`SmartRouter.Applies`)을 넘는 요청만 이름을 짓고, 짓는 시점을 턴 끝에서 턴 시작으로 옮겼다.

③ 이 0.37 인 것이 눈에 띈다. `python hello.py` 는 누가 봐도 안전한데 두 선택지짜리 판단이라 확신이
낮다(8-D 와 같은 현상). 허용 쪽에 floor 를 두는 설계라 결과는 "묻기" — 보수적이고 맞는 방향이지만,
매번 물으면 피곤하다. 조정 손잡이는 둘: `jevConfidenceFloor` 를 낮추거나(라우팅에도 같이 걸림),
안전 질문에 별도 floor 를 두는 것. 몇 번 더 써 보고 결정한다 — 지금은 안전한 쪽으로.

## 8-G. 게시판 API 세션에서 나온 것 넷 (2026-09-22, 세션 `232126`)

| 관측 | 원인 | 조치 |
|---|---|---|
| `write_file` 호출(2,564자)이 **답변으로 출력**되고 파일은 안 써짐 — 3회 | gemma 가 JSON 문자열 안에 진짜 줄바꿈·`\.` 를 씀 → 파서 실패 → "도구 스텝 뒤의 산문은 답" 규칙이 봉투를 답으로 채택 | `ToolCall.Repair`: 문자열 안의 제어문자·미지의 이스케이프를 고쳐 재파싱. 그래도 안 되면 nudge — **봉투 모양은 절대 답이 아님** |
| 설계 **530초**(8.8분, 5,687자) | qwen3.8-27b thinking | 설계 첫 줄들을 화면에 바로 보여줌(`DesignMade`), 선택지가 있으면 먼저 물음 — 기다린 보람이 보이게 |
| 8스텝에 `MaxSteps` — 파일 4개 쓰고 빌드 전에 멈춤, 화면엔 `[stopped: MaxSteps]` 만 | 기본값 8 | 기본 **50**. 예산 소진 시 도구 없는 마무리 호출 1회: 한 것 / 남은 것 / 다음 단계 |
| 턴이 끝나도 "뭘 했고 다음은 뭔지" 없음 | 프롬프트에 규칙 없음 | 시스템 프롬프트: 작업을 마친 final 은 한 것·남은 것·다음 1~3단계로 끝난다 |

선택지 제안: 설계 프롬프트가 "사용자만 정할 수 있는 선택(프레임워크·저장소·구조)이 있으면 첫 줄에
`DECISION NEEDED:` + 번호 목록, 추천에 `(recommended)`" 를 요구한다. 코드가 그 블록을 떼어 사람에게
묻고(REPL 은 줄 입력, 창은 입력줄, `run` 은 추천 채택) 선택을 `[design:…]` 뒤에 "The user decided: …" 로
붙인다. Jev 는 여기 끼지 않는다 — 선택지는 텍스트라 Jev 가 만들 수 없고, 고르는 건 사람 몫이다.

## 8-H. 장기 메모리를 그래프로: Jev 가 "남길 가치"와 "찾아볼 가치"를 판단한다 (2026-09-23)

메모리 파일은 "무슨 일이 있었나"를 적는다. 그래프(Kùzu 임베디드, `akka-graph-loop` 의 PDSA 그래프
메모리에서 채택)는 **남길 가치가 있다고 판단된 것**만 담고, 쓸수록 좋아진다.

| 질문 | 시점 | 선택지 | 규칙 |
|---|---|---|---|
| ⑥ 남길 가치 | 턴 뒤 (백그라운드) | `save` · `skip` | save 는 choice 만, **skip 은 floor 를 넘어야** 한다(확신 없는 skip 은 save). save 면 기본 모델이 1~3줄로 증류(`kind \| title \| text`), Jev 의 판단(질문·choice·confidence·근거)이 `Rationale` 노드로 함께 저장 |
| ⑦ 그래프가 도움될까 | 턴 앞 (그래프에 뭔가 있고 web 라우팅이 아닐 때) | `consult_graph` · `skip_graph` | state 에 그래프 요약(개수, 가장 많이 아는 경로, 최신 제목) |
| ⑧ 어떤 쿼리로 | ⑦ 이 consult 일 때 | `by_keywords` · `by_paths` · `recent` · `most_helpful` | 네 개의 고정 Cypher 중 하나를 Jev 가 고른다. 결과 없으면 키워드로 폴백. 결과는 `[graph memory]` 자료로 모델에, 파일 스캔 **전에** |

간선이 곧 학습이다: 어떤 턴에 건네진 지식은 `HELPED` 간선과 `uses` 카운트를 얻고, 쿼리는 uses 순으로
정렬한다 — 계속 도움이 되는 지식이 올라온다. Jev 의 근거를 `JUSTIFIED_BY` 로 붙여 두는 이유는 나중에
"왜 이걸 기억하고 있지?"에 답하기 위해서다(`agent-one memory query`).

**skip 에만 floor 를 거는 이유** (실측, 2026-09-23): `hello.py` 한 줄 턴은 `skip` 0.99 — 맞다. 그런데 `run.ps1` 과
README 를 쓰고 실행까지 한 턴이 `skip` **0.16** 으로 돌아왔다. 선택지 문구가 든 예("동작하는 명령")에 정확히
해당하는 턴인데도 엔진이 "모르겠다"고 한 것이다. 비용이 비대칭이라 규칙도 비대칭으로 둔다: 잘못 남긴
지식은 3줄이고 쓰이지 않으면 랭킹에서 가라앉지만, 잘못 잊은 지식은 다음 세션의 파일 스캔 한 번이다.
그래서 안전 질문(③)의 거울상 — 거기서는 *허용* 쪽(`safe`)이 floor 를 넘어야 했고, 여기서는 *잊는* 쪽(`skip`)이
넘어야 한다. 화면에는 `knowledge: kept 2 item(s) — the engine leaned to skip but was not sure` 로 남는다.

**실측 (2026-09-23, 백그라운드 세션에서 `agent-one ask` 로 자기 테스트)**

| 턴 | ⑥ 남길 가치 | 그래프 |
|---|---|---|
| `hello.py` 한 줄 생성·실행 | `skip` 0.99 | 0 items — 맞다 |
| `run.ps1` + README 작성·실행 (문서화된 설정) | `skip` 0.98 | 0 items — 선택지 문구대로("파일이 이미 말하는 것") |
| `py -3.99` 로 바꿔 실행 → 실패 → 원인 찾아 복구 | `save` 0.21 | 2 items: (constraint) Python Versioning, (procedure) Stable Script Execution |
| "특정 파이썬 버전 지정 시 주의점?" (한국어) | — | ⑦ `consult_graph` 0.70 → ⑧ `by_keywords` → **0건** |

마지막 줄이 문제였다. 지식은 영어로 증류됐고("Python Versioning") 질문은 한국어("파이썬 버전")라 키워드가
하나도 겹치지 않았다. 두 가지로 막았다: 증류 라인에 네 번째 칸 `keywords` 를 두어 영어와 사용자 언어의
검색어를 함께 저장하고(`python 파이썬 version 버전`), 고른 쿼리와 키워드 폴백이 모두 빈손이면 최신 항목을
그냥 건넨다(`recent (fallback)`) — Jev 가 "도움된다"고 했는데 단어가 안 맞는 건 "아니오"가 아니다. 같은
질문을 다시 물었을 때: `graph: consulted via recent (fallback) — 2 item(s)`, `helped 2 times`.

같은 실측에서 그래프와 무관하게 잡힌 것 둘: 모델이 JSON 안에 `".\run.ps1"` 을 써서 `\r` 이 캐리지리턴으로
풀렸고(PowerShell 에 `.<CR>un.ps1` 이 갔다), 실패한 명령을 반복하다 "use the result" 만 듣고는 성공했다고
보고했다. 각각 `ToolCall.RestoreEscapes` 와 실패 내용을 적어 주는 반복 넛지로 고쳤다. 그리고 `session start`
가 셸 파이프에서 호출되면 데몬이 핸들을 물려받아 파이프가 3분 55초 동안 안 풀렸다 — Windows 에서는
CreateProcessW(bInheritHandles=FALSE) 로 띄운다(0.37초).

Jev 를 쓰지 않는 자리: 증류(텍스트 생성)와 Cypher 문장 자체. Jev 는 고르고, LLM 은 쓴다 — 8-D 이후
줄곧 같은 분업이다.

## 8-I. 일시중지: Esc 뒤에 친 한 줄이 재개·중단·개선 중 무엇인지 Jev 가 읽는다 (2026-09-23)

턴이 도는 중에 Esc 를 누르면 루프가 **다음 스텝 앞에서** 멈춘다(모델이 답하는 중간이나 명령이 도는 중간은
끊을 수 없다 — `AgentLoop.Pause`, 스텝마다 게이트를 본다). 그 다음에 친 한 줄이 무엇인지가 ⑨ 번 질문이다.

| 질문 | 시점 | 선택지 | 규칙 |
|---|---|---|---|
| ⑨ 일시중지 뒤의 한 줄 | Esc 로 멈춘 뒤 Enter | `resume` · `stop` · `refine` | choice 만(세 선택지는 floor 를 잘 못 넘는다). state = 진행 중인 요청 + 지금까지 한 스텝 + 친 한 줄. 빈 줄이거나 키가 없으면 단어표(계속/그만/…)로 판정, 나머지는 refine |

`refine` 이면 그 줄이 `[the user, mid-turn] …` 사용자 메시지로 모델 앞에 놓이고 루프가 이어진다 — "탭 대신
스페이스" 를 빌드 중간에 말하면 그 빌드가 바뀐다. `stop` 은 그 턴의 토큰만 취소해 `cancelled` 로 끝난다.

**같은 날의 실측**: 이 기능을 넣으려다 그 전 단계에서 "요청 중에 키가 안 먹는다"는 보고가 왔다. 헤드리스 창을
액터 위에 띄워 보니 봇 액터의 첫 콜백(`thinking`)이 봇 스레드에서 창을 다시 그리다 Termina 루프와 맞물려
멈췄고, 그 뒤로는 키도 결과도 오지 않았다(직접 세션 + 비동기 제공자는 정상). AgentZero 의 규칙 그대로
고쳤다 — 액터는 UI 코드를 실행하지 않는다: 게이트웨이는 콜백을 큐에 넣고 펌프 스레드 하나가 순서대로
이벤트를 올리며, 창의 뷰모델은 그것을 다시 Termina 루프에 `Post` 한다. `ChatTuiOverActorsTests` 가 그
재현을 회귀 테스트로 남겼다.

## 8-J. PDSA 루프 영입: 계획이 문이고, 한 바퀴가 끝나면 지식에 간선이 걸린다 (2026-09-24)

그래프 메모리(8-H)는 `akka-graph-loop` 의 **PDSA 그래프 메모리**에서 스키마만 가져왔다. 이번에는 루프
자체를 가져왔다 — 데밍의 **Plan · Do · Study · Act**, 그리고 그가 끝까지 고집한 세 번째 칸: `Check`(됐나?)가
아니라 **`Study`(무엇을 배웠나?)**. 원본은 `C:\code\psmon\akka-graph-loop` 의 `PdsaWorkflow`(사이클/단계
노드, `NEXT`·`REINFORCES` 간선, 폐루프 컬럼 `expected`/`verdict`/`actual`)이고, 여기서는 그것을
**한 턴 = 한 단계, 여러 턴 = 한 사이클**로 바꿔 스마트 모드에 앉혔다.

### 왜 같은 DB 인가

사이클은 워크스페이스의 `knowledge.kuzu` **안에** 산다. DB 를 나누면 같은 행은 담을 수 있어도
`(:Cycle)-[:TAUGHT]->(:Knowledge)` 간선은 못 만든다. 그 간선이 이 기능의 전부다 — "게시판 API 만들 때
뭘 배웠지?"를 텍스트 검색이 아니라 **사이클을 타고** 답한다.

```
(:Cycle)-[:HAS_PHASE]->(:Phase)<-[:RAN_IN]-(:Turn)-[:LEARNED]->(:Knowledge)
(:Cycle)-[:NEXT_CYCLE]->(:Cycle)     돈 순서
(:Cycle)-[:REINFORCES]->(:Cycle)     앞 사이클이 못 미쳐서 생긴 사이클
(:Cycle)-[:TAUGHT]->(:Knowledge)     닫을 때 — 이 사이클이 남긴 것
(:Cycle)-[:BUILT_ON]->(:Knowledge)   닫을 때 — 이 사이클이 딛고 선 것
```

### 두 질문이 늘었다

| 질문 | 시점 | 선택지 | 규칙 |
|---|---|---|---|
| ⑩ 어느 단계인가 | 턴 앞 (라우팅·설계 뒤) | `plan` · `do` · `study` · `act` | **사이클이 돌고 있으면 choice 만** — 단계를 잘못 붙여도 행 하나지 행동이 아니다. **사이클이 없을 때는 floor 를 넘은 `plan` 만** 사이클을 연다(확신 없는 추측이 뒤따르는 여러 턴을 끌고 들어간다) |
| ⑪ 계획대로였나 | ⑩ 이 `study` 일 때, 턴 뒤 | `met` · `partial` · `unmet` | choice 만(세 선택지는 floor 를 잘 못 넘는다). state = Plan 이 적어 둔 `expected` + 이번 턴 요청 + 실제 결과. 엔진이 실패하면 `unjudged` — "met" 으로 넘겨짚지 않는다 |

### 문은 "플래닝"이다

이미 있던 ② 범위 질문이 `needs_design` 을 내면 강한 모델이 설계를 쓴다. **그 턴이 곧 Plan** 이므로 ⑩ 을
묻지 않고 사이클을 연다(`Decision.Called = false` 로 기록되어, 나중에 로그를 봐도 엔진이 답한 게 아니라는
게 남는다). 설계가 없고 사이클도 없으면 ⑩ 을 묻되 **확신 있는 `plan`** 일 때만 연다. 그 외에는 PDSA 가
아예 비켜선다 — "빌드 돌려봐" 한 줄이 사이클을 만들지 않는다.

사이클이 도는 중에 다시 `plan` 이 나오면 그건 이 사이클의 두 번째 계획이 아니라 **다음 사이클**이다.
돌던 사이클은 `abandoned` 로 닫히고, 그 사이클의 Study 가 `partial`/`unmet` 이었으면 새 사이클이
`REINFORCES` 간선으로 그것을 가리킨다. 원본의 `PendingReinforceTarget` 과 같은 판단이되, 기준을 Act 의
플래그가 아니라 **Study 의 판정**에 뒀다 — 판정이 곧 피드백이라는 게 PDSA 의 요지라서다.

### 닫기는 증류 **뒤**에

Act 턴이 사이클을 닫는다. 그런데 지식 증류는 턴이 끝난 뒤 백그라운드에서 돈다(8-H). 먼저 닫으면
**마지막 교훈이 아직 저장되지 않은** 사이클에 간선을 거는 꼴이라, `LearnThenCloseAsync` 가 증류를 먼저
기다리고 그다음 닫는다. 세션 테스트가 정확히 그 순서를 잡는다 — 닫힌 사이클의 `TAUGHT` 가 1개여야 한다.

`BUILT_ON` 은 사이클의 턴들에 `HELPED` 로 건네진 **기존** 지식이다. 사이클 안에서 배운 것이 나중 단계에서
다시 쓰였다면 그건 `TAUGHT` 쪽에만 남긴다 — 자기가 가르친 걸 딛고 섰다고 두 번 세지 않는다.

### 보는 곳

- `/status` (창에서 F2): `pdsa  3 cycles · 9 phases · plan met 1/2 · 5 knowledge edges · running #3 (plan, do)`
- `agent-one memory pdsa [n]` — 사이클별 단계 한 줄씩, 그리고 `taught` / `built on` 목록
- `agent-one memory query "MATCH (c:Cycle)-[:TAUGHT]->(k:Knowledge) RETURN c.title, k.title" --columns 2`

Jev 를 쓰지 않는 자리는 그대로다: 계획 문장을 쓰는 것도, 결과를 서술하는 것도 LLM 이다. Jev 는 **어느
칸인지**와 **계획대로였는지**만 고른다.

## 8-K. 빈 폴더에서 "만들어줘"가 answer 로 라우팅되어, 만들지 않고 만들었다고 말했다 (2026-09-24)

PDSA 루프(8-J)를 실사용해 보다 나온 것. 워크스페이스는 빈 폴더(`a1-test/tetris`), 요청은
**"테르리스 웹게임 만들어"**. 세션 로그(`20260924-115622-chat.jsonl`)가 전 과정을 남겼다.

| # | 일어난 일 |
|---|---|
| 1 | ① route → **`answer_directly` 0.77** (floor 통과 → 강제). 툴 전면 차단 |
| 2 | 모델이 `list_files` → 거부. 다시 `list_files` → 반복 가드 |
| 3 | `write_file index.html` (806자) → **거부**. `write_file style.css` (1,609자) → **거부** |
| 4 | 모델이 `final`: *"세 개의 파일을 생성하고 내용을 채웠습니다"* — **디스크는 비어 있음** |
| 5 | ④ escalation 0.99 → qwen3.8-27b 가 **17,788자** 코드 생성 |
| 6 | 그 17,788자는 스텝 detail 에 `"qwen3.8-27b · 17788 chars"` 로만 기록. **본문은 어디에도 없음** |
| 7 | ⑥ 남길 가치 → save → 존재하지 않는 코드에 대한 지식 3건이 그래프에 적재 |

두 번째 턴("브라우저에 구동해 실행해줘")은 route 가 unsure(0.26)라 **전 툴이 열려 있었는데도**
모델이 툴을 하나도 안 쓰고 또 *"index.html 파일로 작성하여 제공했습니다"* 라고 답했다.

### 고친 것 넷

**① 선택지 문구 — 빈 폴더의 신규 제작도 워크스페이스 일이다.** `work_in_workspace` 가
*"the project in the working directory"* 로 시작해서, 프로젝트가 아직 없는 빈 폴더는 해당이
안 되는 것처럼 읽혔다. 이제 *"무언가를 디스크에 만들거나 바꿔야 한다 — 폴더가 비어 있고
프로젝트가 아직 없어도, **파일이 곧 산출물**이므로 해당된다"* 로 시작한다. `answer_directly`
에는 반대 방향의 한 줄을 넣었다: *"모델이 답변 안에 코드를 쓸 수 있다는 이유로 이걸 고르지 말 것
— 만들어 달라고 했으면 디스크에 있기를 원하는 것이고, 이 선택지는 아무것도 쓸 수 없다."*

**② route 강제가 벽이 되지 않게 — 모델이 두 번 요구하면 길을 내준다.** 강제 자체는 유지한다
(제안으로 두면 작은 모델은 여러 선택지 중 하나로 본다 — 8-D). 다만 **첫 거부는 유효, 두 번째
거부 시점에 route 를 버린다**(`AgentLoopGuards.RefuseFamily`, `route-overruled` 스텝). 0.3초짜리
한 번의 추측보다, 모델이 같은 family 를 두 번 요구하는 쪽이 요청에 대한 더 나은 증거다.
거부된 호출은 `Guards.Forget` 으로 기록에서 지운다 — 실행되지 않은 호출을 반복으로 세면
재시도가 반복 넛지에 먼저 걸려 family 검사에 영영 도달하지 못한다(테스트로 잡았다).

**③ 안 쓴 파일은 완료가 아니다.** `final` 의 텍스트에 펜스 코드가 400자 이상 있는데 그 턴에
성공한 `write_file` 이 하나도 없으면 한 번 되돌린다(`ShowsUnwrittenCode`, `unwritten` 스텝):
*"네 답변에 코드가 있는데 이번 턴에 파일을 하나도 쓰지 않았다. 디스크에 아무것도 없어서 사용자는
실행할 수 없다. 쓰지 않은 파일을 만들었다고 절대 말하지 마라."* 판정 기준을 **단어가 아니라
펜스**로 둔 이유는 주장 자체가 사용자 언어로 쓰이기 때문이다("세 개의 파일을 생성하고"). 넛지는
1회이고, 정말 코드만 보여주려던 것이면 다시 `final` 로 답하면 그대로 통과한다.

**④ 강한 모델의 출력은 파일에 남긴다.** `reasoning` / `design` 핸드오프가 길이만 로그에
남기고 본문은 일상 모델의 컨텍스트에만 있었다 — 그 컨텍스트가 정리되면 17,788자가 그대로
증발한다. `ChatSession.LogText` 가 `reasoning-text` / `design-text` 엔트리로 세션 파일에
전문을 쓴다. 화면 스텝은 그대로 한 줄이다.

시스템 프롬프트에도 두 줄을 넣었다: 만들어 달라는 요청이면 **답변 전에** write_file 로 전부 쓸 것,
그리고 실제로 성공한 write_file 없이 파일을 만들었다고 말하지 말 것.

회귀 테스트: `FileDeliverableTests`(8개) — 두 번 요구하면 route 가 열리는지, 첫 거부는 유지되는지,
펜스 코드 + 미기록이 넛지를 받는지, 넛지가 1회인지, 쓴 뒤에는 같은 답이 통과하는지.

### 두 번째 실행: 고쳐진 것과, 그제서야 보인 것 (같은 날)

고친 빌드로 다시 돌렸다(`20260924-123703-chat.jsonl`). ①②④는 전부 작동했다:

| 단계 | 결과 |
|---|---|
| route | `→ workspace` **0.99** (직전 실행은 `answer` 0.77) |
| scope | large → qwen3 설계, `design-text` **4,187자 전문 보존** |
| pdsa | `cycle #1 opened at plan` |
| write_file | `index.html` **성공** (1,429자) |
| final | *"1. index.html … 2. css/style.css … 3. js/tetris.js … 완성되었습니다"* |

Playwright(Edge)로 띄워 보니 `style.css` · `tetris.js` · `main.js` 가 전부
`ERR_FILE_NOT_FOUND`, 캔버스는 한 픽셀도 칠해지지 않았다(`boardPainted: false`).
순수 404 라 `pageErrors` 는 비어 있어서 페이지는 "정상 로드"로 보였다.

③번이 못 잡은 이유는 둘이다: `write_file` 이 **한 번이라도** 성공하면 가드가 해제되고,
이번 답변에는 펜스 코드가 아예 없었다. 그래서 질문을 바꿨다 — *"뭔가 쓰긴 했나"* 가 아니라
**"방금 이름 댄 파일이 전부 거기 있나"**.

**⑤ 답변이 이름 댄 파일은 실제로 있어야 한다.** `final` 에서 경로를 뽑아(`FilePathsIn`,
확장자 기반 정규식), 이번 턴에 쓴 것도 아니고 디스크에도 없는 것이 있으면 한 번 되돌린다
(`unwritten` 스텝에 누락된 파일명을 적는다). 경로는 언어 중립이라 판정이 확실하다 —
주장은 "완성되었습니다"이거나 "done"이지만 `css/style.css` 는 어느 언어에서도 같다.

단, **이번 턴에 실제로 파일 작업을 한 경우에만** 검사한다(`DidFileWork`). 계획 턴은 아직 없는
파일을 이름 대는 게 정상이고, 그건 거짓 보고가 아니다 — 이 구분을 빼먹었다가 PDSA 세션
테스트가 바로 잡았다(설계 턴의 답변이 `src/Api/Program.cs` 를 언급해 넘어갔다).

### 세 번째 실행: 가드는 잡았고, 예산이 1회라 거기서 끝났다 (같은 날)

`20260924-133629-chat.jsonl`. ⑤번이 정확히 발동했다:

```
 9 write_file  index.html                ok
10 unwritten   the answer names 5 file(s) that are not on disk:
               css/style.css, js/core.mjs, js/app.mjs, test/core.test.mjs, server.mjs
11 write_file  css/style.css             ok     ← 넘지 직후
13 run_command node --test test/core.test.mjs → Could not find   ← 안 만든 파일을 실행
17 final       "6개 파일을 모두 작성했습니다"                  ← 예산 소진으로 통과
```

디스크에는 2개뿐이었고, 모델은 테스트 실패를 *"환경적인 이유(파일 경로 인식 오류)"* 로
돌렸다 — 진짜 이유는 그 파일을 안 만든 것이다.

**넓지를 횟수가 아니라 진전으로 묶었다.** 실측은 명확하다 — **넓지 1회당 파일 1개**. 6개짜리
빌드에는 6번이 필요한데 예산이 1이었다. 그래서 카운터를 없애고 정지 조건을 바꿨다:
**직전 넓지 이후 쓴 파일이 늘었을 때만 다시 넓진다**(`TryConsumeUnwrittenNudge(writesSoFar)`).
알려줬는데도 아무것도 안 쓴 모델은 다음에도 안 쓴다. 무한 루프는 루프의 스텝 예산이 막는다.
넓지 문구에도 한 줄 추가 — *"파일이 전부 존재할 때까지 실행하거나 테스트하지 마라."*

> 첫 번째 사건의 손해는 복구되지 않았다. 로그에 남은 건 거부당한 `write_file` 두 건의 본문
> (gemma 판 index.html 806자 · style.css 1,609자)뿐이고, 정작 쓸 만했던 qwen3 판 17,788자는
> 없다. ④ 가 그것 하나를 위한 수정이다.

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
