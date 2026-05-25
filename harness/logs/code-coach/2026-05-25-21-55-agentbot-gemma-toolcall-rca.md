---
date: 2026-05-25T21:55:00+09:00
agent: code-coach
type: research
mode: log-eval
trigger: "원인분석 (function-call JSON parse error in AgentBot Gemma test)"
---

# AgentBot × Gemma function-call 파싱 에러 원인분석

## 실행 요약

사용자가 AgentBot UI에서 Gemma 모델로 테스트 중 다음 에러 관측:

```
⚠ model returned unparseable JSON at iteration 0:
  missing 'tool' field; raw="{"message": "안녕하세요! 무엇을 도와드릴까요?"}"
```

에러 발생 지점:
- `Project/ZeroCommon/Llm/Tools/LocalAgentLoop.cs:323` — `ParseToolCall`이
  `JsonException("missing 'tool' field")` 를 던지는 곳.
- `Project/ZeroCommon/Llm/Tools/ExternalAgentLoop.cs:101` / `LocalAgentLoop.cs:106` —
  catch에서 위 메시지를 합성하는 곳.

## 결정적 단서 — 시스템 프롬프트의 예시 문자열을 LLM이 그대로 복사

`Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs:189-198` 의 `done` 메시지 예시
블록을 보면 **에러 raw와 100% 동일한 문자열**이 박혀 있다:

```text
`done` message — STRICT rules to keep JSON parseable:
  ...
  - Bad:  done({"message": "Claude said: '{\"foo\":\"bar\",...long paste...'"})
  - Good: done({"message": "Claude greeted you back and asked what to do."})
  - Good: done({"message": "안녕하세요! 무엇을 도와드릴까요?"})
                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
                            raw=" ... " 안의 문자열과 한 글자도 다르지 않음
```

즉 모델이 "안녕하세요" 류의 인사 → Mode 1 → `done` 호출까지는 옳게 판단했으나,
**시스템 프롬프트의 의사 함수호출 표기법 `done({...})` 을 보고 안쪽 args만**
`{"message": "..."}` 로 출력했고, 바깥 `{"tool":"done","args":...}` envelope
래퍼를 빠뜨렸다. 전형적인 few-shot in-context 학습 실패 — 예시가 너무 리터럴해서
모델이 패턴이 아니라 토큰을 그대로 모방한 케이스.

## 어느 루프가 실행됐는가 — External (REST) 경로로 단정 가능

두 루프 모두 같은 에러 문구를 생성할 수 있지만, **LocalAgentLoop은 GBNF로
샘플러 단에서 envelope을 강제**한다 (`AgentToolGrammar.cs:208-224`):

```
root ::= ws "{" ws "\"tool\"" ws ":" ws toolname ws "," ws
         "\"args\"" ws ":" ws args ws "}" ws
```

이 root 룰이 활성화된 상태에서는 `{"message": ...}` 만 단독으로 나올 수 없다.
따라서 이 에러는 **GBNF가 없는 ExternalAgentLoop (REST) 경로에서만 가능**하다.
근거:

1. `Project/ZeroCommon/Llm/Tools/ExternalAgentLoop.cs:13-21` —
   "REST providers don't expose a grammar hook" 명시.
2. `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs:1240-1246` —
   `ActiveBackend == External` 분기에서 GBNF 없이 `ExternalAgentLoop` 생성.
3. `ExtractFirstJsonObject` (`ExternalAgentLoop.cs:273-295`) 는 텍스트의
   **첫 번째 균형 잡힌 `{ ... }` 만** 추출한다. 모델이 `done({"message": "..."})`
   처럼 의사 함수호출 형식으로 응답해도 안쪽 `{"message": "..."}` 가 잡혀
   `ParseToolCall` 에 그대로 넘어가 `missing 'tool'` 에서 폭사.

→ 결론: 사용자는 **AgentBot 의 External(Webnori/Ollama 등) 백엔드에서 Gemma 모델로
테스트**한 상태였다. Settings → LLM → Active Backend = External 인지 확인 필요.

## 보강 정황

- `ExternalAgentLoop.cs:13-19` 주석이 이미 이 시나리오를 인지하고 있음:
  > "Gemma 4 follows the in-context schema reliably; non-Gemma models …
  >  will likely emit free-form prose around or instead of the JSON envelope."
- 그러나 Gemma 4 자신도 시스템 프롬프트 예시가 `done({"message":...})` 처럼
  pseudo-function 표기일 경우 **안쪽 객체만 따라쓰는** 회귀를 일으킬 수 있음.
  이번 케이스가 그 회귀의 명시적 증거.
