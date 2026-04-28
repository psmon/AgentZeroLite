# 01 — 기술 스택 정밀 비교 (스펙 표)

> 본 문서는 두 프로젝트의 빌드/런타임/패키지/배포 스펙을 항목별로 1:1 대응시킨다.
> 모든 사실은 2026-04-27 시점 코드 기준이며, 근거 파일 경로를 함께 기록한다.

---

## 1. 솔루션 & 프로젝트 토폴로지

| 항목 | Origin (AgentWin) | AgentZeroLite | 비고 |
|---|---|---|---|
| 솔루션 파일 | `AgentWin.slnx` (slnx 신형식) | **없음** (csproj 직접 빌드) | Lite는 IDE 솔루션 없이 작동 |
| 프로젝트 수 | 4 | 5 | Lite에 `LlmProbe` 콘솔 유틸 추가 |
| WPF 호스트 | `Project/AgentZeroWpf/AgentZeroWpf.csproj` | 동일 경로/파일명 | — |
| 공유 라이브러리 | `Project/ZeroCommon/ZeroCommon.csproj` | 동일 | — |
| WPF 의존 테스트 | `Project/AgentTest/AgentTest.csproj` | 동일 | — |
| 헤드리스 테스트 | `Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj` | 동일 | — |
| 추가 콘솔 유틸 | — | `Project/LlmProbe/LlmProbe.csproj` | Lite 단독: GGUF 모델 로드 검증용 |
| Build Configurations | Debug / Release / **AgentCLI** | 동일 (Debug/Release/AgentCLI) | AgentCLI는 csproj에 선언만, 실제 분기는 `-cli` 플래그로 처리 |
| `Directory.Build.props/targets` | 없음 | 없음 | — |
| `global.json` | 없음 | 없음 | preview SDK 강제 안 함 |

---

## 2. 핵심 csproj 메타

### 2.1 AgentZeroWpf (WPF 호스트)

| 항목 | Origin | Lite |
|---|---|---|
| `OutputType` | `WinExe` | `WinExe` |
| `TargetFramework` | `net10.0-windows` | `net10.0-windows` |
| `AssemblyName` | (기본 = `AgentZeroWpf`) | **`AgentZeroLite`** |
| `RootNamespace` | (기본) | (기본 = `AgentZeroWpf`) — 네임스페이스는 유지 |
| `UseWPF` | `true` | `true` |
| `UseWindowsForms` | `true` (제한적, ConsoleHostWindow용) | `true` |
| `Nullable` | `enable` | `enable` |
| `ImplicitUsings` | `enable` | `enable` |
| `AllowUnsafeBlocks` | `true` | `true` |
| `ProjectReference` | `ZeroCommon.csproj` | `ZeroCommon.csproj` |

**차이점 — Lite의 어셈블리명 변경**

Lite는 `AssemblyName=AgentZeroLite`로 산출물 이름을 분리했다. 단, **네임스페이스는 `AgentZeroWpf.*`를 그대로 유지**하여 코드 import 경로는 Origin과 호환. CLAUDE.md에서 명시:
> *"AgentZeroLite.exe is a WinExe (assembly name set in `AgentZeroWpf.csproj`; project/namespace stay `AgentZeroWpf.*`)"*

### 2.2 ZeroCommon (공유 라이브러리)

| 항목 | Origin | Lite |
|---|---|---|
| `TargetFramework` | `net10.0` | `net10.0` |
| `AssemblyName` | **`Agent.Common`** | **`Agent.Common`** |
| `RootNamespace` | `Agent.Common` | `Agent.Common` |
| `IsPackable` | `false` | `false` |
| `InternalsVisibleTo` | (확인 안됨) | **`ZeroCommon.Tests`** | Lite는 internal API 테스트 가시화 |

---

## 3. NuGet 패키지 비교

### 3.1 공통 (양쪽 모두 사용)

