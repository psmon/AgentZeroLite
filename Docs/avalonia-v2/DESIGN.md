# AgentZero Lite — Avalonia 크로스플랫폼(Windows + macOS) 전환 계획 v2

## Context

AgentZero Lite는 WPF(`net10.0-windows`) 호스트에 묶여 있어 macOS에서 실행되지 않는다. 6월의 첫 시도
(`feat/avalonia-crossplatform`, 6커밋, 브랜치 삭제·객체는 `806cf94`로 남음)는 네이티브 Avalonia 터미널
컨트롤을 써서 **사용자가 보는 터미널과 에이전트가 제어하는 PTY가 서로 다른 두 PTY**가 되는 구조적 문제로
막혔다. 이번에는 WPF에서 안정적으로 검증된 **xterm.js 렌더링 + 단일 `ITerminalSession`** 구조를 그대로
가져가고, PTY 공급자만 OS별로 나눈다.

운영자 결정(확정):
- **기존 WPF 앱은 동작 영향 없이 유지.** 새 Avalonia 호스트를 옆에 추가(`Project/AgentZeroAvalonia`), ZeroCommon 공유.
  `Project/AgentZeroWpf/` 파일은 **수정하지 않는다**(자산 포함). ZeroCommon 변경은 추가(additive)만, 기존 동작 불변.
- 터미널: xterm.js 공식 채택. **Windows는 현행 `ManagedConPtyHost`(kernel32 ConPTY, DLL 없음) 사본을 그대로**,
  macOS는 Porta.Pty(forkpty). **분할창(split panes)이 핵심 요구.**
- 1차 범위 = 코어: 멀티 터미널 탭+분할+워크스페이스, AgentBot 채팅+에이전트 루프, 파일·웹 툴(ZeroCommon 기존),
  설정(External LLM·CLI 정의·터미널 외관·Windows 로컬 LLM), `-cli` IPC.
- 범위 밖(1차): OS 제어, WebDev 플러그인, Voice, Vision, Music, Remote, Wearable BLE, Scrap, Note/Document 뷰어,
  PencilRenderer, macOS 로컬 LLM(stretch), Inno 설치기·서명/공증.

