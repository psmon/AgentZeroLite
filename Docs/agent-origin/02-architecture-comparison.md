# 02 — 아키텍처 비교 (구조 다이어그램 + 분기 사유)

> 본 문서는 두 프로젝트의 핵심 시스템(액터/LLM/터미널/IPC/UI)을 다이어그램 단위로 비교하고,
> 각 분기점에서 *어느 쪽이 어떤 트레이드오프로 그 길을 택했는지* 분석한다.

---

## 1. 액터 토폴로지

### 1.1 양쪽 공통 골격

```
/user
  └── /user/stage                       StageActor       (감독자, 메시지 브로커)
        ├── /user/stage/bot             AgentBotActor    (Chat/Key/AI 모드)
        │     └── /user/stage/bot/{...} (추론 자식)
        └── /user/stage/ws-{name}       WorkspaceActor   (워크스페이스당 1개)
              └── /user/stage/ws-{name}/term-{id}  TerminalActor (세션 1개당)
```

### 1.2 추론 자식 액터 — 핵심 분기점

#### Origin: `ReActActor` (성숙한 5상태 머신)

```
                  ┌──── Idle ────┐
                  │              │
            StartReAct          CompletionSignal
                  │              │
                  ▼              │
            ┌── Thinking         │
            │      │             │
            │   tool_call?       │
            │      │             │
       LLM error  ▼              │
            │   Acting ──────────┤
            │      │             │
            │   wait?            │
            │      │             │
            └── Waiting          │
                  │              │
              tool_result        │
                  │              │
                  ▼              │
                Complete ────────┘
```

**특징** (`/d/Code/AI/AgentWin/Project/ZeroCommon/Actors/ReActActor.cs`):
- 5단계 명시적 상태 머신 (`Become()`) + Error 조기종료 경로
- 가드 상수 **11개** 전수 (정의 라인 28-39):

| # | 상수 | 값 | 위반 시 동작 |
|---|---|---:|---|
| 1 | `DefaultMaxRounds` | 10 | 즉시 종료 (Error) |
| 2 | `MaxSameCallRepeats` | 3 | LLM에 *"This exact call ..."* 에러 toolResult 피드백 + `_consecutiveBlockedCalls++` (액터 안 죽음) |
| 3 | `MaxConsecutiveBlocks` | 3 | 즉시 종료 — 위 피드백이 N회 무시되면 차단 (2단 방어) |
| 4 | `MaxLlmRetries` | 1 | transient (502/503/504/timeout/connection reset 문자열 contains) → 재시도, 그 외 즉시 종료. **backoff 없음** (오리진 약점) |
| 5 | `MaxCallsPerRound` | 30 | 라운드당 도구 호출 cap |
| 6 | `MaxAiWaitSeconds` | 25 | AsyncAiTools 초기 대기 (시작값) |
| 7 | `MinAiWaitSeconds` | 5 | 적응형 대기의 **하한** |
| 8 | `AdaptiveReductionSeconds` | 3 | DONE 도착 시 차감량 |
| 9 | `ShellWaitSeconds` | 3 | AsyncShellTools(`term_send_key`) **고정** 대기 |
| 10 | `TimeoutGraceSeconds` | 12 | DONE 미수신 시 1회 추가 대기량 |
| 11 | `MaxAsyncTimeoutRetries` | 1 | 위 grace 발동 횟수 cap |

- **카운터 키**: `funcName + ":" + argsJson` 그대로 (정규화 없음 — 인자 순서/공백 다르면 다른 키, **가드 우회 가능**한 약점)
- **카운터 reset**: 세션 시작 시 1회 (라운드별 reset 없음 — 긴 세션에서 누적 false positive 가능)
- 비동기 도구는 `CompletionSignal` 또는 `TerminalDoneSignal` 메시지로 외부에서 완료 신호 주입
- 동기/비동기 도구는 코드 내 하드코딩 HashSet으로 분류:
  - **AsyncAiTools** (적응형 대기 25→5초): `term_send`, `stage_send`, `meeting_say`
  - **AsyncShellTools** (고정 3초): `term_send_key`
  - **동기**: `term_read`, `window_list`, `window_focus` 등 (대기 0초)
- `stage_send` 라운드당 1회 제한 별도 (메시지 폭주 방지)

