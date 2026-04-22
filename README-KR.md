# AgentZero Lite

**AI 시대를 위한 미니멀 IDE — 여러 개의 CLI를 한 화면에서 나란히 다루세요.**

> 영문 원본: [README.md](README.md)

![AgentZero Lite — multi-CLI multi-view](Home/main.png)

같은 워크스페이스에서든 다른 워크스페이스에서든, CLI로 돌아가는 AI(Claude,
Codex, 종류 무관)에게 명령 하나를 바로 꽂아 넣을 수 있습니다. 서로 다른 모델의
AI들을 동시에 띄워두고, 같은 경로로 둘을 대화시킬 수도 있습니다 — 별도 중계
서버나 커스텀 브로커 없이, 모델 간 크로스 대화.

AgentZero Lite는 단순한 아이디어로 만들어진 Windows 데스크톱 셸입니다. AI 시대에
하루의 대부분은 *커맨드라인 도구와 대화하는 시간*입니다. `claude`, `codex`, `gh`,
`docker`, `pwsh`, REPL, 빌드 로그 tail — 각각 자기만의 터미널을 원하고, 사용자는
이 모두를 창을 옮기지 않고 동시에 보고 싶습니다. AgentZero Lite는 진짜 멀티탭·
멀티워크스페이스 ConPTY 터미널과, 포커스된 터미널로 텍스트·스킬 매크로를 전달하는
작은 채팅 창을 제공합니다. 그 이상도 이하도 아닙니다.

---

## 주요 기능

- **멀티탭 ConPTY 터미널** — 각 탭이 진짜 `conhost` 렌더러로 돌아갑니다(의사
  PTY 흉내가 아님). `EasyWindowsTerminalControl` / `CI.Microsoft.Terminal.Wpf`
  기반.
- **워크스페이스** — 탭을 폴더 단위로 묶어 프로젝트마다 별도 CLI 세트를 유지합니다.
  워크스페이스 버튼 한 번으로 `cd` 컨텍스트와 새 Claude가 함께 뜹니다.
- **AgentChatBot** — 도킹 가능한 채팅 패널. 입력한 텍스트를 **현재 활성 터미널**로
  전달합니다. `CHT` 모드는 텍스트를, `KEY` 모드는 순수 키스트로크(Ctrl+C, 화살표,
  Tab 등)를 보냅니다. AI가 아니라 **입력 브로커**입니다.
- **AI ↔ AI 대화 (핵심 기능)** — `AgentZeroLite.ps1`을 Claude 탭이나 Codex 탭에
  **딱 한 번 가르치면** 그 뒤로 한쪽 AI가 다른 터미널을 *이름으로 불러* 대화를
  시작할 수 있습니다. 탭 0의 Claude가 탭 1의 Codex에게 말을 걸고, Codex가
  답장을 보내고, 각자 `terminal-read`로 상대 출력을 읽습니다. 중간 브로커도, 클라우드
  중계도 없이, AgentZero의 IPC만 거칩니다. Lite 에디션이 존재하는 이유인, 모델들
  사이의 티키타카입니다.
- **Skill Sync `/` 매크로** — Claude가 탭에서 돌아가는 중에 `[+] → Skill Sync`를
  누르면 Claude 자신의 `/skills` 화면에서 스킬 목록을 읽어와 채팅창의 `/` 슬래시
  메뉴로 만듭니다. `/`를 입력하고 스킬을 골라 Enter — 매크로 텍스트가 터미널로
  쏴집니다. LLM 라운드트립 없음.
- **Note 패널** — 마크다운/Mermaid 다이어그램/Pencil 파일을 렌더링하는 보조
  패널. 현재 워크스페이스 폴더 범위로 동작합니다.
- **CLI 원격 제어** — `AgentZeroLite.exe -cli terminal-send 0 0 "npm test"`를
  스크립트 어디서든 실행해 GUI를 `WM_COPYDATA` + 메모리 맵 파일로 제어합니다.
- **액터 모델 (Akka.NET)** — 터미널 생명주기, 워크스페이스 라우팅, 채팅 입력을
  감독받는 액터로 처리합니다. 한 세션이 죽어도 창 전체가 내려가지 않습니다.
- **실행 파일 하나, 프로세스 하나** — 단일 인스턴스 가드, 설정은 SQLite, .NET 10
  런타임 외 의존성 없음. 빌드 크기 ~60 MB.

---

## 메탈 모델