조사 근거(2026-09-18, 확인됨): Avalonia **12.1.2**(MIT, net10.0) · `Avalonia.Controls.WebView` **12.1.0**(MIT,
WebView2/WKWebView; `NavigateToString`, `WebResourceRequested`, `WebMessageReceived`+`invokeCSharpAction`, `InvokeScript`;
로컬 폴더 매핑은 **미문서화**) · `Dock.Avalonia` 12.1.0.6(있으나 미채택, 아래) · `Porta.Pty` **2.2.2**(MIT, .NET 10 전용,
osx-arm64/x64 네이티브 동봉) · `CommunityToolkit.Mvvm` 8.4.0. 윈도우 종속: P/Invoke 96개(ZeroCommon 1개), WPF 코드비하인드
~21k줄·XAML ~7k줄, 뷰모델 없음, IPC = WM_COPYDATA + MMF 19개, 단일 인스턴스 = `Local\` 뮤텍스.
`Project/AgentZeroAvalonia/`는 현재 추적되지 않는 옛 `bin/obj`만 남아 있다(소스 없음).

## 목표 아키텍처

```
Project/AgentZeroAvalonia (net10.0, Avalonia 12.1)                Project/AgentZeroWpf (무변경)
 ├─ Terminal/ IPtyHost 구현: ConPtyHost(Win, ManagedConPtyHost 사본) / PortaPtyHost(mac)
 │            XtermWebViewTerminalControl (NativeWebView) + XtermBridge + TerminalSurfaceHost
 ├─ Layout/   SplitTree(직접 구현: 재귀 Grid+GridSplitter) ↔ ZeroCommon DockPaneLayout JSON
 ├─ Chat/     AgentBotView/ViewModel — 액터 토폴로지 재사용
 ├─ Settings/ · Cli/ (NamedPipe 클라이언트/서버, -cli 이중 디스패치)
 ├─ Actors/ActorSystemManager (WPF 것 그대로, UI 스레드에서 Initialize)
 └─ Wasm/xterm: vendor/** 는 WPF 폴더에 링크, index.html·term.js는 사본(전송 shim 포함)
                       ↓ 공유
Project/ZeroCommon (추가만): Platform/{AppPaths, ISingleInstanceGuard, ICliIpcBridge}, Services/{IPtyHost,
 XtermTerminalSession(사본), TerminalHealthTracker, TerminalLaunchPlanner, TerminalEnvironment.BuildPosix()},
 Module/TerminalCatalogJson, Data/CliDefinition OS별 시드, Security/AesGcmFileSecretProtector, Agents/ChatModeCycle
```

## Phase 0 — 브랜치·미션 (M0033 착수 전)

1. 잔존 `Project/AgentZeroAvalonia/{bin,obj}` 삭제(비추적 산출물, 삭제 전 `git status`로 확인).
2. `git worktree add C:\code\psmon\AgentZeroLite-avalonia-v2 -b feat/avalonia-v2 main`.
3. `harness/knowledge/_shared/long-lived-branches.md`: 삭제된 `feat/avalonia-crossplatform` 행을 `feat/avalonia-v2`로 교체.
   정책: ZeroCommon 심(M0033)은 main에 PR로 먼저 병합, 호스트 프로젝트는 M0040 수용 후 병합. `806cf94`는 `git show` 참조용.
4. `AgentZeroLite.slnx`에 `Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj` 등록. 설계 기록 `Docs/avalonia-v2/DESIGN.md`.
5. 미션 시리즈(`harness/missions/`, 마일스톤당 1건, `related:` 연결):

| Id | 제목 |
|---|---|
| M0033 | Avalonia v2 ①: ZeroCommon 플랫폼 seam — AppPaths·단일인스턴스·NamedPipe CLI IPC·IPtyHost/XtermTerminalSession·POSIX 환경·OS별 CLI 시드 |
| M0034 | Avalonia v2 ②: 호스트 골격 — 프로젝트·부트스트랩·액터 시스템·메인 셸·CI 빌드 게이트 |
| M0035 | Avalonia v2 ③: xterm.js WebView 터미널 — 자산 서빙·PTY 호스트 2종·브리지·헬스 |
| M0036 | Avalonia v2 ④: 분할창·탭·워크스페이스 — SplitTree·단축키·DockPaneLayout 호환 저장 |
| M0037 | Avalonia v2 ⑤: AgentBot 채팅 + 에이전트 루프 — 모드 순환·진행 카드·툴벨트 |
| M0038 | Avalonia v2 ⑥: 설정 — External LLM·CLI 정의 CRUD·터미널 외관·(Windows) 로컬 LLM |
| M0039 | Avalonia v2 ⑦: `-cli` 표면 — 파이프 서버/클라이언트·1차 동사·셸 래퍼 |
| M0040 | Avalonia v2 ⑧: 패키징/CI — win-x64 zip·osx-arm64 .app·macOS 스모크 |

## Phase 1 — ZeroCommon 추가 (M0033, main에 먼저; 각 단계마다 WPF 빌드 녹색 유지)

모두 새 파일이거나 `OperatingSystem.IsWindows()` 분기 추가. WPF 호출 경로의 Windows 동작은 바이트 단위로 동일해야 한다.

| # | 변경 | 파일 | 테스트(ZeroCommon.Tests) |
|---|---|---|---|
| 1.1 | `AppPaths` — `DataRoot`(1차: 모든 OS에서 `LocalApplicationData/AgentZeroLite`, 나중에 `~/Library/Application Support`로 옮길 단일 스위치), `File()`, `Dir()`. **기존 20개 스토어는 손대지 않음**(새 호스트만 사용) | `Platform/AppPaths.cs` | 경로 해석 |
| 1.2 | `ISingleInstanceGuard` — Windows: 뮤텍스 `Local\AgentZeroLite.SingleInstance`(WPF와 **같은 이름**: 두 GUI가 같은 SQLite를 동시에 열지 못하게, 문서화), Unix: `AppPaths` 아래 잠금 파일 | `Platform/ISingleInstanceGuard.cs` (806cf94 살림) | 2회 획득 → 두 번째 false |
| 1.3 | `ICliIpcBridge` + `CliIpcProtocol` — `NamedPipeServerStream("AgentZeroLite.cli", Asynchronous|CurrentUserOnly)`, 연결당 요청 1건, 요청/응답 = UTF-8 JSON 한 줄, **요청 객체는 WPF `{"command":…}`와 동일, 응답은 MMF 페이로드와 동일**(→ `-cli help agentzero`·프린터 무변경), 1 MiB 캡, 핸들러 `Func<string,CancellationToken,Task<string>>`(web 같은 비동기 동사가 accept 루프를 막지 않음), 클라이언트 `SendRequest(json, timeoutMs)` 연결 실패 시 null(= GUI 없음) | `Platform/ICliIpcBridge.cs`, `Platform/CliIpcProtocol.cs` (806cf94 재작업) | 왕복·타임아웃·동시 2클라이언트·초과 크기 거부·Dispose 1초 내 |
| 1.4 | 터미널 카탈로그 계약 — `IConsoleTabInfo`에 `ITerminalSession? Session`·`bool IsTerminalStarted` 추가(WPF `ConsoleTabInfo`는 이미 두 멤버를 가짐 → WPF 무편집으로 컴파일). `Module/TerminalCatalogJson.cs` = `CliTerminalIpcHelper.BuildTerminalListJson`/`TryResolveSession`의 WPF-free 이식(hwnd는 콜백, 기본 `""`) | `Module/ICliGroupInfo.cs`, `Module/TerminalCatalogJson.cs` | WPF JSON 골든 테스트 |
| 1.5 | `TerminalEnvironment.BuildPosix()` 추가(대소문자 구분 딕셔너리, 같은 변수 제거 목록, `TERM=xterm-256color`·`LANG` 기본), `PrependPath(env, dir)`(`;`/`:`) — WPF의 `cmd /c set PATH` 래퍼 대체. 기존 `Build()` 불변 | `Services/TerminalEnvironment.cs` | `Path`≠`PATH` 보존, LANG, 구분자 |
| 1.6 | `TerminalLaunchSpec` + `TerminalLaunchPlanner` + `CommandLineSplitter` — `CliDefinition`(+workDir, appDir) → `(App, Args[], Cwd, Env)`. Unix에서 `.exe` 정의는 `IsAvailableOnThisOs()`=false; SSH/원격 정의는 1차 비Windows에서 `Unsupported` | `Services/TerminalLaunchSpec.cs`, `TerminalLaunchPlanner.cs`, `CommandLineSplitter.cs` | 분리기·OS별 계획·.exe 필터 |
| 1.7 | OS별 CLI 시드(마이그레이션 없이) — `AppDbContext.EnsureDefaultCliDefinitions`(이미 런타임 시드 "Claude" 존재)에 비Windows 분기: 비`.exe` 정의가 없으면 `zsh`(`/bin/zsh -l`), `bash`, `Claude`(`/bin/zsh -l -c "claude; exec zsh -l"`) `IsBuiltIn=true` 추가. OS 판정은 주입 가능한 `Func<bool>` | `Data/AppDbContext.cs` | 인메모리 SQLite, `isWindows:false` 강제 |
| 1.8 | 윈도우 전용 코드 울타리 — `LlmService`의 `_putenv_s`는 `IsWindows()`일 때만; `VulkanDeviceEnumerator` 비Windows → 빈 목록; `LlamaSharpLocalLlm.ConfigureNativeOnce` RID 인식(`win-x64-*/llama.dll` 외는 `PlatformNotSupportedException("use External")`); `LlmGateway.IsActiveAvailable()` 비Windows Local → false | `Llm/LlmService.cs`, `VulkanDeviceEnumerator.cs`, `LlamaSharpLocalLlm.cs`, `LlmGateway.cs` | 비Windows → PlatformNotSupported |
| 1.9 | `IPtyHost` + `XtermTerminalSession` + `TerminalHealthTracker` — `IPtyHost : IDisposable { event Action<string> Output; event Action Exited; bool IsRunning; void Write(ReadOnlySpan<char>); void Resize(int,int); string Diagnostics }`. `AgentZeroWpf/Services/WebViewXtermTerminalSession.cs`를 **복사**해 `Services/XtermTerminalSession.cs`(ctor `IPtyHost`), 헬스 FSM(INPUT-NO-ECHO, Alive→Stale@3→Dead@5, 지연 주입 가능)은 `TerminalHealthTracker`로 분리. WPF 원본은 그대로(중복 허용, 후속 통합). **Porta.Pty는 ZeroCommon에 넣지 않음**(네이티브 동봉 → ZeroWearable·테스트까지 끌려감) | `Services/IPtyHost.cs`, `XtermTerminalSession.cs`, `TerminalHealthTracker.cs` | `FakePtyHost`; `WriteAsync` 청킹(200/50ms/300ms), 스냅샷→`GetConsoleText`, 헬스 전이 |
| 1.10 | `AesGcmFileSecretProtector : Agent.Common.Security.ISecretProtector`(현행 인터페이스 대상; 806cf94의 AES 본문만 살림) — 마커 `aesg:v1:`, 멱등, 평문 통과, 키 파일 0600 | `Security/AesGcmFileSecretProtector.cs` | 왕복·멱등·변조→null |
| 1.11 | `Agents/ChatModeCycle.cs`(+`ChatMode`) — WPF `UI/APP/ChatModeCycle.cs` 순수 로직 사본 | 신규 | 전이표 |

## Phase 2 — 호스트 골격 (M0034)

- csproj: `OutputType WinExe`, `net10.0`, `AssemblyName AgentZeroLite`(래퍼·가이드 이름 유지, 출력 폴더는 별도), `Configurations Debug;Release;AgentCLI`,
  컴파일 바인딩 기본, `app.manifest`. 패키지: `Avalonia`/`.Desktop`/`.Themes.Fluent`/`.Fonts.Inter` 12.1.2, `AvaloniaUI.DiagnosticsSupport`(Debug),
  `Avalonia.Controls.WebView` 12.1.0, `CommunityToolkit.Mvvm` 8.4.0, `Porta.Pty` 2.2.2, `ProjectReference ZeroCommon`. **미채택**: Dock.Avalonia,
  Iciclecreek.Avalonia.Terminal, Markdown.Avalonia(11.x), ZeroWearable 참조.
- 자산: `<Content Include="..\AgentZeroWpf\Wasm\xterm\vendor\**" Link="Wasm\xterm\vendor\...">` 링크(한 벌 유지), `version.txt` 링크(WPF의
  `BumpVersionAfterBuild`는 복제하지 않음). `Wasm/xterm/index.html`·`term.js`는 **사본**(WPF 자산 무변경).
- `AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()`. 부트 순서: 로거 → `-cli`면 `Cli/CliMain` → 단일 인스턴스 → `SecretProtection.Protector`
  (Windows DPAPI 사본 / macOS AES) → DB init + 시드 → `ActorSystemManager.Initialize()`(UI 스레드; `SynchronizationContext.Current`가 null이면
  `AvaloniaSynchronizationContext` 명시 설치) → `CliServer` → MainWindow. 종료는 `ShutdownRequested`에서 fire-and-forget(`exit-clr=on` 유지).
- 폴더: `Actors/`, `Cli/`, `Security/`, `Services/`(PtyHost 2종, LocalAssetServer, TerminalCatalog, WorkspaceToolHost, AgentLoopWiring), `Terminal/`,
  `Layout/`, `ViewModels/`, `Views/`, `Styles/Theme.axaml`, `macos/`.
- 806cf94에서 살릴 것(`git show 806cf94:<path>` → 12 API로 손질): `Program.cs`(구조), `App.axaml(.cs)`, `Styles/Theme.axaml`, `Actors/ActorSystemManager.cs`,
  `Views/SettingsView.axaml`+`ViewModels/SettingsViewModel.cs`(External LLM 폼 골격), `ViewModels/Converters.cs`, `Models/ChatMessage.cs`,
  `Services/PtyTerminalSession.cs`의 Porta 스폰+UTF-8 `Decoder` 읽기 루프. 버릴 것: TerminalView(네이티브), Docking/*, Notebook/Markdown/Clipboard, NullVoice, CliStub.
- 수용: 앱 기동, 두 번째 인스턴스 거부, 공유 경로 DB 초기화, `-cli status`가 파이프로 응답, 메인 셸(액티비티 바·워크스페이스 사이드바·빈 문서 영역·봇 토글), CI 잡 A 녹색.

## Phase 3 — 터미널 (M0035, 핵심)

1. **PTY 백엔드**: `Services/ConPtyHost.cs` = `AgentZeroWpf/Services/ManagedConPtyHost.cs` 사본(`[SupportedOSPlatform("windows")]`, kernel32만) — Windows 기본.
   `Services/PortaPtyHost.cs` = `PtyProvider.SpawnAsync(new PtyOptions{App, CommandLine, Cwd, Environment, Cols, Rows})`, 상태 있는 UTF-8 디코더 읽기 루프,
   쓰기 직렬화, `ProcessExited→Exited`, `Dispose→Kill` — macOS. 선택은 `OperatingSystem.IsWindows()`.
2. **세션**: ZeroCommon `XtermTerminalSession`(1.9). 소유 관계는 WPF와 동일(컨트롤이 PTY 호스트 소유, 닫으면 자식 종료).
3. **자산 서빙**: 1순위 **`LocalAssetServer`**(`HttpListener` 127.0.0.1 임의 포트, 경로 `/{token}/index.html`, 토큰 32바이트, `..` 거부, 확장자 화이트리스트, `no-store`;
   `index.html`의 CSP `'self'`가 그대로 유효). 시작 시 반나절 타임박스 스파이크로 `WebResourceRequested` 응답 합성·`file://` 탐색을 시험해 가능하면 최적화로 채택
   (`Docs/avalonia-v2/DESIGN.md`에 기록). `EnvironmentRequested`로 유저 데이터 폴더 `%TEMP%/AgentZeroLite_Xterm`, Debug에서 devtools.