- 즉 "Gemma 가 신뢰할 수 있다" 는 가정이 흔들린 것이 아니라, **프롬프트의
  예시 표기 자체가 함정** 이었다.

## 평가 (code-coach 4축)

| 축 | 결과 |
|---|---|
| Idiom 정합성 | OK — 파싱 / 가드 / 에러 메시지는 깔끔. |
| Seam 존중 | OK — Local/External 가 IAgentLoop seam을 공유하고 GBNF 유무만 분기. |
| 실패 모드 | **Should-fix** — REST 경로는 envelope 누락에 대한 self-healing 0건. |
| 가독성 | **Suggestion** — 시스템 프롬프트의 `done({…})` 의사 표기가 학습 함정. |

## 다음 단계 제안 (우선순위 순)

### 1. [Must-fix] 시스템 프롬프트의 `done` 예시를 envelope 형식으로 정규화

`AgentToolGrammar.cs:196-198` 를 다음과 같이 교체:

```text
- Bad:  {"tool": "done", "args": {"message": "Claude said: '{\"foo\":\"bar\",...long paste...'"}}
- Good: {"tool": "done", "args": {"message": "Claude greeted you back and asked what to do."}}
- Good: {"tool": "done", "args": {"message": "안녕하세요! 무엇을 도와드릴까요?"}}
```

이렇게 하면 모델이 토큰을 그대로 복사하더라도 그 자체가 유효한 envelope이 된다.
원인 — 결과 분리. 가장 작은 변경으로 가장 큰 효과.

### 2. [Should-fix] ExternalAgentLoop 에 envelope 자가복구 1회 허용

`ExternalAgentLoop.cs:94-103` 의 catch 블록 직전에:

- 파싱된 JSON에 `"tool"` 필드는 없지만 `"message"` 필드만 있는 경우
- 1회에 한해 `{"tool":"done","args":<original>}` 로 wrap 해서 재해석

엄격성과 회복력의 trade-off — 1회 한정 + iteration 0 한정으로 묶으면
"무한 hallucination 보호" 와 충돌하지 않는다. 가드는 `AgentLoopGuards`
스타일로 카운팅.

### 3. [Should-fix] envelope 미준수 시 자가교정 피드백 turn 추가

현 구현은 첫 envelope 미준수에서 즉시 루프 종료 (`break;`). REST에서는
다음 turn에 교정 지시를 user role로 주입해 self-repair 기회를 1~2회 허용해도
"hard-stop 가드" 가 무한 루프를 막아준다.

피드백 메시지 예시:

```
--- TOOL CALL FORMAT ERROR ---
Your last reply was JSON but did not include the required envelope.
Reply with EXACTLY this shape next:
  {"tool": "<tool-name>", "args": { ... }}
For your previous intent, the correct envelope would have been:
  {"tool": "done", "args": {"message": "<your text>"}}
```

### 4. [Suggestion] AgentBot 첫 turn 직전에 백엔드/모델/엔벨로프-정책 한 줄 로그

현재 `AppLogger.Log("[AIMODE] StartAgentLoop sent (backend=External, …)")`
는 있으나, 에러 raw 에 모델명이 없어서 RCA 시 사용자 확인이 필요했다.
실패 메시지 자체에 `backend=External provider=<name> model=<id>` 를
prefix로 붙이면 다음에는 로그만 보고 바로 분기를 알 수 있다.

### 5. [Suggestion] 회귀 테스트 추가

