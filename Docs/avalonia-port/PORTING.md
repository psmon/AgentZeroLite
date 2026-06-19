# AgentZeroWpf → AgentZeroAvalonia 포팅 가이드

WPF(Windows 전용) 앱을 **Avalonia UI** 기반 cross-platform(Windows/macOS/Linux) 앱으로
이식하는 작업의 현황·아키텍처·남은 작업·검증 방법을 정리한다.

> 프레임워크 선택: MAUI(macOS=Mac Catalyst, UIKit 샌드박스)는 이 앱의 핵심인
> 터미널·OS자동화·IPC와 맞지 않아 **Avalonia**(진짜 데스크톱, AppKit 접근, WPF XAML과
> 거의 1:1)를 선택했다. 상세 근거는 최초 계획 참조.

대상 프로젝트: `Project/AgentZeroAvalonia/` (TFM `net10.0`, Avalonia 11.3.17).
공유 로직 `Project/ZeroCommon/`은 WPF/Win32-free 유지 — 액터·LLM·EF Core·플랫폼 추상화의 집.
기존 `Project/AgentZeroWpf/`는 회귀 방지를 위해 **무변경 유지**.

---

## 1. 완료된 작업

| 영역 | 구현 | 위치 |
|---|---|---|
| 앱 셸 | Program/App 부트스트랩, 단일 TFM, `-cli` 이중 모드 | `Program.cs`, `App.axaml(.cs)` |
| 액터 시스템 | ActorSystemManager 이식(WPF 의존 없음) | `Actors/ActorSystemManager.cs` |
| DB | EF Core SQLite 초기화/마이그레이션 | `App.axaml.cs` → `AppDbContext.InitializeDatabase()` |
| 채팅 | External LLM(REST) 채팅 — 기존 액터 토폴로지 재사용 | `Views/AgentChatView`, `ViewModels/AgentChatViewModel` |
| 터미널 | cross-platform PTY(Porta.Pty) + 멀티 탭 + 액터 바인딩 | `Services/PtyTerminalSession`, `Views/TerminalView` |
| 에이전트 도구 | 실제 터미널 toolbelt(list/read/send/send_key) | `Tools/PtyTerminalToolbelt`, `Services/TerminalRegistry` |
| 도킹 | Dock.Avalonia 레이아웃(채팅/터미널/문서 도큐먼트) | `Docking/`, `ViewModels/MainWindowViewModel` |
| 문서 뷰어 | Markdown 네이티브 렌더(WebView 불필요) | `Views/MarkdownView` |
| 단일 인스턴스 | Win=Mutex / Unix=파일락 | `ZeroCommon/Platform/ISingleInstanceGuard.cs` |
| 비밀 보호 | Win=DPAPI / Unix=AES-GCM+키파일 | `ZeroCommon/Platform/ISecretProtector.cs` |
| CLI IPC | NamedPipe(Unix=domain socket) | `ZeroCommon/Platform/ICliIpcBridge.cs` |
| 음성(기본값) | Null TTS/STT/재생 + 팩토리 | `ZeroCommon/Voice/NullVoice.cs` |
| 이식성 수정 | `_putenv_s`(Win)/`setenv`(Unix) 분기 | `ZeroCommon/Llm/LlmService.cs` |

---

## 2. 플랫폼 추상화 seam (cross-platform 규칙)

WPF/Win32 종속은 모두 ZeroCommon의 인터페이스 뒤로 숨기고, OS별 구현을 팩토리가 고른다.

| 기능 | 인터페이스 | Windows | macOS/Linux |
|---|---|---|---|
| 터미널 | `ITerminalSession` | ConPTY(Porta.Pty) | forkpty(Porta.Pty 네이티브 shim) |
| 단일 인스턴스 | `ISingleInstanceGuard` | named Mutex | 파일락(FileShare.None) |
| 비밀 보호 | `ISecretProtector` | DPAPI | AES-GCM+키파일 → **(후속) Keychain/libsecret** |
| CLI IPC | `ICliIpcBridge` | NamedPipe | Unix domain socket(동일 API) |
| 음성 TTS | `ITextToSpeech` | **(후속) System.Speech** | **(후속) AVSpeechSynthesizer** |
| 음성 STT | `ISpeechToText` | Whisper.net | Whisper.net(cross-platform 런타임) |
| 오디오 재생 | `IAudioPlaybackQueue` | **(후속) NAudio** | **(후속) AVAudioEngine** |

원칙: ZeroCommon은 BCL + cross-platform 패키지만. Win32 전용 코드는 `OperatingSystem.IsWindows()`
가드 + 비Windows 폴백을 둔다.

---

## 3. 남은 작업 (네이티브 필요 / 무한 범위 — 단계적)

### (A) 터미널 고도화
- **완전한 VT100 렌더러** ✅ — `Iciclecreek.Avalonia.Terminal` 1.0.12(XTerm.NET 에뮬레이션 +
  Porta.Pty, Avalonia 11.3 호환)로 시각 터미널 구현. 색상/커서/TUI 앱 렌더 지원. 멀티탭(Dock).
  셸 spawn/teardown 런타임 검증 완료.
- **남은 사항 — 에이전트→시각 터미널 입력 주입**: Iciclecreek `TerminalControl`은 PTY writer를
  비공개로 둬(`XTerm.Terminal.GenerateKeyInput`은 문자열만 반환, DataReceived는 내부 전용)
  에이전트가 시각 터미널에 send 불가. 현재 에이전트 제어 경로는 별도 `PtyTerminalSession`+
  `PtyTerminalToolbelt`(헤드리스, 검증됨). 통합 방안: XTerm.NET 엔진을 `PtyTerminalSession`으로
  직접 구동하는 커스텀 렌더러 작성(셀 단위 렌더 + Avalonia↔XTerm 키 매핑) — 단일 PTY가
  렌더+에이전트 모두 서비스. (XTerm.NET API: `Terminal.Write`/`GetVisibleLines`/`Buffer.GetLine`/
  `GenerateKeyInput`. 렌더 참조: Iciclecreek `TerminalView.RenderNormalLine`.)