4. **브리지**(`Terminal/XtermBridge.cs`, 사본 `term.js`): JS→호스트 `invokeCSharpAction(JSON.stringify(o))` 우선, 없으면 `chrome.webview.postMessage`;
   호스트→JS `ExecuteScriptAsync("window.zeroHost.recv(<json>)")`. 메시지 어휘는 WPF와 동일(`ready/in/resize/screen/renderer/fontstatus/activate/link`) +
   `out64`(base64 배치) + `hotkey`. 출력 경로: PTY 읽기 → `SynchronizedOutputBuffer`(DEC 2026, 150ms) → UI 스레드 코얼레싱 → 프레임당 `out64` 1회(64 KiB 캡),
   `ready` 전 4 MiB 버퍼. 스파이크에서 처리량 측정(5 MB `cat`, `yes | head -200000`, Claude Code TUI 재그림).
5. **메시지 처리**(WPF `XtermTerminalControl.xaml.cs` 1:1): `ready`→외관(`TerminalSettingsStore`)+플러시+Resize, `in`→`Write`+`NoteInputAttempt`,
   `resize`→`Resize`, `screen`→스냅샷, `activate`→탭 활성, `link`→`TopLevel.Launcher.LaunchUriAsync`(http/https만), `hotkey`→명령.
   헬스 Dead → 배너 "이 터미널 다시 시작"(저장된 `TerminalLaunchSpec`으로 재생성·재바인딩).