**적응형 대기 — 일방향 감소**

```
시작:    _currentAiWaitSeconds = 25
DONE 1회: 25 → 22  (-AdaptiveReductionSeconds=3)
DONE 2회: 22 → 19
...
DONE 7회: 8 → 5
DONE 8회+: 5 (MinAiWaitSeconds 도달, 고정)
```

증가 신호 없음 — 빠른 응답에 점점 적응만 함. 느려지는 터미널엔 회복 안 되는 *알려진 한계*.

#### Lite: `AgentLoopActor` (단순 루프)

```
            ┌── Idle ──┐
            │          │
       StartReAct  CompletionSignal
            │          │
            ▼          │
        Running ───────┘
            │
        (tool loop until max or done)
            │
            ▼
         Done
```

**특징** (`/d/Code/AI/AgentZeroLite/Project/ZeroCommon/Actors/AgentLoopActor.cs`, `Llm/Tools/LocalAgentLoop.cs`, `Llm/Tools/ExternalAgentLoop.cs`):
- 액터는 thin (Idle/Running 2단계만 `Become`). 루프는 `for (iter = 0; iter < MaxIterations; iter++)` (`LocalAgentLoop.cs` 라인 74-139)
- 액터 → `Task.Run` + `PipeTo`로 `RunAsync` 비동기 실행, 콜백을 `Self.Tell`로 메일박스에 푸시
- **현재 가드 (3개)**:
  - `MaxIterations = 12` (`AgentLoopOptions` 라인 300) — 라운드 cap
  - `CancellationToken` (사용자 취소)
  - `KnownTools` 화이트리스트 검증 (라인 109-113) — 알려지지 않은 도구 즉시 break
- **가드 부재** (오리진 대비 무방비):
  - 같은 도구 반복 감지 ❌
  - LLM transient 재시도 ❌ (JSON 파싱 실패 시 즉시 break)
  - 라운드당 도구 호출 cap (for-loop가 라운드당 1도구 강제하므로 의미 자체가 다름)
- **시간 모델 자체가 다름**: 비동기 도구는 모두 `Task<string>` 반환, 루프가 직접 `await`. 외부 완료 신호 없음. LLM이 자발적으로 `wait(seconds=N)` 도구를 호출 (시스템 프롬프트에 가이드)

### 1.3 분기 분석

| 항목 | Origin | Lite | 평가 |
|---|---|---|---|
| 상태 명시성 | 5단계 (`Become`) | 단순 진입/종료 | Origin 우위 (디버깅·로깅 용이) |
| 무한 루프 방어 | 다층 가드 (`MaxSameCallRepeats`, `MaxConsecutiveBlocks`) | LLM 자체 stop 토큰에 의존 | Origin 우위 |
| 비동기 도구 모델 | `AsyncAi`/`AsyncShell`/Sync 3분류 + `CompletionSignal` | 코드 레벨 분기 | Origin 우위 |
| 외부 LLM 호환성 | (Origin은 외부 only) | Local + External 양쪽 지원 | Lite 우위 |
| 코드 복잡도 | 높음 (~수백 줄 상태 머신) | 낮음 (~수백 줄 루프) | Lite 우위 (유지보수) |

**채택 권고** (2026-04-27 정밀 분석 반영):
> **가드 *3종 세트*만 발췌 이식**: `MaxSameCallRepeats` + `MaxConsecutiveBlocks` + `MaxLlmRetries`(External만, backoff 추가).
> 액터 5상태 머신·적응형 대기·`CompletionSignal` 외부 메시지·도구 분류 HashSet은 **이식 거부** — Lite의 `Task.await` 시간 모델과 충돌, 통째 이식은 architectural backslide.
> 카운터 키는 오리진 약점 보강을 위해 **JSON 인자 정규화** 후 비교.
> 상세 분석 + 패치 스케치: [`harness/logs/tamer/2026-04-27-15-00-react-actor-guard-analysis.md`](../../harness/logs/tamer/2026-04-27-15-00-react-actor-guard-analysis.md)
> → P0 (안정성 직결, ~120 LOC, 2~3일)

---

## 2. LLM 게이트웨이 / 추상화

### 2.1 Origin: 외부 단일 추상

