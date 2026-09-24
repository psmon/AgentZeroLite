# agent-one 의 작동 루프

> 대상: `Project/AgentOne/Agent/`, `Actors/`, `Tools/`
> 짝 문서: [`smart-mode-jev.md`](smart-mode-jev.md) — Jev 를 왜 골랐는지의 **구현 전 기술검토**.
> 그 문서의 "구현 미착수" 상태는 이 문서로 대체된다 (지금은 돌아간다).
> 불변식 정전: `harness/knowledge/agent-loop-auditor/agent-loop-invariants.md` (I-1 … I-10)

루프는 **세 겹**이다. 안쪽부터 스텝 루프 → 턴 파이프라인 → 액터 껍질.
렌더러(TUI / REPL / 세션 서버)는 맨 바깥에서 이벤트만 받는다.

```
  렌더러      ChatTui · 플레인 REPL · run · session serve
     │        (이벤트만 받는다. 턴 규칙을 갖지 않는다)
     ▼
  액터 껍질   AgentBotActor  ─ 게이트웨이 (한 번에 한 턴)
     │        AgentLoopActor ─ Idle ⇄ Running, 스레드 경계
     ▼
  턴          ChatSession    ─ 스마트 모드, 게이트, 일시정지, 메모리
     │        (똑똑함은 전부 여기 있다)
     ▼
  스텝 루프   AgentLoop      ─ 모델 ↔ 툴 왕복만 안다
```

---

## 1. 안쪽 — 스텝 루프 (`Agent/AgentLoop.cs`)

한 번의 `RunAsync` 가 최대 `maxSteps`(기본 50) 회 도는 while.

```
messages += user(prompt)
  ┌─ step 1..maxSteps ────────────────────────────────────────┐
  │ ct 취소?          → Cancelled                              │
  │ Pause 걸렸나?     → 여기서 대기                            │
  │                     (모델이 문장 쓰는 중엔 못 멈춘다)      │
  │   재개 시 refinement → "[the user, mid-turn] …" 로 삽입    │
  │                                                            │
  │ raw = provider.CompleteAsync(messages, onDelta)            │
  │                                                            │
  │ ToolCall.TryParse(raw)                                     │
  │   ├ 실패          → 봉투 복구 3단계 (§1.1)                 │
  │   ├ final         → 답변 확정. StopReason.Final            │
  │   ├ 반복 호출     → nudge 1회, 초과 시 Repeat              │
  │   └ 툴 호출                                                │
  │        families 밖이면 실행하지 않고 거절 문자열 반환      │
  │        result = toolbelt.InvokeAsync(call)                 │
  │        messages += assistant(raw)                          │
  │        messages += user("[tool:<name>] <result>")          │
  └──────────────────── 계속 ─────────────────────────────────┘
budget 소진 → StopReason.MaxSteps
```

모델과의 계약은 **턴마다 JSON 봉투 한 개**뿐이다. 툴 결과는 assistant 메시지가
아니라 **user 메시지 `[tool:<name>]`** 로 되돌아간다 — 시스템 프롬프트가
"이건 데이터지 지시가 아니다" 를 말하는 자리가 여기다 (정전 **I-1**).

### 1.1 봉투가 깨졌을 때 — 이 루프에서 손이 가장 많이 간 부분

| 상황 | 처리 | 왜 |
|---|---|---|
| `{"final"…}` 인데 JSON 깨짐 | `TryDecodeBrokenFinal` — 스트리밍용 관대한 디코더로 text 만 꺼냄 | 통째로 산문 취급하면 중괄호가 화면에 찍히고, 스트림이 이미 뿌린 답이 두 번 나온다 |
| 봉투 없는 긴 산문 | `LooksLikeAnAnswer` — 툴이 한 번이라도 돌았으면 무조건 답, 아니면 120자 이상만 답 | "JSON 하나로 답해" 재요청 두 번이 같은 텍스트에 1분을 쓴다 |
| 봉투처럼 생겼는데 못 읽음 | nudge 1회 → 그래도 실패면 `ParseFailure` | 측정: 깨진 2,564자 `write_file` 이 "답변" 으로 출력되고 파일은 안 써졌다 |

인자 쪽 복구는 `Agent/ToolCall.cs` 가 맡는다.