6. **액터 바인딩**: 시작 시 `CreateTerminalInWorkspace` + `BindSessionInWorkspace`, 워크스페이스 추가 시 `RegisterWorkspace`, 활성 시 `SetActiveTerminal`(HWND 갱신은 생략).
7. **ConPTY 사본 vs Porta 대비 체크리스트**(macOS만 Porta): 환경 전달(빈 딕셔너리≠상속), 종료 감지(`ProcessExited`), 부하 중 리사이즈, 한글 에코, 5 KB 붙여넣기 청킹.
- 수용: Windows `cmd/pwsh/Claude`·macOS `/bin/zsh` 탭 렌더링, 한글 IME, 리사이즈 추종, `-cli terminal-read`가 화면 텍스트 반환, `terminal-send "dir"` 에코,
  Claude Code TUI 사용 가능, 막힌 탭에 헬스 배너.

## Phase 4 — 분할창·탭·워크스페이스 (M0036)

**결정: Dock.Avalonia 대신 직접 구현한 `SplitTree`.** 이유: (1) `NativeWebView`는 `NativeControlHost`라 분할 시 **재부모화되면 네이티브 뷰가 파괴·재생성**(스크롤백·
재탐색 손실) — Dock의 드래그 도킹이 정확히 그 동작. (2) 영속 모델이 이미 `ZeroCommon/Services/DockPaneLayout.cs`(`DockPaneNode` Pane/Group/Vertical + `Normalise`)이고
재귀 Grid+GridSplitter가 1:1. (3) Dock의 툴독·플로팅은 범위 밖.

- 모델 `Layout/`: `PaneNode{Tabs, Active}` | `SplitGroupNode{Vertical, Children, Weights}`; `SplitLayoutMapper` ↔ `DockPaneNode`(탭은 워크스페이스 탭 인덱스, 가중치는 저장 안 함 — WPF와 같은 JSON).
- **표면 계층**: `TerminalSurfaceHost`(Canvas)에 워크스페이스의 모든 `XtermWebViewTerminalControl`을 한 번만 배치하고 절대 좌표로 위치; `SplitLayoutView`(Grid·Splitter·탭 스트립)는
  그 아래에서 각 페인의 콘텐츠 사각형을 보고(`LayoutUpdated`) 활성 탭 컨트롤의 `Canvas.Left/Top/Width/Height`·`IsVisible`을 갱신. 분할·탭 이동·워크스페이스 전환에서 **재부모화 없음**,
  비활성 탭의 PTY는 계속 실행(WPF 동일).
- macOS z-order: 네이티브 뷰 위에 Avalonia 콘텐츠를 그리지 않음 — 메뉴는 `Popup/Flyout`(별도 창), 배너·링크 스트립은 터미널 사각형 **위쪽** 영역.
- 명령: `SplitRight/SplitDown/ClosePane/NewTab(cliDefId)/CloseTab/NextTab/PrevTab/MoveTabToPane/FocusPane(dir)/RenameTab/RestartTab`.
- 영속: `LayoutChanged` 500ms 디바운스 → `DockPaneLayout.ToJson` → `CliGroup.LayoutJson`(`CliWorkspacePersistence`, `ICliGroupInfo.DockLayoutJson`) — 같은 DB를 두 호스트가 열 수 있음.
- 단축키: WebView가 포커스를 가지면 Avalonia `KeyBinding`이 못 봄 → `term.js`가 `attachCustomKeyEventHandler`로 코드 집합을 가로채 `{type:'hotkey',name}` 전송; 코드 표는
  `ZeroCommon/Services/ShortcutSettings.cs` 어휘 재사용, `config` 메시지로 JS에 전달(macOS `Cmd`, Windows `Ctrl`). 기본: 분할 오른쪽 `Ctrl/Cmd+Shift+E`, 아래 `+Shift+O`, 새 탭 `+T`, 닫기 `+W`,
  순환 `Ctrl+Tab`, 페인 이동 `Alt+화살표`, 봇 토글 `+Shift+B`.
- 수용: 2×2 분할·탭 이동·워크스페이스 복수, 재시작 후 레이아웃 복원, **Avalonia가 저장한 레이아웃을 WPF가 동일하게 열음**(실제 WPF 저장 행 골든 테스트), 터미널 포커스 중 모든 코드 동작.