```
+--------------------------------------------------------------------------+
| AgentZero                                                    -  □  ×    |
+---+------------+-----------------------------------------------+--------+
|   | WORKSPACES | [Claude1] [pwsh1] [build-log] [+]            |        |
| ⚙ | ▸ monorepo +-----------------------------------------------+        |
| 🤖 |   ▸ web    |                                              |        |
|   |   ▸ api    |           ConPTY 터미널 (활성 탭)              |        |
|   | ▸ blog     |                                              |        |
|   |            |                                              |        |
|   | SESSIONS   +-----------------------------------------------+        |
|   |  · Claude1 | AGENT BOT ▾ | OUTPUT | LOG | NOTE                    |
|   |  · pwsh1   +-----------------------------------------------+        |
|   |            |  > /skills                                    |        |
|   |            |  [스킬 목록]                                    |        |
|   |            |  > 테스트 돌리고 요약해줘                    [Send]  |
+---+------------+-----------------------------------------------+--------+
```

상단: 탭마다 ConPTY 터미널. 좌측: 액티비티 바 + 워크스페이스/세션 사이드바.
하단: 탭 전환형 패널 — AGENT BOT(활성 터미널로 텍스트/키 전송), OUTPUT, LOG,
NOTE(워크스페이스별 마크다운 뷰어).

---

## 아키텍처

```
┌─ AgentZeroWpf (WinExe, WPF, net10.0-windows) ───────────────────────────┐
│                                                                         │
│  MainWindow  ──── N개 ConPTY 탭 호스팅  ──── AgentBotWindow (dock/float)│
│      │                                              │                   │
│      │  WM_COPYDATA + MMF  <─  CliHandler.cs  ──>   │                   │
│      │  (외부 스크립트가 GUI를 구동)                  │                   │
│      ▼                                              ▼                   │
│  ActorSystemManager (Akka.NET)                                          │
└──────────────────────┬──────────────────────────────────────────────────┘
                       │  ProjectReference
┌─ ZeroCommon (ClassLib, net10.0) ────────────────────────────────────────┐
│  Actors/    Stage → Workspace(N) → Terminal(N)  + AgentBot (1)          │
│  Services/  ITerminalSession, AgentEventStream, AppLogger               │
│  Data/      AppDbContext + EF Core (SQLite)                             │
│             CliDefinition / CliGroup / CliTab / ClipboardEntry          │
│  Module/    CliTerminalIpcHelper, CliWorkspacePersistence, ...          │
└─────────────────────────────────────────────────────────────────────────┘
```

`ZeroCommon`은 UI 의존성이 없는 라이브러리로, 자체 헤드리스 테스트 프로젝트
(`ZeroCommon.Tests`, xUnit + Akka.TestKit)가 커버합니다. `AgentTest`는 WPF에
의존하는 영역을 담당합니다.

### 액터 토폴로지

```
/user/stage                  — 최상위 감독자, 1개 인스턴스
    /bot                     — AgentBotActor: 모드(Chat/Key) + UI 콜백
    /ws-<workspace>          — WorkspaceActor: 해당 폴더의 터미널 소유
        /term-<id>           — TerminalActor: ITerminalSession 래핑
```

모든 메시지는 `ZeroCommon/Actors/Messages.cs` 한 곳에 정의돼 있습니다.

---

## 프로젝트 구성

| 프로젝트              | 경로                          | 종류                          | 네임스페이스          |
|----------------------|-------------------------------|-------------------------------|----------------------|
| **AgentZeroWpf**     | `Project/AgentZeroWpf/`       | WinExe (net10.0-windows, WPF) | `AgentZeroWpf.*`     |
| **ZeroCommon**       | `Project/ZeroCommon/`         | ClassLib (net10.0, UI 없음)   | `Agent.Common.*`     |
| **AgentTest**        | `Project/AgentTest/`          | xUnit (net10.0-windows)       | `AgentTest.*`        |
| **ZeroCommon.Tests** | `Project/ZeroCommon.Tests/`   | xUnit (net10.0, 헤드리스)     | `ZeroCommon.Tests.*` |

참조 관계: `AgentTest → AgentZeroWpf → ZeroCommon ← ZeroCommon.Tests`. WPF / Win32
의존성이 없는 코드는 전부 ZeroCommon에 있어야 합니다.

---

## 빌드 & 실행

요구사항: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/), `dotnet`을
실행할 수 있는 터미널. Rider나 Visual Studio 2022 17.11+에서 작업 가능 —
아래 IDE 주의사항을 꼭 읽어주세요.

```bash
# 복원 + WPF 앱 빌드 (ZeroCommon은 프로젝트 참조로 자동 빌드)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# Release 빌드 (CLI 래퍼 스크립트 쓰기 전에 필요)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Release

# GUI 실행
Project/AgentZeroWpf/bin/Debug/net10.0-windows/AgentZeroLite.exe

# 헤드리스 테스트 (공용 로직)
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj

# WPF 의존 테스트 (액터, 터미널 세션, 승인 파서)
dotnet test Project/AgentTest/AgentTest.csproj
```

### ⚠️ IDE 주의사항 — 디버깅 시 Terminal Mode OFF