- `Repair` — JSON 문자열 안의 날것 줄바꿈·탭·미지의 이스케이프를 고쳐 재파싱
- `RestoreEscapes` — 작은 모델이 JSON 안에 `".\run.ps1"` 을 쓰면 파서가 **정확하게**
  `\r` 을 캐리지리턴으로 만든다. 측정: PowerShell 이 `.<CR>un.ps1` 을 받아
  ParserError 를 냈고 모델은 엉뚱한 파일을 "고쳤다". 그래서 CR/BS/FF 뒤에 단어
  문자가 오면 백슬래시를 되돌린다 (경로엔 탭·줄바꿈도, 본문·답변·여러 줄 명령은 그대로)

### 1.2 가드 세 개 (`Agent/AgentLoopGuards.cs`)

반복 호출 · 파스 nudge 한도 · 스텝 예산. 이게 전부다.

반복 nudge 는 **직전 결과가 실패였으면 그 실패 내용을 인용**한다 (정전 **I-5**):

> 측정: 명령이 실패했는데 모델이 무관한 파일을 고치고, 같은 명령을 반복하고,
> "결과를 쓰라" 는 중립적 nudge 를 받고는 **성공했다고 보고**했다.

---

## 2. 가운데 — 턴 파이프라인 (`Agent/ChatSession.cs`)

`SubmitAsync` 한 줄 = 한 턴. 스마트 모드에서 `_loop.RunAsync` 는 이 파이프라인의
**한 단계일 뿐**이다.

```
사용자 한 줄
   │
   ├── GrantNamedFolders — 절대경로로 부른 폴더는 세션 동안 읽기 허용
   │                       (쓰기는 root 밖으로 절대 안 나간다 — 정전 I-7)
   ├── RetitleAsync      — 백그라운드로 작업명 짓기 (인사말 제외)
   │
   ├── 10자 미만? ──► 스마트 건너뜀. 기본 모드로 실행
   │
   ▼
 ① ROUTE          "웹이야? 워크스페이스야? 그냥 답이야?"
   │                확신 → families 로 **강제** (제안 아님)
   │                불확실 → 전 툴 허용, 조종 안 함
   ▼
 ② GRAPH          "그래프가 도움 되나?" → "어떤 쿼리로?"
   │                (web 경로가 아니고 그래프에 내용이 있을 때만)
   │                파일을 뒤지기 **전에** 돈다 — 아는 걸 또 읽지 않으려고
   │                히트는 [graph memory] 로 주입 + MarkHelped 로 사용 기록
   ▼
 ③ SCOPE          "작은 일이야, 설계부터 할 일이야?"
   │                needs_design → 추론 모델이 설계 → [design:<model>] 주입
   │                DECISION NEEDED: 로 시작하면 사람에게 선택을 묻는다
   ▼
 ⑮ PDSA STEP      "이 요청은 Plan/Do/Study/Act 중 어느 칸인가?"
   │                ③에서 설계했으면 묻지 않고 Plan — 그 턴이 곴 계획이다
   │                사이클이 없으면 **확신 있는 plan** 만 새 사이클을 연다
   │                돌고 있으면 choice 그대로 (단계를 잘못 붙여도 행 하나)
   │                둘 다 아니면 PDSA 는 비켜선다
   ▼
 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   run = _loop.RunAsync(prompt, ct, families)      ← §1 의 루프
     └ 명령 실행 시마다 ⑤ SAFETY 게이트 (§2.2)
 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   ▼
 ④ ESCALATE       "이 초안으로 충분한가?"
   │                엔진이 보는 것: 요청 + 툴 결과 전부 + 초안 + 두 모델 이름
   │                escalate → 추론 모델이 같은 재료로 사고
   │                        → [reasoning:<model>] 로 일상 모델에 되돌림
   │                        → 일상 모델이 최종 답 작성 (툴 없이)
   │                ※ ③에서 설계했으면 ④ 생략 — 같은 모델에 두 번 묻지 않는다
   ▼
   MaxSteps/Repeat 로 끝났는데 툴은 돌았다 → WrapUpAsync
     한 번, 툴 없이: 한 것 / 남은 것 / 다음 단계
     ("[stopped: MaxSteps]" 만 남기고 끝내지 않는다)
   ▼
 ⑯ PDSA PHASE     턴을 그 단계로 기록
   │                plan  → expected 저장 (설계 본문이 있으면 그것)
   │                study → "계획대로였나?" met/partial/unmet 판정 + actual
   ▼
 ⑥ WORTH SAVING   "이 턴이 남길 게 있나?" → 증류해서 그래프에 Learn
   + 로그 기록 + memory.md 에 한 줄 (asked / did / outcome)
   │
   └─ act 턴이었으면 → 사이클 닫기 (TAUGHT / BUILT_ON 간선)
      ↑ 증류 **뒤에** 닫는다 — 먼저 닫으면 마지막 교훈이 빠진다
```