## Phase 5 — AgentBot (M0037)

- `AgentBotViewModel`(ObservableObject): `Items`(User/Bot/SystemNotice/ProgressCard/ToolCallCard), `Mode`(ZeroCommon `ChatModeCycle`, AI 가용 = `LlmGateway.IsActiveAvailable()`),
  명령 `Send/CycleMode/NewSession(ResetAgentLoopMemory)/Cancel(CancelAgentLoop)/SendKey`. CHT: 활성 세션 `WriteAndEnter`; KEY: `SendControl`; AI: `StartAgentLoop`.
- `Services/AgentLoopWiring.cs` = `AgentBotWindow.xaml.cs` 1211–1300 이식: `SetAgentLoopCallbacks`(`Dispatcher.UIThread.Post`), `AgentLoopBindings`(ToolbeltFactory → `WorkspaceToolHost`,
  OptionsFactory, AgentLoopFactory — External 전 OS, Local은 Windows+`LlamaSharpLocalLlm`일 때만). `SetBotUiCallback`→Items; `bot-chat` CLI → `TerminalSentToBot`.
- 진행 카드: Thinking 스피너 → Generating 토큰 갱신 → Acting `ToolCallCard` 추가 → Done/Error 최종 메시지(M0032에서 고친 `AgentLoopActor` 진행 전달 활용).
- `Services/WorkspaceToolHost : IAgentToolbelt`: 터미널 4동사는 `TerminalCatalogJson` + 첫 접촉 소개(`IntroduceTerminalIfFirst`) 이식, 파일은 `FileToolCore`(활성 워크스페이스 루트),
  `find_files/open_file`은 `FileOpenPolicy` + `Launcher`, 웹은 `HeadlessWebToolSurface`(`via: headless`), Os*는 기본값. (WPF `WorkspaceTerminalToolHost`는 그대로.)
- 수용: CHT/KEY로 터미널 구동, AI 모드로 `list_terminals→send_to_terminal→read_terminal`·`read_file/grep`·`web_search` E2E, 카드 렌더, CLI `bot-chat` 도착, Windows 로컬 LLM 경로 동작.

## Phase 6 — 설정 (M0038)

좌측 내비(LLM / CLI 정의 / 터미널) + `SettingsViewModel` 부분 클래스. 재사용: `LlmRuntimeSettings`·`ExternalLlmSettings`·`LlmSettingsStore`·`LlmGateway.OpenSession`(테스트 버튼);
Windows 로컬: `LlmModelCatalog/Locator/Downloader`, `LocalLlmBackend`, `VulkanDeviceEnumerator`, `LlmService.LoadAsync/UnloadAsync`(비Windows는 라디오 비활성+툴팁);
CLI 정의: `CliWorkspacePersistence.LoadCliDefinitions`·`AppDbContext`·`CliDefinition`(`IsBuiltIn` 삭제 불가, `StorageProvider` 파일 선택, 비Windows `.exe` 숨김, SSH 필드 읽기 전용);
터미널 외관: `TerminalSettings/Store`·`TerminalThemeCatalog`(`config` 재전송으로 라이브 미리보기). 비밀은 `SecretProtection.Protector`(Windows DPAPI 사본 / macOS AES).

## Phase 7 — CLI (M0039)

- `Program.Main`: Avalonia 초기화 **전에** `-cli` → `Cli/CliMain.Run`. Windows는 `AttachConsole/AllocConsole/FreeConsole` 사본(`Cli/WindowsConsole.cs`, 울타리).
- 1차 동사: `help [topic]`(`AgentSkillGuides` 그대로), `version`, `status`, `terminal-list/-send/-key/-read/-wait/-alias`, `bot-chat`, `web open|search|read|tabs`, `open-win`, `close-win`(파이프 명령).
  프린터는 `CliHandler.cs`에서 이식(같은 JSON). 숨은 자가진단 `selftest pty|ipc|secrets`(CI용).
- `CliClient.Send` = `ICliIpcBridge.SendRequest`; null → "GUI가 실행 중이 아님(파이프 없음)" + Windows 힌트("WPF 빌드는 WM_COPYDATA 사용").
- `CliServer`(GUI) → `Dispatcher.UIThread.InvokeAsync(router.Handle)`; `CliCommandRouter`는 `MainWindow.HandleCliCommand` 디스패치 미러(`TerminalCatalogJson`, `WriteAndSubmit`,
  `TerminalControlSequences`, `ApprovalParser.StripAnsiCodes`); `web`은 UI 스레드 밖에서 `HeadlessWebToolSurface`, `req` 에코.
- 래퍼: `AgentZeroLite.ps1` 사본, `AgentZeroLite.sh`(`exec "$(dirname "$0")/AgentZeroLite" -cli "$@"`, `.app/Contents/MacOS/` 안에 동봉).

## Phase 8 — 패키징/CI (M0040)

- win-x64: `dotnet publish … -r win-x64 --self-contained -p:PublishSingleFile=false` → `AgentZeroLite-Avalonia-v{ver}-win-x64.zip`(Inno 없음).
- osx-arm64: publish + `macos/build-app.sh`(`Info.plist`: `com.psmon.agentzerolite`, `CFBundleExecutable AgentZeroLite`, `LSMinimumSystemVersion 12.0`) + ad-hoc `codesign`;
  Developer ID 서명·공증은 후속(`harness/knowledge/_shared/code-signing.md`에 절차 기록). `ditto -c -k` zip.
- CI `.github/workflows/avalonia-build.yml`(push `feat/avalonia-v2`, dispatch): 잡 A windows-latest — 테스트(ZeroCommon.Tests·AgentZeroAvalonia.Tests)+win-x64 publish;
  잡 B macos-14 — 빌드·osx-arm64 publish·`build-app.sh`·`-cli version/selftest pty|ipc|secrets`·ZeroCommon.Tests on macOS. **`release.yml`은 손대지 않음.**