| 패키지 | 버전 | 용도 |
|---|---|---|
| Akka | 1.5.40 | 액터 시스템 |
| Akka.DependencyInjection | 1.5.40 | DI 통합 |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.0-preview.3.25171.6 | EF Core SQLite |
| Microsoft.EntityFrameworkCore.Design | 10.0.0-preview.3.25171.6 | 마이그레이션 디자인 타임 |
| Dirkster.AvalonDock | 4.72.1 | 도킹 윈도우 |
| Dirkster.AvalonDock.Themes.VS2013 | 4.72.1 | VS2013 테마 |
| EasyWindowsTerminalControl | 1.0.36 | ConPTY 래퍼 (`TermPTY`) |
| CI.Microsoft.Terminal.Wpf | 1.22.250204002 | DirectX 터미널 렌더러 |
| Microsoft.Web.WebView2 | 1.0.3124.44 | WebView2 (Markdown/Mermaid) |
| AvalonEdit | 6.3.1.120 | 텍스트 에디터 |
| Docnet.Core | 2.6.0 | PDF 렌더링 |
| SharpVectors.Wpf | 1.8.5 | SVG 렌더링 |
| Microsoft.Build | 17.14.28 | (Origin만, 빌드 도구) |
| Microsoft.Build.Tasks.Core | 17.14.28 | MSBuild 태스크 |

### 3.2 Origin 전용 (Lite에 없음)

| 패키지 | 버전 | 용도 | Lite 채택 검토 |
|---|---|---|---|
| **Whisper.net** | 1.9.0 | STT (whisper.cpp 바인딩) | 🟡 P1 — Speech 모듈 |
| **Whisper.net.Runtime** | 1.9.0 | CPU 런타임 | 🟡 P1 |
| **Whisper.net.Runtime.Cuda** | 1.9.0 | NVIDIA GPU 가속 (graceful fallback) | 🟡 P1 |
| **NAudio** | 2.2.1 | 마이크 캡처, RMS VAD | 🟡 P1 |
| **System.Speech** | 10.0.6 | Windows TTS | 🟢 P3 — 짝으로 채택 |

### 3.3 Lite 전용 (Origin에 없음)

| 패키지 | 버전 | 용도 | 채택 사유 |
|---|---|---|---|
| **LLamaSharp** | 0.26.0 | 로컬 LLM 추론 | Origin은 Whisper와 ggml.dll 충돌로 **제거**, Lite는 self-built DLL로 우회 |
| **System.Security.Cryptography.Xml** | 10.0.7 | (PrivateAssets=all) | LLamaSharp 의존성 또는 모델 서명 검증 추정 |

### 3.4 테스트 프로젝트

| 패키지 | Origin | Lite | 비고 |
|---|---|---|---|
| Akka.TestKit.Xunit2 | 1.5.40 | 1.5.40 | — |
| Microsoft.NET.Test.Sdk | 17.14.1 | 17.14.1 | — |
| xunit | 2.9.3 | 2.9.3 | — |
| xunit.runner.visualstudio | 3.1.4 | 3.1.4 | — |
| coverlet.collector | 6.0.4 | 6.0.4 | — |
| **Xunit.SkippableFact** | — | **1.5.23** | Lite만, 환경 의존 테스트 skip 패턴 |

---

## 4. 네이티브 DLL 의존

### 4.1 ConPTY 스택 (양쪽 동일)

| DLL | NuGet 출처 | 버전 (Origin) | 버전 (Lite) | 복사 방식 |
|---|---|---|---|---|
| `conpty.dll` | CI.Microsoft.Windows.Console.ConPTY | (확인 안됨) | **1.22.250314001** | csproj `<Content>` 항목 PreserveNewest |
| `Microsoft.Terminal.Control.dll` | CI.Microsoft.Terminal.Wpf | 1.22.250204002 | 1.22.250204002 | 동일 |

> **CLAUDE.md 경고**: NuGet 버전을 bump하면 csproj의 `$(NuGetPackageRoot)` 하드코딩 경로 두 곳을 함께 갱신해야 한다. 갱신 누락 시 ConPTY 탭이 silent fail.

### 4.2 LLM 네이티브 (Lite 전용)