```
[Caller]
    │
    ▼
ILlmProvider (interface)
    │
    ├─ ProviderName
    ├─ ListModelsAsync()
    ├─ CompleteAsync(LlmRequest)
    ├─ StreamAsync(LlmRequest)
    └─ SupportsMultimodal(model)
    │
    ▼
LlmProviderFactory
    │
    └─ OpenAiCompatibleProvider (단일 구현체)
              │
              ├─ Ollama          (http://localhost:11434/v1)
              ├─ LM Studio
              ├─ OpenAI          (https://api.openai.com)
              └─ OpenAI Audio    (4o, audio 모델)
```

**특징**:
- 모든 외부 제공자가 OpenAI 호환 REST → 단일 구현체로 처리
- API 키 정책이 제공자별로 다름 (Ollama는 공백, OpenAI/LMStudio는 필수)
- LLamaSharp 제거 후 **온디바이스 LLM 부재** — 외부 의존 100%

### 2.2 Lite: Local + External Hybrid Gateway

```
[Caller]
    │
    ▼
LlmGateway (단일 진입점)
    │
    ├─ IsActiveAvailable()  ── 네트워크 프로브 없이 즉시 응답 가능 여부
    └─ OpenSession()
          │
          ├─ Backend = Local
          │     │
          │     └─ LlmService (state machine)
          │           │
          │           ├─ Unloaded → Loading → Loaded → Unloading
          │           ├─ Vulkan 디바이스 필터링
          │           │     ├─ GGML_VK_VISIBLE_DEVICES (Env)
          │           │     └─ _putenv_s (CRT 캐시 동기화)
          │           └─ LlamaSharpLocalLlm
          │                 │
          │                 ├─ Custom RID (win-x64-cpu / win-x64-vulkan)
          │                 └─ NativeLibraryConfig.WithLibrary()
          │
          └─ Backend = External
                │
                └─ ExternalChatSession
                      │
                      └─ OpenAiCompatibleProvider
                            ├─ Webnori   (a2.webnori.com)  ← 기본
                            ├─ OpenAI
                            ├─ LMStudio
                            └─ Ollama
```

**Lite 단독 강점**:
- `LlmGateway`로 Local/External 호출자가 동일 API 사용
- `IsActiveAvailable()`로 즉시 가용성 체크 (네트워크 프로브 X)
- Vulkan 환경변수를 **이중 소스(.NET Env + CRT _putenv_s)** 로 일치시켜 multi-GPU 환경 안정화
- LLamaSharp 0.26.0 ABI에 맞춘 self-built llama.cpp commit `3f7c29d` (Gemma 4 호환)

### 2.3 모델 카탈로그

| 모델 | Origin | Lite | 비고 |
|---|:---:|:---:|---|
| Gemma 4 E4B (Q4_K_XL) | ❌ | ✅ | Lite 기본, ~5.1GB, GBNF function-calling |
| Gemma 4 E2B (Q4_K_XL) | ❌ | ✅ | ~3.2GB |
| Llama-3.1 Nemotron Nano 8B v1 | ❌ | ✅ | ~5.0GB, native tool-calling |

> Origin은 외부 호스팅 모델만 카탈로그화. Lite의 GGUF 카탈로그는 `LlmModelCatalog.cs`에 명시.

### 2.4 분기 분석

| 항목 | Origin | Lite | 평가 |
|---|---|---|---|
| 외부 백엔드 추상 | `ILlmProvider` | `LlmGateway` (Local 포함) | 동등 (목적이 다름) |
| 온디바이스 LLM | ❌ (제거됨) | ✅ (self-built) | Lite 우위 |
| Hybrid 진입점 | ❌ | ✅ (`LlmGateway`) | Lite 우위 |
| Vulkan multi-GPU | ❌ (해당 없음) | ✅ (`GGML_VK_VISIBLE_DEVICES`) | Lite 우위 |
| Webnori 기본 백엔드 | ❌ | ✅ | Lite 단독 |
| 음성 모델 (Audio) | ✅ (OpenAI Audio, GPT-4o audio) | ❌ | Origin 우위 — Speech 모듈 일부 |