- macOS GUI/WKWebView 검증은 사람 필요 → `Docs/avalonia-v2/macos-smoke.md` 체크리스트(운영자 Mac 보유 여부 미확인 — 없으면 M0040까지 CI 자가진단만).

## 위험과 완화

| 위험 | 완화 |
|---|---|
| WebView 로컬 자산 매핑 미문서화 | `LocalAssetServer` 1순위(결정적), `WebResourceRequested`/`file://`는 스파이크 후 최적화 |
| `invokeCSharpAction` 의미가 WebView2/WKWebView에서 다름 | 항상 JSON **문자열** 전송, 호스트 `JsonDocument` 파싱, 코덱 단위 테스트, M0035 1일차 양방향 스파이크 |
| `ExecuteScriptAsync` 처리량 | 프레임 코얼레싱 + base64 배치, 목표치 측정, 지연 시 배치 확대 |
| macOS `NativeControlHost` z-order | 터미널 위에 그리지 않는 레이아웃, 메뉴는 Popup 창, macOS 스모크 조기 확인 |
| Windows PTY 동작 회귀 | Windows는 검증된 `ManagedConPtyHost` 사본 사용(Porta 미사용) |
| Porta.Pty macOS 동작(환경·종료·리사이즈·UTF-8) | Phase 3 체크리스트, CI macOS `selftest pty` |
| 패키지 미캐시(Avalonia 12.1.2·WebView 12.1.0·Porta 2.2.2) | M0034 첫 작업 = 복원; 불가 시 가장 가까운 버전 고정·기록 |
| 두 GUI가 한 SQLite | 같은 뮤텍스 이름·문서화, `-cli`가 어느 호스트가 응답하는지 표시 |
| LLamaSharp macOS Metal | 저장소는 0.26 + 자체 빌드 win DLL → 백엔드 패키지 버전 불일치. 1차 macOS = External만 |
| `HttpListener` on macOS | `IAssetServer` 뒤에 `TcpListener` 응답기 폴백 |
| WebView가 단축키 삼킴 | `attachCustomKeyEventHandler` → `hotkey` 메시지, WKWebView에서 Cmd 코드 확인 |
| Akka `SynchronizedDispatcher`에 SyncContext 없음 | 기동 시 단언·`AvaloniaSynchronizationContext` 명시 설치 |
| 중복 코드(XtermTerminalSession·term.js·ConPtyHost·ChatModeCycle 사본) | "WPF 무영향" 우선 결정; 1차 수용 후 통합 미션으로 회수 |

## 검증

- **ZeroCommon.Tests**(양 OS CI): Phase 1 표의 테스트, `FakePtyHost`, DockPaneLayout 호환 골든 JSON.
- **새 `Project/AgentZeroAvalonia.Tests`**(xUnit + `Avalonia.Headless.XUnit` 12, `[AvaloniaFact]`): SplitTree 모델/매퍼, `TerminalSurfaceHost` 사각형 매핑, `AgentBotViewModel` 모드·카드,
  `CliCommandRouter`(가짜 세션), `XtermBridge` 코덱, `LocalAssetServer`(토큰·트래버설·MIME), `PortaPtyHost`/`ConPtyHost` 에코 왕복(각 OS 러너).
- **회귀(매 마일스톤)**: `git diff --stat main -- Project/AgentZeroWpf` 가 비어 있음; WPF Debug 빌드·`AgentTest`·`ZeroCommon.Tests` 전부 통과.
- **수동 스모크 Windows**: WPF 종료 후 Avalonia 실행 → cmd/pwsh/Claude 탭 → 2×2 분할 → 한글 입력·5 KB 붙여넣기 → `AgentZeroLite.ps1 terminal-list/-send/-read/-wait` → AI 모드(Webnori) →
  설정 왕복 → 재시작 레이아웃 복원 → WPF 실행해 같은 워크스페이스 열림 확인.
- **수동 스모크 macOS**(`Docs/avalonia-v2/macos-smoke.md`): `xattr` 후 첫 실행, zsh 탭 색상·`LANG`, `claude` 탭, Cmd 코드, 분할/닫기, `AgentZeroLite.sh status/terminal-*`,
  External LLM 채팅, `secret.key` 0600, 종료 시 고아 `zsh` 없음, 팝업/메뉴가 터미널 위에 보임.

## 핵심 참조 파일

- `Project/AgentZeroWpf/Services/WebViewXtermTerminalSession.cs` — ZeroCommon `XtermTerminalSession`의 원본(복사), 헬스 FSM 출처
- `Project/AgentZeroWpf/Services/ManagedConPtyHost.cs` — Windows `ConPtyHost` 원본(복사)
- `Project/AgentZeroWpf/UI/Components/XtermTerminalControl.xaml.cs` — 브리지·코얼레싱·외관 로직 이식 원본
- `Project/AgentZeroWpf/Wasm/xterm/term.js`, `index.html` — 사본에 전송 shim·`out64`·`hotkey` 추가 (원본 무변경)
- `Project/ZeroCommon/Services/DockPaneLayout.cs` — SplitTree가 매핑할 영속 모델
- `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs` — `HandleCliCommand`(754–890) CLI JSON 계약, `BindSessionToActors`(2164–2185), `InitializeWebViewTerminal`(3132–3165)
- `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` 1211–1300 — 에이전트 루프 바인딩 원본
- `Project/AgentZeroWpf/Actors/ActorSystemManager.cs` — 그대로 복사
- `Project/ZeroCommon/Module/ICliGroupInfo.cs`, `Project/AgentZeroWpf/Module/CliTerminalIpcHelper.cs` — 카탈로그 계약·JSON 형식

## 구현 기록 — M0035 터미널 (2026-09-19)

계획 Phase 3을 구현하면서 확정된 사실. 계획과 다른 결정은 굵게.

### NativeWebView 12.1.0 실제 API (리플렉션 덤프)