| 위치 | 내용 | 비고 |
|---|---|---|
| `Project/ZeroCommon/runtimes/win-x64-cpu/native/` | self-built llama.dll (CPU) | llama.cpp commit 3f7c29d, LLamaSharp 0.26.0 ABI 호환 |
| `Project/ZeroCommon/runtimes/win-x64-vulkan/native/` | self-built llama.dll (Vulkan) | Multi-GPU `GGML_VK_VISIBLE_DEVICES` 환경변수 처리 |

> **자동 로드 회피 트릭**: 표준 RID(`win-x64`) 대신 custom RID 폴더(`win-x64-cpu`, `win-x64-vulkan`)를 사용. `NativeLibraryConfig.WithLibrary()`로 `LocalLlmOptions.Backend` 값에 따라 명시 로드 → 동시 로드 충돌 방지.
>
> **Origin은 이 우회를 시도하지 않고 LLamaSharp 자체를 제거**. Lite의 우회 패턴이 더 정교하다.

---

## 5. 데이터베이스 / 영속성

### 5.1 EF Core 설정

| 항목 | Origin | Lite |
|---|---|---|
| EF Core 버전 | 10.0.0-preview.3.25171.6 | 10.0.0-preview.3.25171.6 |
| DBMS | SQLite | SQLite |
| DB 파일 | `%LOCALAPPDATA%\AgentZero\agentZero.db` | `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` |
| 마이그레이션 경로 | `ZeroCommon/Data/Migrations/` | `ZeroCommon/Data/Migrations/` |
| 자동 마이그레이션 | `db.Database.Migrate()` | `db.Database.Migrate()` |
| 빈 폴더 함정 | — | `AgentZeroWpf/Data/Migrations/` 존재(빈 폴더). 스캐폴드 금지 명시 (CLAUDE.md) |

### 5.2 마이그레이션 누적 비교

| 메트릭 | Origin | Lite |
|---|---|---|
| 마이그레이션 파일 수 | **31개** | **1개** (InitialCreate, 2026-04-21) |
| 누적 기간 | 2026-04-04 ~ 2026-04-21 | 2026-04-21 ~ |
| 분기 시점 | (Lite의 InitialCreate가 Origin v4 후속) |

**주요 Origin 마이그레이션 (Lite의 InitialCreate에 통합 또는 미반영)**:

| 마이그레이션 | 내용 | Lite 통합 여부 |
|---|---|---|
| `RenamePsToPw` | PS5/PS7 → PW5/PW7 명칭 통일 | ✅ 통합 |
| `AddLastActiveIndices` | 탭 활성 상태 추적 (`LastActiveGroupIndex/TabIndex`) | ✅ 통합 |
| `AddClipboardEntries` | 클립보드 히스토리 엔티티 | ❌ 미통합 |
| `AddVoiceAudioOutput` | TTS 출력 설정 | ❌ Speech 모듈 부재 |
| `AddPerProviderCredentials` | LLM 제공자별 API 키 분리 | 🟡 Lite는 Webnori/OpenAI/LMStudio/Ollama를 코드에서 분기 (DB 영속화 미확인) |
| `AddSttTtsSettings` | STT/TTS 설정 | ❌ |
| `AddSttUseGpu` | STT GPU 가속 플래그 | ❌ |
| `AddIsBotDocked` | Bot 임베드 상태 | ✅ 통합 |

### 5.3 엔티티

| 엔티티 | Origin | Lite | 비고 |
|---|---|---|---|
| `AppWindowState` | ✅ | ✅ | 윈도우 위치/크기, 탭 활성, OnboardingDismissedVersion |
| `CliDefinition` | ✅ | ✅ | 셸 정의 (CMD/PW5/PW7/Claude) |
| `CliGroup` | ✅ | ✅ | 워크스페이스 그룹 |
| `CliTab` | ✅ | ✅ | 그룹 내 탭 |
| `ClipboardEntry` | ✅ | ❌ | Origin: 클립보드 히스토리 |
| **`LlmSettings`** | ✅ | ❌ | Origin: 모델/API키/MaxTokens 영속화 (1행 싱글톤) |