**채택 권고**:
> **Lite의 LLM 스택을 유지** — Origin이 포기한 것을 되살릴 이유 없음.
> 다만 Origin의 `OpenAI Audio` 추상 패턴은 Lite의 음성 통합 시 참고. → P3

---

## 3. 터미널 세션 / ConPTY 통합

### 3.1 양쪽 공통

```
ITerminalSession (ZeroCommon, headless-friendly)
    │
    ├─ SessionId / InternalId / IsRunning
    ├─ Write / WriteAndSubmit / WriteAsync
    ├─ SendControl(TerminalControl)
    ├─ event OutputReceived
    └─ ReadOutput / GetConsoleText
        │
        ▼ implementation
ConPtyTerminalSession (WPF, Project/AgentZeroWpf/Services/)
    │
    ├─ TermPTY 래핑 (EasyWindowsTerminalControl)
    ├─ 50ms 폴링 → OutputReceived 발행
    ├─ Channel<WriteRequest> (cap=64) 백프레셔
    ├─ 적응형 청크 (200자 이상 분할, 50ms gap)
    └─ PtyRefHash (중복 바인드 방지)
```

### 3.2 차이점

| 항목 | Origin | Lite |
|---|---|---|
| ITerminalSession 시그니처 | 동일 | 동일 |
| ConPtyTerminalSession 구현 | 동일 패턴 | 동일 패턴 |
| `TerminalActor` 모드 자동 전환 | `PlainCli ↔ AiAgent` 자동 (정규식: `(?:claude\|anthropic\|Claude Code\|copilot\|codex\|gemini)\s*[>❯]\|╭─\|❯\s*$`) | (확인 필요) |
| 터미널 출력 throttling | 50ms 폴링 (이전 1500ms DispatcherTimer 개선) | 동일 |

### 3.3 분기 분석

이 영역은 양쪽이 거의 동일. **분기 사유: 없음**. Origin의 `TerminalActor` 정규식 자동 전환 패턴을 Lite가 동일하게 보유하는지만 검증 필요.

---

## 4. CLI / IPC 파이프라인

### 4.1 양쪽 공통 골격

```
[CLI 프로세스]                                  [GUI 프로세스]
    │                                              │
    ▼                                              │
AgentZeroLite.exe -cli <cmd>                       │
    │                                              │
    1. 인자 파싱 + AttachOrAllocConsole            │
    2. FindWindow("AgentZero[Lite]")               │
    3. WM_COPYDATA 전송 (마커 0x4147 / 0x414C)     │
    │  (JSON payload + sessionId)                  │
    │ ─────────────────────────────────────────►   │
    │                                              │ MainWindow.WndProc
    │                                              │   ↓
    │                                              │ CliTerminalIpcHelper.OnCopyData
    │                                              │   ↓
    │                                              │ 명령 dispatch
    │                                              │   ↓
    │                                              │ Memory-Mapped File 응답 작성
    │                                              │ (이름: AgentZero*.{sessionId})
    4. MMF 폴링 (300ms × 5초)                      │
    │ ◄─────────────────────────────────────────── │
    5. JSON 결과 stdout 출력 + exit code           │
```

### 4.2 차이점

| 항목 | Origin | Lite |
|---|---|---|
| WM_COPYDATA 마커 | `0x4147` (`GA`) — 이전 프로젝트명 잔재 | `0x414C` (`AL`) |
| MMF 응답 작성자 | `IpcMemoryMappedResponseWriter`(추정) | `IpcMemoryMappedResponseWriter` |
| 응답 prefix | `AgentZeroWpf.*` (추정) | `AgentZeroLite_*` |
| 명령 표면 | 데스크톱 자동화 풀세트 | 핵심 IPC만 |

### 4.3 데스크톱 자동화 명령 (Origin 단독)

```
[CLI 명령군 — Lite에 없음]

UI Automation:
  text-capture       — 활성 윈도우 텍스트 추출 (CancellationToken 지원)
  scroll-capture     — 스크롤 가능한 영역 통합 캡처
  element-tree       — UI 트리 dump

Mouse/Keyboard:
  mousemove <x,y>
  mouseclick <x,y> [left|right|middle]
  mousewheel <delta>
  keypress <key> [modifiers]

Window:
  get-window-info    — 활성 윈도우 핸들 정보
  wininfo-layout     — 윈도우 배치 dump
  dpi                — DPI 정보
  capture <rect>     — 화면 영역 캡처
  screenshot         — 풀스크린 캡처
  activate <hwnd>    — 윈도우 활성화

Misc:
  copy <text>        — 클립보드 복사
  open-win / close-win  — 윈도우 열기/닫기
  console            — 콘솔 부착
```