- `InvokeScript(string) : Task<string>`, `Navigate(Uri)`, `NavigateToString(text, baseUri)`, `Source` 속성.
- `WebMessageReceived` 인자는 `Body`(string) 하나. JS 쪽 진입점은 `window.invokeCSharpAction(body)`.
- `WebResourceRequested`는 `Request`(Uri·Method·Headers)만 노출하고 응답을 합성할 수 없다. 따라서
  **`LocalAssetServer`는 폴백이 아니라 정본**이다(스파이크 불필요 판정).
- `EnvironmentRequested`는 어댑터 생성 전에 오며 `WindowsWebView2EnvironmentRequestedEventArgs.UserDataFolder`,
  `AppleWKWebViewEnvironmentRequestedEventArgs.ScriptHandlerMessageName` 등을 준다. `EnableDevTools`는 Debug에서 켠다.
- **`BeginReparenting(bool)` / `BeginReparentingAsync()`가 공식 API로 존재한다** ("네이티브 컨트롤의 파괴를 부모 변경 동안 지연").
  M0036의 표면 호스트(재부모화 회피) 결정은 유지하되, 이 API로 페인 간 이동을 단순화할 수 있는지 스파이크 항목으로 남긴다.

### 자산 서빙

`Services/LocalAssetServer` = `TcpListener` 위의 손수 만든 HTTP/1.1 응답기. `HttpListener`를 쓰지 않은 이유: Windows http.sys URL 예약이
필요 없고, macOS에서도 같은 코드가 돈다. 127.0.0.1 임시 포트, 64 hex 토큰 경로, 확장자 화이트리스트, `..`/역슬래시/콜론 거부,
루트 밖 실제 경로 거부, `Cache-Control: no-store`, `Connection: close`. `index.html`의 CSP `'self'`는 이 루프백 origin이다.

### 브리지

- JS → 호스트: `term.js` 사본의 `post()`는 `invokeCSharpAction` → `chrome.webview.postMessage` → `webkit.messageHandlers.*` 순으로 전송을
  고르고, 아직 주입되지 않았으면 큐에 담아 50 ms 간격으로 재시도한다(`ready`가 사라지지 않게).
- 호스트 → JS: `XtermMessages.BuildRecvScript`가 `window.zeroHost.recv({...})` 스크립트를 만들고 `InvokeScript`로 보낸다.
  출력은 `out64`(UTF-8의 base64, 64 KiB 단위, 코드포인트 경계 보존)로 가고 xterm.js가 바이트를 직접 디코드한다.
  펌프는 하나(`PumpAsync`): 이전 스크립트가 도는 동안 쌓인 출력을 다음 배치로 합친다.
- 어휘는 WPF와 동일 + `out64`, `hotkey`, `config.hotkeys`(M0036이 표를 채움).
- 처리량(Windows, DOM 렌더러, Debug): 5 MB `type` → 화면에 마지막 줄까지 7.8 s(0.5 s 폴링 포함). 입력 정지 여부는 운영자 스모크 항목.

### ConPTY 사본에서 잡은 것 두 가지 (WPF 원본에는 없는 코드)

1. **표준 핸들 상속.** 콘솔 자식은 `bInheritHandles=false`여도 부모의 표준 핸들 사본을 받는다(호환 규칙). WPF는 GUI라 표준 핸들이
   없어 드러나지 않았지만, stdio가 파이프인 부모(`-cli selftest`, 테스트 러너)에서는 cmd의 출력이 의사콘솔을 우회해 그 파이프로
   나갔다(진단: 파이프에는 conhost의 `?9001h ?1004h` 16자만 오고 마커는 부모 stdout에 찍힘). `CreateProcess` 동안
   `SetStdHandle(..., 0)`으로 비우고 복원한다. `ConPtyHost.Start` 참조.
2. **종료 감지.** ConPTY는 자식 종료 후에도 출력 파이프를 열어 두므로 EOF는 종료 신호가 아니다. 프로세스 핸들을 기다리는 감시
   스레드가 `Exited`를 정확히 한 번 올린다. 그래서 "프로세스가 종료됨" 배너와 `terminal-send`의 "PTY dead" 거부가 Windows에서도
   즉시 동작한다(스모크: `exit` 후 0.3 s 내 로그, 이후 send 거부).

### ZeroCommon 수정 1건 (M0033 버그)

`TerminalEnvironment.PrependPath`가 `Path`/`PATH`가 공존하는 대소문자 구분 환경에서 열거 순서에 따라 엉뚱한 키에 붙였다
(문자열 해시가 프로세스마다 달라 간헐 실패). 정확한 `PATH` 키를 우선한다.

### 테스트

`Project/AgentZeroAvalonia.Tests`(xUnit, 헤드리스): `XtermMessages` 코덱 5, `LocalAssetServer` 해석·실서빙 2, PTY 백엔드 에코/종료 2 +
이론 케이스. CI 두 잡 모두 실행한다. `Avalonia.Headless.XUnit`는 아직 필요 없어 넣지 않았다(M0036 SplitTree도 순수 모델).

## 구현 기록 — M0036 분할창 (2026-09-19)

- **모델** `Layout/WorkspaceLayout<T>`: `PaneNode`(탭 목록·활성 탭) / `SplitNode`(Vertical·자식). 규칙은 WPF `SplitDocument`와 같다 —
  분할은 활성 탭을 바로 뒤의 새 페인으로 옮기고(혼자면 null → 호스트는 같은 정의의 새 터미널을 연다), 같은 방향의 부모는 흡수,
  다른 방향이면 중첩 분할로 감싸고, 빈 페인은 부모를 접으며 같은 방향의 손자는 부모에 병합한다. `FromDock/ToDock`은
  `DockPaneNode`(탭 인덱스)와 매핑하고 `DockPaneLayout.Normalise`로 모르는 탭은 첫 페인에 넣는다.