**Lite 미보유 엔티티 채택 검토**:
- `LlmSettings`: External 백엔드 다양화로 코드 분기 중 → DB 영속화하면 사용자 설정 UX 개선 가능. **P2 채택 후보**.
- `ClipboardEntry`: Lite의 핵심 시나리오와 거리. **보류**.

### 5.4 시드 데이터 (양쪽 동일)

```
CMD  → cmd.exe                                    (IsBuiltIn=true, SortOrder=0)
PW5  → powershell.exe                             (IsBuiltIn=true, SortOrder=1)
PW7  → pwsh.exe                                  (IsBuiltIn=true, SortOrder=2)
Claude → powershell.exe -NoExit -Command claude  (IsBuiltIn=true, SortOrder=maxSort+1, EnsureDefaultCliDefinitions로 동적 추가)
```

---

## 6. CLI / IPC

### 6.1 진입 분기

| 항목 | Origin | Lite |
|---|---|---|
| CLI 트리거 | `args.Contains("-cli")` | `args.Contains("-cli")` |
| 진입 클래스 | `CliHandler.Run()` | `CliHandler.Run()` |
| 종료 호출 | `Environment.Exit(exitCode)` | 동일 |
| 단일 인스턴스 뮤텍스 | `Local\AgentZeroWpf.SingleInstance` | `Local\AgentZeroLite.SingleInstance` |

### 6.2 IPC 메커니즘

| 항목 | Origin | Lite |
|---|---|---|
| 전송 채널 | WM_COPYDATA | WM_COPYDATA |
| 마커 (4 bytes) | `0x4147` ("GA") | `0x414C` ("AL") |
| 응답 채널 | Memory-Mapped File | Memory-Mapped File |
| MMF 이름 prefix | `AgentZeroWpf.*` (추정) | `AgentZeroLite_*` |
| 폴링 간격 | 300ms | 300ms |
| 기본 타임아웃 | 5000ms (5초) | 5000ms (5초) |
| `--no-wait` 옵션 | ✅ | ✅ |
| `--timeout <ms>` | ✅ | ✅ |

### 6.3 CLI 명령 표면

| 카테고리 | 명령 | Origin | Lite |
|---|---|:---:|:---:|
| **공통 인프라** | `help` | ✅ | ✅ |
| | `status` | ✅ | ✅ |
| **터미널 IPC** | `terminal-list` | ✅ | ✅ |
| | `terminal-send` | ✅ | ✅ |
| | `terminal-key` | ✅ | ✅ |
| | `terminal-read` | ✅ | ✅ |
| **봇 IPC** | `bot-chat` | ✅ | ✅ |
| **클립보드** | `copy` | ✅ | ❌ |
| **윈도우 제어** | `open-win` / `close-win` | ✅ | ❌ |
| | `console` | ✅ | ❌ |
| | `activate` | ✅ | ❌ |
| | `screenshot` | ✅ | ❌ |
| **데스크톱 자동화** | `get-window-info` | ✅ | ❌ |
| | `wininfo-layout` | ✅ | ❌ |
| | `dpi` | ✅ | ❌ |
| | `capture <rect>` | ✅ | ❌ |
| | `text-capture` (UI Automation) | ✅ | ❌ |
| | `scroll-capture` | ✅ | ❌ |
| | `element-tree` | ✅ | ❌ |
| | `mousemove` / `mouseclick` / `mousewheel` | ✅ | ❌ |
| | `keypress` | ✅ | ❌ |

> **차이 요약**: Origin은 데스크톱 자동화 풀세트(UI Automation, 마우스/키보드 시뮬레이션, 스크린 캡처)를 CLI로 노출. Lite는 핵심 IPC만 유지.
>
> **채택 검토**: 자동화 명령은 LLM의 외부 도구로 노출하면 ReAct 액션 표면이 크게 확장됨. **P1 채택 후보** — 단, 영향 범위가 커서 별도 모듈화 필요.

### 6.4 PowerShell 래퍼