AgentZero는 WPF 내부에 자기만의 ConPTY 터미널을 호스팅합니다. IDE가 프로세스의
stdin/stdout/stderr에 자기 터미널을 붙이면(Rider 기본값, VS의 "Redirect standard
output", VS Code 내장 터미널로 직접 실행) **ConPTY가 소유해야 할 콘솔 이벤트를
IDE가 가로챕니다**. 탭이 시작되지 않거나 깨진 출력이 보입니다.

**Run / Debug 누르기 전에 IDE의 터미널 붙이기를 반드시 끄세요:**

| IDE            | 설정                                                                      |
|----------------|---------------------------------------------------------------------------|
| **Rider**      | Run / Debug configuration → **Use external console = ON** (`USE_EXTERNAL_CONSOLE=1` in `.run.xml`) |
| **Visual Studio** | 프로젝트 속성 → 디버그 → **"Use the standard console" / "Redirect standard output" 체크 해제** |
| **VS Code**    | `launch.json`에서 `"console": "externalTerminal"` 설정 (`"internalConsole"` 금지) |

요약 — 자식 프로세스에게 진짜 콘솔 창을 주세요. 일반 셸에서 `dotnet run`으로
실행하는 경우는 문제없습니다(IDE가 stdio를 가로채지 않음).

---

## 두 AI CLI가 서로 대화하게 만들기

Lite 에디션의 대표 유스케이스이며 1분이면 세팅됩니다.

1. **CLI 경로 등록 (1회).** Settings → *AgentZero CLI* → `Register PATH`.
   이제 어느 셸에서든 `AgentZeroLite.ps1`이 잡힙니다.
2. **같은 워크스페이스에 AI 탭을 2개 띄웁니다.** 예를 들어 그룹 0 탭 0 = `claude`,
   그룹 0 탭 1 = `codex` (자연어 지시를 받는 AI CLI면 어느 것이든).
3. **각 AI에게 이 도구를 가르칩니다.** 탭마다 아래 한 줄을 붙여넣으세요:
   > `AgentZeroLite.ps1 help`를 학습하고, 터미널 간 대화에 이 도구를 사용해.
   > `terminal-list`로 탭 목록을 보고, `terminal-send <grp> <tab> "텍스트"`로
   > 다른 AI 탭에 이름으로 말을 걸고, `terminal-read <grp> <tab> --last 2000`으로
   > 상대 답변을 읽어.
4. **대화를 시작합니다.** Claude 탭에서 이렇게 말하세요:
   *"Codex라는 이름의 탭에 인사하고, REST 엔드포인트를 함께 설계하자고 제안해."*
   Claude는 `AgentZeroLite.ps1 terminal-send 0 1 "안녕 Codex, ..."`를 실행합니다.
   Codex는 자기 프롬프트에서 그것을 보고 답변을 구성해 `terminal-send 0 0 "..."`로
   보냅니다. 두 탭에서 대화가 실시간으로 흐릅니다.

이 구조가 작동하는 이유:

- 각 AI는 **자기만의 ConPTY**에서 돌아갑니다 — 공유 메모리 없음, 컨텍스트 유출 없음.
- 메시지는 **AgentZero의 IPC**(`WM_COPYDATA` + 메모리 맵 파일)로 이동합니다.
  클라우드 중계 아님 — 어떤 것도 기기를 떠나지 않습니다.
- 탭 레이아웃 덕분에 언제든 끼어들거나 방향을 틀거나 수동 보조를 넣을 수 있습니다.
  인간이 계속 감독자입니다.
- 브로커가 그냥 AI가 이미 이해하는 셸 명령이므로, `claude` 대신 아무 CLI-native
  에이전트(Aider, Copilot, 로컬 `ollama` 챗 등)를 꽂아도 같은 프로토콜이 유지됩니다.

이것이 Lite 에디션이 만들어진 이유인 "모델 간 티키타카"입니다. 터미널 멀티플렉서는
여러 프롬프트를 **볼** 수 있게 해주지만, AgentZero Lite는 그들이 **서로 말할** 수
있게 해줍니다.

---

## CLI — 스크립트에서 GUI 구동

스크립트에서 호출 가능한 모든 동작은 `AgentZeroLite.exe -cli <command>`를 통해
나갑니다. GUI가 이미 떠 있어야 하며, CLI는 `WM_COPYDATA`(marker `0x414C "AL"`)로
명령을 보내고 지정된 메모리 맵 파일로 응답을 받습니다. 기본 5초 폴링 타임아웃이
있고, 응답을 기다리지 않으려면 `--no-wait`를 붙이세요.

| 명령                                | 동작                                                            |
|-------------------------------------|-----------------------------------------------------------------|
| `status`                            | GUI 상태 JSON 덤프 (워크스페이스 수, 상태 바)                  |
| `copy`                              | 최근 클립보드 버퍼를 시스템 클립보드로 복사                    |
| `open-win` / `close-win`            | 메인 창 표시 / 숨김                                             |
| `console`                           | 앱 디렉토리에서 PowerShell 새로 열기                            |
| `log [--last N] [--clear]`          | CLI 액션 히스토리 (파일 기반)                                   |
| `terminal-list`                     | 워크스페이스/탭 세션 전체 JSON 리스트                           |
| `terminal-send <g> <t> "text"`      | 워크스페이스 `<g>`의 탭 `<t>`로 텍스트 전송                     |
| `terminal-key <g> <t> <key>`        | 제어키 전송 (Ctrl+C, Enter, Tab, 화살표, …)                    |
| `terminal-read <g> <t> [-n N]`      | 탭 스크롤백 마지막 N바이트 읽기                                 |
| `bot-chat [--from X] "text"`        | Bot 창에 외부 채팅 버블 표시                                    |
| `help`                              | 명령어 레퍼런스                                                 |

편의용 PowerShell 래퍼가 `Project/AgentZeroWpf/AgentZeroLite.ps1`에 있습니다. 앱
디렉토리가 `PATH`에 등록된 뒤라면 어느 셸에서든 호출 가능합니다. Settings에서
**AgentZero CLI → Register PATH** 버튼으로 등록할 수 있습니다.

---

## 설정

두 개의 탭만 있습니다:

- **CLI Definitions** — AgentZero가 실행할 셸(`cmd`, `pwsh`, `claude …`, 커스텀)을
  등록합니다. 내장 항목은 삭제 불가. 새로 추가하면 모든 워크스페이스의 `+` 메뉴에
  나타납니다.
- **AgentZero CLI** — 원클릭으로 앱 디렉토리를 사용자 `PATH`에 등록합니다. 이후
  `AgentZeroLite.ps1` 및 `AgentZeroLite.exe -cli …`가 어느 셸에서든 동작합니다.
  **AI ↔ AI Talk 섹션**에는 Claude/Codex에게 이 CLI를 가르치는 원샷 지시문이
  복사 가능한 형태로 준비돼 있습니다.

저장 위치: `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` (SQLite, 첫 실행 시
EF Core가 자동 마이그레이션).

---

## 상태

**Alpha.** 12개 헤드리스 테스트는 그린. WPF 통합 테스트는 데스크톱 세션에서만
실행되는 opt-in 항목입니다. `ZeroCommon` 내부 API는 v1.0까지 불안정한 것으로
간주됩니다.

---

## 왜 또 다른 터미널인가?

인간이 아니라 AI 코딩 도구가 터미널을 움직이기 시작했기 때문입니다. 유용한 작업
단위는 더 이상 "셸 하나"가 아니라 "그 중 하나가 생각하는 동안 내가 탭으로 왔다갔다
하는 셸 셋"입니다. Windows Terminal, Conemu, Hyper 같은 도구는 **단일 프롬프트**를
최적화합니다. AgentZero Lite는 그 반대를 최적화합니다 — 프로젝트별로 묶인
여러 동시 프롬프트, 옆에 붙은 노트패드, 텍스트 브로커 채팅 패널. 제품의 전부는
그것뿐입니다.

## 로드맵

> **왜 Lite 스탠드얼론 모드부터 Akka.NET인가?**
> 지금은 단일 장치에서 돌아가는 Lite지만, 같은 액터 모델이 **Remote / Cluster**
> 로 자연스럽게 확장됩니다 — 원격 비서 추가, 온디바이스 AI 클러스터까지.
> 장기 로드맵으로 **실험 중**이며, 이 도전이 완성될지는 지켜봐 주세요.
> `LiteMode`는 오픈소스로 공개되어, Akka.NET의 베이직 액터 모델과 함께 멀티뷰 모드로 CLI 제어할수 있어  
> 교보재로도 활용할 수 있습니다.

### AgentZero **PRO** 로드맵

#### 🧩 AkkaStacks — 분산 런타임

| 단계 | 이름 | 설명 |
| --- | --- | --- |
| 1 | **AgentZeroRemote** | 1대의 AgentZero 장치를 원격에서 조작 |
| 2 | **AgentZeroCluster** | N대의 AgentZero를 클러스터화해 다중 이용 |

#### 🧠 LLMStacks — 지능·입출력 확장

| 이름 | 설명 |
| --- | --- |
| **AgentZeroAIMODE** | 온디바이스 모델 탑재 AI 채팅 모드 |
| **AgentZeroVoice** | 음성 입출력 지원 |
| **AgentZeroOS** | OS 네이티브 자동화 지원 |

---

🚧 **준비 중** · <https://blumn.ai/>