### 4.4 분기 분석

| 항목 | Origin | Lite | 평가 |
|---|---|---|---|
| IPC 코어 | 동일 (WM_COPYDATA + MMF) | 동일 | — |
| 데스크톱 자동화 표면 | 풀세트 (~15 명령) | 핵심 IPC 6개 | Origin 우위 — *기능*, Lite 우위 — *경량성* |
| LLM 도구로 노출 | 가능 (Origin 자체 ReAct 도구로 사용) | 미노출 | Origin 우위 |

**채택 권고**:
> **Origin 자동화 명령군을 Lite의 별도 모듈(`AgentZeroWpf.Automation`)로 이식.**
> 단, 핵심 인스톨러에는 옵션 빌드로 분리 — 보안 리뷰(security-guard) 통과 후 LLM 도구로 노출.
> → P1 (외부 도구 표면 확장 → ReAct 능력 확장)

---

## 5. UI 호스트 / 윈도우 토폴로지

### 5.1 Origin: 멀티 윈도우 데스크톱 허브

```
MainWindow (~600x500)
    │
    ├─ Sidebar:  CLI+, 워크스페이스 그룹, Clipper, Bot, Settings
    ├─ TabBar:  멀티탭
    ├─ TerminalPanel:  EasyWindowsTerminalControl 호스팅
    ├─ LogRow:  3탭 (Bot / Output / Log) — Ctrl+Shift+1/2/3
    ├─ BotDockHost:  AgentBotWindow embed (높이 280, Ctrl+Shift+`)
    └─ ScrapPanel:  윈도우 스파이 + 캡처 결과

Detached Windows:
    AgentBotWindow         (420x600, 3모드 CHT/KEY/AI)
    NoteWindow             (1100x700, 파일 브라우저 + 문서 뷰어)
    HarnessMonitorWindow   (750x650, 하네스 설정 + AI 어시스트)
    CliDefEditWindow       (420x400)
    TerminalTestWindow     (테스트용)

External Service:
    IVirtualDesktopService — Windows Virtual Desktop API COM 래핑
```

### 5.2 Lite: 핵심 셸 + 봇

```
MainWindow
    │
    ├─ TabBar / Workspace
    ├─ ConPtyTerminalSession 호스팅
    └─ (Embedded BotDockHost는 확인 필요)

AgentBotWindow
    └─ Chat / Key / AI 3모드

Markdown/Pencil 미리보기:
    MarkdownViewer + MermaidRenderer + PencilRenderer + WebView2
```

### 5.3 차이점 요약

| UI 요소 | Origin | Lite | 채택 검토 |
|---|:---:|:---:|---|
| MainWindow + TabBar | ✅ | ✅ | — |
| AgentBotWindow (Chat/Key/AI) | ✅ | ✅ | — |
| BotDockHost embed/floating 토글 | ✅ (Ctrl+Shift+\`) | (확인 필요) | P2 |
| LogRow 3탭 (Bot/Output/Log) | ✅ | (확인 필요) | P2 |
| NoteWindow | ✅ | ❌ | 보류 (Lite 정체성과 거리) |
| HarnessMonitorWindow | ✅ | ❌ | 보류 — Lite는 CLI 기반 하네스로 충분 |
| ScrapPanel | ✅ | ❌ | P3 (자동화 명령과 함께) |
| VirtualDesktop API | ✅ | ❌ | **P1** (멀티 데스크톱 워크스페이스에 직결) |
| Mermaid/Pencil/WebView2 | (확인 안됨) | ✅ | Lite 우위 |

---

## 6. 빌드 / 배포 파이프라인

### 6.1 양쪽 공통

```
Git tag v* push
    │
    ▼
GitHub Actions release.yml
    │
    1. .NET 10 preview SDK setup
    2. dotnet restore
    3. dotnet publish -c Release -r win-x64 --self-contained
    4. Compress-Archive → ZIP
    5. iscc /DAppVersion=... → Inno Setup
    6. GitHub Release 생성 (ZIP + Setup.exe 첨부)
    │
    ▼