| 파일 | Origin | Lite | 차이 |
|---|---|---|---|
| CLI 래퍼 | `AgentZero.ps1` | `AgentZeroLite.ps1` | 동일 패턴 (`Start-Process -NoNewWindow -Wait`) |
| GUI 래퍼 | `AgentWpf.ps1` | `AgentZeroLiteGui.ps1` | 동일 패턴 |

---

## 7. 빌드 / 배포

### 7.1 자동 버전 bump

양쪽 모두 동일한 `BumpVersionAfterBuild` MSBuild target을 사용:

```
Release 빌드 종료 → version.txt 읽기 → patch+1
└─ patch == 9 일 때 → minor+1, patch=0
   └─ minor == 9 일 때 → major+1, minor=0
```

| 현재 버전 | Origin | Lite |
|---|---|---|
| version.txt | **4.5.5** | **0.1.4** |

### 7.2 인스톨러 (Inno Setup)

| 항목 | Origin | Lite |
|---|---|---|
| 스크립트 | `installer/AgentZero.iss` | `installer/AgentZeroLite.iss` |
| AppId | (확인 안됨) | `{8E2D1B3C-9F7A-4D22-A9B6-AZLITE000001}` |
| AppName | `AgentZero` | `AgentZero Lite` |
| 기본 설치 경로 | `{autopf}\AgentZero` | `{autopf}\AgentZeroLite` |
| 산출물명 | `AgentZero-v{version}-Setup.exe` | `AgentZeroLite-v{version}-Setup.exe` |
| 다국어 | English (Inno) + publish/에 16개 언어 폴더 잔재 | English + Korean (Inno 명시) |
| 권한 | `lowest` (사용자 설치) | (확인 필요) |
| 압축 | `lzma2` solid | (확인 필요) |
| Post-install | exe 자동 실행 | exe 자동 실행 |

### 7.3 GitHub Actions

| 항목 | Origin | Lite |
|---|---|---|
| 워크플로 파일 | `.github/workflows/release.yml` | `.github/workflows/release.yml` |
| 트리거 | git tag `v*` | git tag `v*` + manual workflow_dispatch |
| Publish 모드 | self-contained, win-x64 | self-contained, win-x64 |
| 산출물 | ZIP + `Setup.exe` | ZIP + `Setup.exe` |
| Release Notes | 자동 생성 (`generate_release_notes: true`) | (확인 필요) |
| 코드 사이닝 | `AgentZero-signing.cer` 존재, CI에서 사용 안 함 | (확인 안됨, .cer 미발견) |

### 7.4 빌드 산출물 비교

| 산출물 | Origin | Lite |
|---|---|---|
| 단일 exe | `AgentZeroWpf.exe` | `AgentZeroLite.exe` |
| 압축 패키지 | `AgentZero-v{ver}-win-x64.zip` | `AgentZeroLite-v{ver}-Setup.exe` (+ZIP) |
| 인스톨러 | `AgentZero-v{ver}-Setup.exe` | `AgentZeroLite-v{ver}-Setup.exe` |
| publish/ 잔재 | 16개 언어 폴더, runtimes/ | (Lite는 publish 폴더가 git 추적 대상 아님) |

---

## 8. 코드 통계 (참고)