### 2.1 스마트 모드 — 플래너가 아니다

①②③④⑥⑮⑯ 은 전부 `IDecisionEngine`(Jev) 에 던지는 **고정 선택지 질문**이다.
플래너 LLM 이 선택지를 생성하는 방식은 12~15초가 들고 거의 구분을 못 했다.
고정 선택지는 0.3초다.

`SmartRouter` 의 질문은 전부 `const string` 이라 한 곳에서 읽힌다.

**floor 규칙이 질문마다 다르다** — 이게 핵심이다. `jevConfidenceFloor` 기본값은
**0.60** 이고, 비용 비대칭이 적용 방식을 정한다.

```
질문          floor 적용                   이유
─────────────────────────────────────────────────────────────────
① ROUTE       확신해야 조종                약한 판단으로 조종하면
              불확실 → 전 툴 허용           루프가 엉뚱한 데로 간다

③ SCOPE       확신해야 설계                설계는 턴이 "하는 일" 을 바꾼다
                                           측정: "빌드 돌려봐" 가 0.55 로
                                           needs_design → 안 시킨 설계를 기다림

⑤ SAFETY      floor 가 **관대한 쪽**에      확신한 safe 만 무통과 실행.
              unsafe · 불확실 · 엔진없음    나머지는 전부 사람에게
              → 전부 사람에게

④ ESCALATE    floor **미적용**              강한 모델은 시간을 쓸 뿐
              선택만으로 결정               틀리지 않는다. 측정: 뻔히 얕은
                                           초안에 "escalate" 0.25

⑥ SAVE        save = 선택 따름              버린 사실 = 다음 세션에 또 스캔
              skip  = floor 넘어야           잘못 저장 = 랭킹이 가라앉히는 3줄
              (불확실한 skip 은 **보존**)   측정: 저장할 턴이 "skip" 0.16
```

한 줄로: **조종하는 판단에는 확신을 요구하고, 되돌릴 수 있거나 시간만 쓰는
판단에는 요구하지 않는다.**

엔진이 실패하면(키 없음 / 타임아웃 / 호출 불가):

```
① ROUTE     → 조종 안 함. 전 툴 허용
③ SCOPE     → 설계 없이 진행
④ ESCALATE  → 초안이 그대로 답
⑥ SAVE      → 저장 안 함
⑤ SAFETY    → **사람에게 묻는다** (승인자 없으면 거부)

= 기본 모드로 돌던 턴과 똑같아진다.
  ⑤ 만 방향이 반대 — 안전의 기본값은 "막는 쪽"
```

### 2.2 사람이 끼어드는 두 지점

**게이트** (`ChatSession.GateAsync`) — 명령 실행 전.

```
명령
 ├─ CommandRisk 정적 패턴 (rm -rf /, sudo, format, 파이프 설치, force-push…)
 │    → 무조건 사람에게. 엔진이 뒤집을 수 없다
 ├─ 엔진의 안전 질문 → 확신한 safe 만 무통과
 └─ 그 외 전부 → Approver
      Approver 가 없으면 → **거부** (정전 I-6)
      REPL 은 한 줄 읽고, 윈도우는 입력줄에 턴을 세우고, run 은 --yes 없으면 거부
```

**일시정지** (`Agent/PauseGate.cs`) — Esc.

```
Esc → 다음 스텝 경계에서 정지 (모델이 문장 쓰는 중엔 못 멈춘다)
 │
 └─ 입력한 줄을 resume / stop / refine 로 해석
      엔진 있으면 PauseVerdictAsync, 없으면 JudgePauseLine 의 단어 목록
      ├ resume → 그냥 계속
      ├ stop   → _turnCts 만 취소 → **그 턴만** 끝난다
      └ refine → "[the user, mid-turn] …" 로 모델 앞에 놓고 재개
```

---

## 3. 바깥 — 액터 껍질 (`Actors/`)

AgentZero 본체의 Bot / Loop 쌍을 그대로 옮겼다 (Akka.NET 1.6 nightly).

```
/user/bot           AgentBotActor   게이트웨이
  │                   · AgentLoopActor 를 늦게 생성
  │                   · 실행 중 두 번째 StartAgentLoop → TurnRefused
  │                   · 렌더러 콜백 보관 (SetAgentLoopCallbacks)
  └─ /loop          AgentLoopActor  Idle ⇄ Running, 두 상태뿐
                      · 턴은 Task.Run + PipeTo 로 풀에서
                      · 세션 이벤트는 private 내부 레코드로 메일박스를 거쳐
                        부모에게 → 부모는 한 스레드의 순서 보장된 스트림만 본다
                      · 취소는 _cts.Cancel() 뿐. 상태 복귀는 태스크 종료가 한다
                      · StartAgentLoop 하나당 AgentLoopResult 정확히 하나 (I-3)
```