사용자 다운로드
```

### 6.2 차이점

| 항목 | Origin | Lite |
|---|---|---|
| 코드 사이닝 | `.cer` 존재, CI 미사용 | `.cer` 미발견 |
| 다국어 인스톨러 | English + publish/에 16개 언어 잔재 | English + Korean 명시 |
| Workflow 트리거 | tag만 | tag + manual workflow_dispatch |
| 산출물 명명 | `AgentZero-v{ver}-Setup.exe` | `AgentZeroLite-v{ver}-Setup.exe` |

### 6.3 분기 분석

이 영역은 명명·자산 차이만 있고 파이프라인 구조는 동일. **분기 사유: 없음**.

> **Lite의 manual `workflow_dispatch` 트리거는 Origin이 채택할 가치 있음** (역방향 채택).

---

## 7. 하네스 (Kakashi) 디자인

### 7.1 Origin: 도메인 전문가 정원

```
AgentZero Flow (v1.5.1)
    │
    ├─ tamer                          ── 정원지기 (메타)
    ├─ wpf-engineer                   ── WPF/XAML 전문
    ├─ conpty-engineer                ── ConPTY 전문
    ├─ ipc-engineer                   ── WM_COPYDATA/MMF 전문
    ├─ automation-engineer            ── UI Automation 전문
    ├─ llm-engineer                   ── LLM/도구 호출 전문
    ├─ native-efficiency-auditor      ── DLL/메모리/성능
    ├─ nondev-usability-evaluator     ── 비개발자 UX
    └─ chakra-auditor                 ── (특수 감사 — 내용 미확인)

Engines:
    full-inspection
    targeted-review
    native-automation-audit
    usability-journey-audit

Storage:
    data-base/harness-note.db (SQLite WAL)  ── 평가 노트 영속화
```

### 7.2 Lite: QA 정원

```
AgentZero Lite Harness (v1.1.2)
    │
    ├─ tamer            ── 정원지기 (메타)
    ├─ security-guard   ── prompt injection · 보안
    ├─ build-doctor     ── 빌드 · 릴리스 파이프라인
    ├─ test-sentinel    ── 테스트 실행 · 커버리지
    └─ code-coach       ── 코드 품질 · 리팩토링

Engines:
    release-build-pipeline   ── security-guard → build-doctor 순차
    pre-commit-review        ── 커밋 전 코드 리뷰

Storage:
    (별도 DB 없음, MD 파일 로그)
```

### 7.3 분기 분석

| 항목 | Origin | Lite | 평가 |
|---|---|---|---|
| 에이전트 수 | 9 | 5 | Origin 우위 (커버리지) |
| 도메인 전문가 | 6명 | 0명 | Origin 우위 |
| QA 전문가 | 0명 (chakra-auditor만 부분) | 4명 | Lite 우위 |
| 엔진 정교성 | full-inspection / targeted-review / native-automation-audit / usability-journey-audit | release-build-pipeline / pre-commit-review | Origin 우위 (점검), Lite 우위 (자동화 통합) |
| 노트 영속화 | SQLite WAL | MD 파일만 | Origin 우위 — 검색·분석 |
| 자동 트리거 | (확인 필요) | release/commit 자동 통합 | Lite 우위 |

**채택 권고**:
> **Origin의 도메인 전문가 5명(`wpf-engineer`, `conpty-engineer`, `ipc-engineer`, `llm-engineer`, `native-efficiency-auditor`)을 Lite에 추가.**
> → md 파일 복사만으로 가능 (코드 변경 무관). P2.
>
> **Origin의 `data-base/harness-note.db` 패턴은 보류** — Lite의 MD 로그가 git diff 친화적.

---

## 8. Speech / Voice 파이프라인 (Origin 전용)

### 8.1 Origin Speech 스택

```
[마이크 입력]
    │
    ▼
NAudio (마이크 캡처, RMS 진폭)
    │
    ▼
VAD (Voice Activity Detection, 자체 구현)
    │
    ▼
Whisper.net (whisper.cpp 바인딩)
    │
    ├─ Whisper.net.Runtime           (CPU)
    └─ Whisper.net.Runtime.Cuda      (GPU, graceful fallback)
    │
    ▼