- **뷰** `TerminalsView`: `LayoutRoot` Grid에 트리를 다시 그리고(페인 = 탭 스트립 + 콘텐츠 슬롯 Border, 분할 = Star/Auto 열·행 +
  GridSplitter 4px), `SurfaceHost` Canvas가 모든 `XtermWebViewTerminalControl`을 보유한다. `LayoutUpdated`마다 각 페인 슬롯의
  사각형을 `TranslatePoint`로 캔버스 좌표로 옮겨 활성 탭 컨트롤에 `Canvas.Left/Top/Width/Height`를 준다(변화 없으면 건너뜀).
  비활성 탭은 `IsVisible=false`로 숨기고 PTY는 계속 돈다. 페인 포커스 이동은 슬롯 사각형으로 `Neighbour`를 고른다.
- **단축키** `Layout/HotkeyTable`: id는 `WindowCommandIds` + 호스트 전용(next/prev-tab, move-tab-next-pane, close-pane,
  focus-left/right/up/down, bot.toggle). 사용자 설정 → WPF 제안 표 → 호스트 기본값. 같은 표를 `config.hotkeys`로 렌더러에
  보내고(`term.js`의 `attachCustomKeyEventHandler`) 창에서는 `KeyDown` 터널 핸들러가 `HotkeyTable.Match`로 본다.
- **CLI** `layout status|split-right|split-down|close-tab|close-pane|add|next-tab|prev-tab|move-tab|focus-<dir>` — 요청은 WPF와
  같은 `{"command":"layout","sub":...}`, 응답에 `panes`와 저장 JSON.
- **영속**: `WorkspaceViewModel.DockLayoutJson = Layout.ToJson(Tabs)`; 변경은 500 ms 디바운스 후 `SaveCliGroups`, 종료 시 즉시.
- 골든 행이 로컬 DB에 없어 `DockPaneLayout.ToJson` 산출로 고정(페인에도 `"Vertical":false`).

## 구현 기록 — M0037 AgentBot (2026-09-19)

- **뷰모델** `AgentBotViewModel`: `Items`(User/Bot/System/Tool/Progress), `Mode`(`ChatModeCycle`, AI 가용성은
  `AgentLoopWiring.Unavailability()`), `Send/CycleMode/NewSession/Cancel`. 액터 콜백은 `Post`(UI 스레드 마샬링, 테스트는 동기)로
  `ApplyProgress/ApplyResult`에 들어온다. 첫 AI 요청은 봇 액터 부착(비동기 `CreateBot` Ask)이 끝날 때까지 보류된다.
- **배선** `AgentLoopWiring.Build` = WPF `EnsureAgentLoopWiring`의 `AgentLoopBindings`(ToolbeltFactory / OptionsFactory /
  AgentLoopFactory). Local은 `LlmService.Llm as LlamaSharpLocalLlm`이 있을 때만(=Windows), External은 전 OS.
- **툴벨트** `WorkspaceToolHost`: 터미널 = `TerminalCatalogJson` + 핸드셰이크(`IntroduceTerminalIfFirst` Ask, `MarkHandshakeSent`,
  `MarkConversationActive`), 키는 `CliCommandRouter.KeySequence` 재사용; 파일 = `FileToolCore`; `open_file`은
  `Process.Start(UseShellExecute)`(Windows ShellExecute / macOS open); `stop_media`의 폴백 키는 Windows에서만; 웹 = headless.
- **CLI** `bot-chat`(WPF와 같은 요청 `{command, message, from}`·응답 `{ok, from, message_length}`), `bot-ask`(신규, AI 턴 시작).
- **ZeroCommon 추가 1건**: `Llm/Tools/GemmaNativeToolCall` — Gemma 4 네이티브 `<|tool_call>call: name{args}<tool_call|>`을
  봉투로 변환. `ExternalAgentLoop`가 `ExtractFirstJsonObject` 전에 한 번 호출(교정 예산 소모 없음). WPF도 같은 혜택.
- 측정: WebnoriA2 · gemma-4-e4b, 2 툴 턴 + done = 13.6 s(턴당 1.2–9.4 s, 모델 응답 시간이 전부).

## 구현 기록 — M0038 설정 (2026-09-19)

- **뷰모델** `SettingsViewModel`(섹션 LLM / CLI / Terminal) + `CliDefinitionItem`(편집 중인 정의 1건; `Validate`, `ApplyTo(entity, protect)`).
  스토어는 전부 WPF 것: `LlmSettingsStore`(키는 Save 시 `SecretProtection.Protect` — Windows DPAPI 사본 / macOS AES-GCM),
  `AppDbContext.CliDefinitions`, `TerminalSettingsStore`.
- **LLM**: External은 전 OS(공급자 콤보 = `ExternalProviderNames.All`, 공급자별 키/URL 필드는 선택 시만 표시, "Test connection"은
  저장 후 `LlmGateway.OpenSession().SendAsync` 45 s). Local 라디오는 Windows에서만 활성(툴팁 안내); 카탈로그 콤보·CPU/Vulkan·
  Vulkan 장치(`VulkanDeviceEnumerator`)·컨텍스트/GPU 레이어·파일 상태·다운로드(`LlmModelDownloader`, 진행률)·Load/Unload
  (`LlmService`, `StateChanged`).
- **CLI 정의**: 이 OS에서 실행 가능한 것만 나열(`TerminalLaunchPlanner.IsAvailableOnThisOs` — macOS에서 `.exe` 숨김), New/Save/Delete
  (built-in 삭제 불가)/▲▼(SortOrder 교환), 실행 파일·키 파일 피커, SSH 필드는 Windows에서만 편집 가능. 저장 후
  `CliDefinitionsChanged` → 셸의 새 탭 메뉴 즉시 갱신.
- **터미널 외관**: 폰트·크기(8–32)·행간(0.8–2.0)·테마(`TerminalThemeCatalog.Names`)·커서 깜빡임·WebGL. 저장 시 `AppearanceChanged` →
  `TerminalsView`가 열린 모든 렌더러에 `config`를 다시 보낸다(라이브). WebGL은 새로 여는 터미널부터.
- XAML 주의: 같은 요소에 `DataContext`와 `IsVisible` 바인딩을 두면 `IsVisible`이 새 DataContext에서 해석된다(컴파일 바인딩 오류) —
  가시성은 바깥 `Panel`에.