- 헬스 상태머신(no-echo 감지), first-contact 핸드셰이크 — WPF `ConPtyTerminalSession` 참조.
- **macOS 런타임 PTY 실검증**: Porta.Pty가 macOS 네이티브 shim을 포함하나, 여기선
  Windows 런타임 + Mac 크로스컴파일까지만 검증. 실제 Mac에서 `-cli pty-selftest` 실행 필요.
- **NU1903**: EF Core SQLite 전이 의존 `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 취약점 권고 →
  패치 버전 핀 필요(EF 호환 확인 후).

### (B) OS 자동화 (`AgentZeroWpf/OsControl/*`)
스크린 캡처·입력 주입·창 열거는 전부 Win32. 새 인터페이스를 ZeroCommon에 정의하고 OS별 구현:
- `IScreenCaptureService`: Win=GDI BitBlt / macOS=CGWindowListCreateImage(Quartz).
- `IInputSimulator`: Win=SendInput / macOS=CGEventPost.
- `IWindowEnumerator`: Win=EnumWindows / macOS=CGWindowListCopyWindowInfo.
- macOS는 접근성/화면 기록 권한 필요. 샌드박스 정책 확인 필수.

### (C) WebView 패널 (Mermaid/임의 HTML/WebDev 샌드박스)
- WPF는 WebView2 다수 사용. Avalonia 옵션: `WebViewControl-Avalonia`(CEF, 무겁지만 안정),
  `Avalonia.WebView`(플랫폼 네이티브 WebView2/WKWebView, 가벼움).
- Markdown 단순 표시는 이미 Markdown.Avalonia로 해결(네이티브). Mermaid/HTML만 WebView 필요.

### (D) 로컬 LLM (llama.cpp) macOS 네이티브
- 현재 `ZeroCommon/runtimes/win-x64-{cpu,vulkan}/`만 존재 → Mac은 External(REST)만.
- macOS arm64용 llama.cpp(Metal) DLL을 빌드해 `runtimes/osx-arm64/`에 배치하고
  `LlamaSharpLocalLlm`의 NativeLibraryConfig RID 분기 추가. (CI에서 Mac 빌드 필요.)

### (E) 음성 네이티브 구현
- `VoiceServices` 팩토리에 OS 분기 추가: Win=System.Speech/NAudio, macOS=AVFoundation.
- STT는 Whisper.net이 cross-platform이라 비교적 이식 용이.

### (F) 나머지 25개 WPF 뷰 단계적 이식
- 패턴 확립됨: WPF XAML → Avalonia `.axaml`(거의 1:1), 코드비하인드 분리, MVVM.
- 우선순위: SettingsPanel → FileTreePanel → 기타. 각 뷰는 Dock 도큐먼트/툴로 추가.

### (G) CLI 전체 명령 라우팅
- `ICliIpcBridge`로 GUI 서버 + CLI 클라 라우팅 완성(현재 self-test만). WPF `CliHandler`의
  명령 집합(status/terminal-*/bot-chat 등)을 JSON 프로토콜로 이관.

---

## 4. 검증 하네스

GUI 상호작용 없이 헤드리스로 핵심 백엔드를 검증하는 self-test가 `-cli`에 있다:

```bash
AgentZeroLite -cli pty-selftest          # PTY 셸 spawn + I/O 라운드트립
AgentZeroLite -cli toolbelt-selftest     # 에이전트 toolbelt list/send/read
AgentZeroLite -cli ipc-selftest          # NamedPipe 요청/응답
AgentZeroLite -cli actor-term-selftest   # 워크스페이스/터미널 액터 바인딩
```

빌드/테스트:
```bash
dotnet test  Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj      # 헤드리스 단위 테스트(301)
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj    # Windows
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -r osx-arm64 --self-contained  # macOS 이식성 게이트
```

**Mac 실검증 체크리스트**(Mac 확보 시): `dotnet run` GUI 기동 → 도킹/채팅/터미널 표시 →
`-cli pty-selftest`로 `/bin/zsh` spawn 확인 → 단일 인스턴스/비밀/IPC self-test(있다면 추가).

---

## 5. 의존성 매핑 (WPF → Avalonia)

| WPF | Avalonia | 상태 |
|---|---|---|
| WPF XAML | Avalonia `.axaml` | 적용 |
| Dirkster.AvalonDock | Dock.Avalonia 11.3.12 | 적용 |
| EasyWindowsTerminalControl(ConPTY) | Porta.Pty 1.0.7 | 적용 |
| (Markdown 표시) | Markdown.Avalonia 11.0.3 | 적용 |
| Microsoft.Web.WebView2 | WebViewControl-Avalonia / Avalonia.WebView | 후속(4C) |
| AvalonEdit | AvaloniaEdit | 후속(코드 에디터 필요 시) |
| SharpVectors.Wpf | Avalonia.Svg.Skia | 후속(SVG 필요 시) |
| System.Speech / NAudio | AVFoundation / 플랫폼별 | 후속(3E) |
| WMI(System.Management) | Vulkan/Metal 열거 | 후속 |
| DPAPI | ISecretProtector(AES/Keychain) | 적용/후속 |
| WM_COPYDATA+MMF | ICliIpcBridge(NamedPipe) | 적용 |