[STT 결과 텍스트]
    │
    ▼
AgentBotActor.UserInput  ──→  ReAct 루프
    │
    ▼
[봇 응답 텍스트]
    │
    ▼
System.Speech.Synthesis (TTS, Windows native)
    │
    ▼
[스피커 출력]
```

### 8.2 분기 분석

Lite에는 이 파이프라인 전체가 부재. **채택 시 publish 사이즈 +50MB 정도**, CUDA runtime은 옵션 패키지로 분리 가능.

**채택 권고**:
> **Speech 모듈을 별도 NuGet 의존 그룹으로 통합 — 인스톨러에서 "Voice Pack" 옵션 체크박스로 분리.**
> 코어 Lite의 경량성을 해치지 않으면서 음성 입력/출력 옵션 제공.
> → P1 (입력) + P3 (출력)

---

## 9. 통합 아키텍처 요약 — One Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        AgentZeroLite (현재)                       │
├─────────────────────────────────────────────────────────────────┤
│  [WPF UI: MainWindow, AgentBotWindow]                             │
│       │                                                           │
│  [ActorSystem]                                                    │
│       └─ StageActor                                               │
│             ├─ AgentBotActor (UI 게이트웨이)                       │
│             │     └─ AgentLoopActor (단순 루프, /bot/loop)  ◄───── │ ★ Origin ReActActor 가드 이식 (P0)
│             └─ WorkspaceActor                                     │
│                   └─ TerminalActor → ConPtyTerminalSession        │
│       │                                                           │
│  [LlmGateway]  ◄── Local (LLamaSharp+self-built) + External       │
│       │                                                           │
│  [EF Core SQLite] ── AgentZeroLite/agentZeroLite.db               │
│       │                                                           │
│  [WM_COPYDATA "AL" + MMF]                                         │
│       │                                                           │
│  [Inno Setup + GitHub Actions]                                    │
│                                                                   │
│  Missing (Origin 보유):                                           │
│       ◄── Speech (Whisper.net + NAudio + System.Speech) [P1+P3]   │
│       ◄── Desktop Automation CLI (UIA + mouse + keyboard) [P1]    │
│       ◄── IVirtualDesktopService [P1]                             │
│       ◄── NoteWindow / HarnessMonitorWindow [보류]                │
│       ◄── 도메인 전문가 5명 (wpf/conpty/ipc/llm/native) [P2]      │
│       ◄── BotDockHost embed/float 토글 [P2]                       │
│       ◄── LogRow 3탭 (Bot/Output/Log) [P2]                        │
└─────────────────────────────────────────────────────────────────┘
```

---

## 10. 종합 평가

| 영역 | Origin 우위 | Lite 우위 | 채택 방향 |
|---|:---:|:---:|---|
| 액터 ReAct 안정성 | ✅ (5상태 머신, 가드 다층) | | **Origin 패턴 이식** |
| LLM 게이트웨이 | | ✅ (Local+External hybrid) | Lite 유지 |
| 온디바이스 LLM | | ✅ (self-built) | Lite 유지 |
| Speech 파이프라인 | ✅ | | Origin 모듈 이식 |
| ConPTY 통합 | (동등) | (동등) | — |
| CLI/IPC 코어 | (동등) | (동등) | — |
| CLI 자동화 표면 | ✅ | | Origin 명령군 이식 |
| WPF UI 풍부함 | ✅ | | 선별 채택 |
| Markdown/Mermaid 임베드 | (확인 안됨) | ✅ | Lite 유지 |
| EF Core 마이그레이션 | | ✅ (정리됨) | Lite 유지 |
| 영속 엔티티 풍부함 | ✅ (LlmSettings 등) | | Origin 일부 이식 |
| 하네스 정원 | ✅ (도메인 전문가) | ✅ (QA) | 합집합 채택 |
| 빌드 파이프라인 | (동등) | ✅ (manual trigger) | (역방향 보완) |
| 다국어 | ✅ (RESX 잠재) | ❌ (하드코딩) | Origin 패턴 채택 |

다음: [`03-adoption-recommendations.md`](./03-adoption-recommendations.md) — 우선순위별 채택 로드맵.