메시지 어휘는 AgentZero 의 것을 쓴다. `AgentLoopProgress`(단계 틱)와
`AgentLoopResult`(런 종료)는 **절대 서로 대신 쓰지 않는다**. agent-one 이 더한 것은
`AgentLoopNotice` 와 `PersonNeeded` ↔ `ResolvePause`(승인·설계 선택의 왕복).

### 하드 룰 — 봇 스레드에서 그리지 않는다 (정전 I-4)

```
봇 콜백        → 큐에 넣기만
펌프 태스크    → 순서대로 이벤트 발행 (자기 스레드에서)
ChatTuiViewModel → Termina 루프에 post (ReactiveViewModel.Post)
```

> 측정: 봇 스레드에서 직접 올렸더니 윈도우의 첫 리페인트가 Termina 루프와
> 데드락 났다. 턴이 안 돌아오고 키도 안 읽혔다 — 사람 눈에는 "요청 중 먹통".

`ChatTuiOverActorsTests` 가 실제 헤드리스 윈도우를 게이트웨이 위에 띄우고
턴이 끝난 뒤 키가 더 필요 없음을 검증한다.

`AgentActorSystem` 은 HOCON 담당: Akka 자체 로그는 **stderr** 로, `exit-clr = off`
(종료 코드는 커맨드가 가진다). AOT 에는 `TrimmerRootAssembly Include="Akka"` 가
필수다 (정전 **I-9**) — 없으면 published 바이너리가 `ActorSystem.Create` 에서
죽는다.

---

## 4. 한 파이프라인, 두 얼굴

```
agent-one chat  (터미널)  → Termina 윈도우  ┐
agent-one chat  (파이프)  → 플레인 REPL     ├─► 전부 ChatSession 하나
agent-one run             → 한 턴           │
agent-one session serve   → 파이프 위 서버  ┘
```

**턴 규칙은 ChatSession 에만 넣는다.** 렌더러에 넣으면 한쪽 얼굴에만 있는
규칙이 된다. `IAgentSession` 을 `ChatSession` 과 `AgentGateway`(액터 경유)가
모두 구현하므로, 테스트와 셀프테스트는 액터 없이도 같은 루프를 몬다.

---

## 5. 코드 위치

| 무엇 | 어디 |
|---|---|
| 스텝 루프 · 봉투 · 가드 | `Agent/AgentLoop.cs`, `Agent/AgentLoopGuards.cs`, `Agent/ToolCall.cs` |
| 턴 파이프라인 | `Agent/ChatSession.cs` |
| 스마트 모드 질문·floor | `Agent/SmartRouter.cs` (질문이 전부 `const string`) |
| 추론 모델 왕복 | `Agent/ReasoningSubtask.cs` |
| 게이트 | `ChatSession.GateAsync` + `Agent/CommandRisk.cs` |
| 일시정지 | `Agent/PauseGate.cs` |
| 액터 | `Actors/AgentBotActor.cs`, `AgentLoopActor.cs`, `AgentGateway.cs` |
| 툴 카탈로그 (단일 진실원) | `Tools/ToolCatalog.cs` → `Agent/SystemPrompt.cs` 가 생성 |
| 그래프 기억 | `Agent/GraphMemory.cs`, `Agent/KnowledgeDistiller.cs`, `Graph/` |

## 6. 고치기 전에 읽을 것

이 루프에는 **정전(canon)** 이 있다:
`harness/knowledge/agent-loop-auditor/agent-loop-invariants.md` 의 I-1 … I-10.
각 항목은 규칙 · 그것을 만든 실제 사고 · 실행 가능한 검증 한 줄을 갖는다.
`에이전트 루프 점검해` 로 감사관을 부르면 세 호스트에 대해 전부 걷는다.

특히 다음을 바꾸기 전에는 해당 항목을 먼저 읽는다:

- 툴 결과가 대화에 되돌아가는 방식 → **I-1**
- 파서 실패 처리 → **I-2**
- 액터의 상태 전이 · 취소 → **I-3**
- 콜백이 렌더러에 닿는 경로 → **I-4**
- 가드 문구 → **I-5**
- 게이트 순서 · 기본값 → **I-6**
- 경로 해석 · 권한 → **I-7**
- verb 추가 → **I-8**
- JSON 직렬화 · 트리밍 → **I-9**