`Project/ZeroCommon.Tests/ExternalAgentLoopTests.cs` 에 다음 케이스 추가:
- 모델이 `{"message": "..."}` 만 반환 → (수정 #2 이후) `done` 으로 자가복구 성공
- 모델이 `done({"message": "..."})` pseudo 표기 → 추출 후 자가복구
- 자가복구 카운터가 1회로 capped 되는지 검증

## 사용자 확인 요청 사항

1. `Settings → LLM → Active Backend` 가 **External** 이었는지 확인.
   (Local 이었다면 이 분석의 분기가 틀린 것이라 GBNF/grammar 활성화 여부
   재조사 필요.)
2. 어떤 외부 provider/model 이었는지 (Webnori gemma-4-e4b? Ollama gemma2? Gemma3?).
   재현 가능한 가장 작은 모델 조합을 알면 위 패치를 회귀 테스트로 굳히기 좋음.

## 실측 (2026-05-25 22:00)

사용자 확인: **Webnori a1 + `google/gemma-4-e4b`** (사전 공유 키, 비밀 아님).

회귀 테스트 추가 — `Project/ZeroCommon.Tests/WebnoriExternalSmokeTests.cs`:
`Greeting_drives_clean_done_envelope_against_real_Gemma4` — `WEBNORI_SMOKE=1`
환경에서 실제 a1 API 를 호출, "안녕" 1회 send, MaxIterations=2 / temp=0.0 /
45s turn timeout.

실행 결과 (xUnit detailed log):

```
Webnori responded in 1818ms
TerminatedCleanly = False
FailureReason   = model returned unparseable JSON at iteration 0:
                  missing 'tool' field;
                  raw="{"message": "안녕하세요! 무엇을 도와드릴까요?"}"
FinalMessage    = (동일)
TurnCount       = 0
```

→ 분석 가설 100% 적중. 분기 단정도 옳음 (External + 1.8s 응답, timeout/transient
무관). `raw` 가 `AgentToolGrammar.cs:198` 예시 문자열과 **한 글자도 다르지 않다**
는 사실로 "few-shot 예시를 토큰 단위로 복사" 가설을 실증.

테스트는 의도적으로 **현재 실패** → 패치(권고 #1 또는 #2) 적용 후 패스하도록
설계 — Assert.True(run.TerminatedCleanly) 가 fix gate 역할. 패치 적용까지 이
테스트는 RED 상태로 유지 (또는 `[Skip = "blocked by envelope fix"]` 로 격리
가능, 우리는 RED 유지 권장 — 누가 무심코 합쳐도 CI 가 잡는다).

## 패치 적용 (2026-05-25 22:10)

권고 #1 + #2 동시 적용.

### #1 — `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs`

`done` 예시 3줄을 의사 함수호출 표기 `done({...})` → 풀 envelope
`{"tool":"done","args":{...}}` 로 교체. 메타 지시문도 한 줄 추가:
"Examples are shown in the EXACT envelope you must emit. Copy the SHAPE —
do NOT drop the outer wrapper." 모델이 토큰 그대로 복사해도 valid 한 envelope
이 되는 것이 핵심.

### #2 — `Project/ZeroCommon/Llm/Tools/ExternalAgentLoop.cs`

- 인스턴스 필드 `_envelopeRepairUsed` (once-per-session 가드).
- 내부 정적 헬퍼 `TryRepairAsDoneEnvelope(rawJson, out repaired)`:
  - root 가 object 이고
  - `"tool"` 필드는 없고
  - `"message"` 필드가 string 인 경우에만
  - `{"tool":"done","args":<original>}` 형태로 wrap.
- ParseToolCall catch 블록에서 가드 충족 시 한 번 wrap 후 재파싱. 두 번째
  발생부터는 기존 hard-fail 경로 유지 — 지속적 schema drift 마스킹 방지.

다른 tool 들은 args 가 필수 (group/tab/text 등) 라 inner-only 형태로 의미가
있는 자동 복구가 불가능 → `done` 한정으로만 매칭 (스펙 좁히기 = 거짓양성 0).

### 검증

`Project/ZeroCommon.Tests/ExternalAgentLoopTests.cs` 에 5개 신규 케이스:

| 테스트 | 결과 |
|---|---|
| `TryRepairAsDoneEnvelope_wraps_inner_args_only_message` | PASS |
| `TryRepairAsDoneEnvelope_refuses_when_envelope_already_present` | PASS |
| `TryRepairAsDoneEnvelope_refuses_when_no_message_field` | PASS |
| `TryRepairAsDoneEnvelope_refuses_when_message_is_not_string` | PASS |
| `Loop_self_heals_inner_args_only_done_payload_once` | PASS |
| `Loop_does_not_self_heal_twice_in_same_session` | PASS |
| `Loop_falls_through_when_inner_payload_lacks_message_field` | PASS |

ExternalAgentLoopTests 전체 16/16 통과, TurnTimeout 회귀 가드도 무중단.

### Webnori 스모크 — RED → GREEN

```
Webnori responded in 2117ms
TerminatedCleanly = True
FailureReason   = (none)
FinalMessage    = 안녕하세요! 무엇을 도와드릴까요?
TurnCount       = 0
```

이번엔 fix #1 만으로 통과한 것인지 fix #2 가 자가복구한 것인지 표면적으로
불분명 — 둘 다 적용된 상태라 모델 응답이 이미 valid envelope 으로 왔는지
inner-only 였는데 wrap 됐는지 로그 한 줄 추가하면 다음에 더 명확해질 수 있음
(future improvement: `OnTurnCompleted` 로 wrap 발생 시 한 줄 로그).
TurnCount=0 ⇒ done 이 첫 turn 에 깨끗하게 emit 됨. fix #1 이 작동했다는 강한
신호 — 회귀 시에도 fix #2 가 안전망 역할.