| 메트릭 | Origin | Lite |
|---|---|---|
| C# 파일 수 (소스) | ~1,950 | ~소스 60+α (ZeroCommon) + WPF 측 다수 |
| 추정 라인 (C#) | ~6,322 (소스만) | ~6,250 (git 관리 대상) |
| XAML 파일 | 16 | 추정 5~10 (MainWindow, AgentBotWindow 등) |
| 테스트 케이스 | ~100 | (확인 필요) |
| 문서 (`Docs/`) | `Architecture, Layout, Libraries, FunctionCalling, IPC-Pipeline, FileStructure, SKILL` 등 | `gemma4-* (3편), harness/, llm/, agent-origin/`(본 세트) |

---

## 9. 설정·런타임 환경 차이

### 9.1 IDE 디버그 가이드 (CLAUDE.md)

양쪽 모두 동일한 가이드:

| IDE | 설정 |
|---|---|
| Rider | Run/Debug config → *Use external console = ON* |
| Visual Studio | Debug properties → uncheck *Use the standard console* / *Redirect standard output* |
| VS Code | `launch.json` → `"console": "externalTerminal"` |

> 사유: ConPTY가 콘솔 이벤트를 점유해야 하므로, IDE가 stdio를 가로채면 ConPTY 탭이 깨진다.

### 9.2 Akka 설정 (coordinated-shutdown)

양쪽 모두 동일한 `exit-clr = on` 패턴 사용:

```hocon
akka {
  coordinated-shutdown {
    default-phase-timeout = 5 s
    terminate-actor-system = on
    exit-clr = on                  # CLR 종료 자동화
    run-by-clr-shutdown-hook = on
  }
  actor {
    synchronized-dispatcher {
      type = SynchronizedDispatcher
      throughput = 10
    }
  }
}
```

> **공통 함정**: 이전에 `ShutdownAsync().GetAwaiter().GetResult()`를 UI 스레드에서 호출하다 데드락 발생. 두 프로젝트 모두 fire-and-forget 패턴으로 해결한 동일 history 보유 (Lite의 CLAUDE.md에 명시).

---

## 10. 외부 통합

### 10.1 MCP (.mcp.json)

| 서버 | Origin | Lite |
|---|:---:|:---:|
| memorizer (SSE) | (확인 안됨) | ✅ `mcp.webnori.com/sse` |
| playwright (stdio) | (확인 안됨) | ✅ `npx @playwright/mcp@latest` |
| pencil (stdio) | (확인 안됨) | ✅ Pencil 데스크톱 unpacked |
| x-mcp (stdio) | (확인 안됨) | ✅ 로컬 패치된 X-MCP-2.0 |

> **둘 다 MCP를 자체 호출하지는 않음** — `.mcp.json`은 Claude Code 환경 설정이며, AgentZero/Lite 자체에는 MCP 클라이언트/서버 구현이 없다. (Origin/Lite 모두 동일)

### 10.2 외부 LLM 제공자

| 제공자 | Origin | Lite | 비고 |
|---|:---:|:---:|---|
| Ollama | ✅ | ✅ | OpenAI 호환 |
| LM Studio | ✅ | ✅ | OpenAI 호환 |
| OpenAI | ✅ | ✅ | 공식 |
| **Webnori** (`a2.webnori.com`) | ❌ | ✅ | Lite 단독, 기본 백엔드 |
| **Local (LLamaSharp)** | ❌ (제거됨) | ✅ | 핵심 분기점 |

---

## 11. Speech / Voice

| 기능 | Origin | Lite |
|---|---|---|
| STT | ✅ Whisper.net (CPU + CUDA fallback) | ❌ |
| TTS | ✅ System.Speech (Windows native) | ❌ |
| 마이크 캡처 | ✅ NAudio | ❌ |
| VAD (Voice Activity Detection) | ✅ NAudio RMS 기반 | ❌ |
| UI 노출 | MainWindow "Voice Test" 패널 | — |

> **채택 분석**: Speech는 응집력 있는 모듈(`Whisper.net + NAudio + System.Speech`)로, Origin에서 따로 떼어 Lite에 이식 가능. 단 publish 사이즈 증가 (~50MB), CUDA runtime 선택적 포함 필요. **P1**.

---

## 12. UI 자산 / 추가 윈도우

| 윈도우 | Origin | Lite | 사이즈/용도 |
|---|:---:|:---:|---|
| MainWindow | ✅ | ✅ | ~600x500 (양쪽) |
| AgentBotWindow | ✅ (420x600, 3모드) | ✅ (3모드) | Chat/Key/AI |
| **NoteWindow** | ✅ (1100x700) | ❌ | 파일 브라우저 + 문서 뷰어 |
| **HarnessMonitorWindow** | ✅ (750x650) | ❌ | 하네스 설정 + AI 어시스트 |
| **ScrapPanel** | ✅ | ❌ | 윈도우 스파이 + 캡처 결과 |
| CliDefEditWindow | ✅ (420x400) | (확인 필요) | CLI 정의 편집 |
| **VirtualDesktop API** | ✅ (`IVirtualDesktopService`) | ❌ | Windows 가상 데스크톱 COM 래핑 |
| **Mermaid 임베드** | (확인 안됨) | ✅ `Assets/mermaid.min.js` (3.1MB, EmbeddedResource) | 오프라인 렌더링 |
| **Pencil 렌더러** | (확인 안됨) | ✅ `PencilRenderer.cs` | .pen 파일 미리보기 |

---

## 13. 하네스 (QA Garden)

| 항목 | Origin | Lite |
|---|---|---|
| 이름 | `AgentZero Flow` | `AgentZero Lite Harness` |
| 버전 | 1.5.1 | 1.1.2 |
| 에이전트 수 | **9명** | **5명** |
| 에이전트 종류 | tamer, **wpf-engineer**, **conpty-engineer**, **ipc-engineer**, **automation-engineer**, **llm-engineer**, **native-efficiency-auditor**, **nondev-usability-evaluator**, **chakra-auditor** | tamer, security-guard, build-doctor, test-sentinel, code-coach |
| 엔진 수 | **4개** | **2개** |
| 엔진 종류 | full-inspection, targeted-review, native-automation-audit, usability-journey-audit | release-build-pipeline, pre-commit-review |
| 노트 DB | `data-base/harness-note.db` (SQLite WAL) | (확인 안됨) |

**차이 요약**:
- Origin은 **도메인 전문가** 위주 (각 기술 레이어별 리뷰어)
- Lite는 **품질 관리** 위주 (보안/빌드/테스트/코칭)
- 두 접근의 합집합이 이상적. **P2 채택**: Origin의 wpf/conpty/ipc/llm/native 전문가를 Lite에 추가 가능.

---

## 14. 문서 / 기획 디렉토리

| 디렉토리 | Origin | Lite |
|---|---|---|
| `Docs/AgentZero/` (Origin) / `Docs/` (Lite) | Architecture, Layout, Libraries, FunctionCalling, IPC-Pipeline, FileStructure, SKILL | gemma4-* (3편), harness/, llm/, agent-origin/ (본 세트) |
| `Tech/` | BUG, DOC, Labs, NewTech, Refact, claude-logs | (없음) |
| `Prompt/` | 27개 prompt 히스토리, Update/, pencil/ | (없음) |
| `data-base/` | harness-note.db | (없음) |
| `home/` | illustration-prompts.md, index.html | (없음) |
| `secret/` | API 키 템플릿, 위키 노트 | (없음) |
| `installer/` | AgentZero.iss | AgentZeroLite.iss |
| `output/`, `publish/` | 빌드 산출물 (16개 언어 폴더 포함) | (git ignore) |

---

## 15. 핵심 차이 요약 (1줄씩)

1. Lite는 솔루션 파일을 버리고 csproj 직접 빌드 → IDE 의존성 감소.
2. Lite는 `LlmProbe` 콘솔 유틸 추가 → GGUF 로드 검증 분리.
3. Lite는 `AgentZeroLite.exe`로 산출물 명칭 분리 (네임스페이스는 호환 유지).
4. Lite는 LLamaSharp을 self-built DLL로 살림 → Origin은 Whisper와 충돌로 포기.
5. Lite는 `LlmGateway`로 Local/External 통합 추상 → Origin은 외부만 단순 분기.
6. Lite는 EF Core 마이그레이션을 1개로 정리 → Origin은 31개 누적.
7. Lite는 Speech (STT/TTS) 전체 부재 → Origin은 Whisper.net + NAudio + System.Speech.
8. Lite는 CLI 자동화 명령 부재 → Origin은 데스크톱 자동화 풀세트.
9. Lite는 NoteWindow/HarnessMonitor/Scrap/VirtualDesktop 부재 → Origin은 모두 보유.
10. Lite는 하네스가 QA 5명 → Origin은 도메인 전문가 9명.

다음: [`02-architecture-comparison.md`](./02-architecture-comparison.md) — 아키텍처 다이어그램 및 분기 사유.
