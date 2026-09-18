# AgentZero Lite — Avalonia 호스트 (Windows + macOS)

> English: [README-Avalonia.md](README-Avalonia.md) · 본 매뉴얼: [README-KR.md](README-KR.md) · 설계 기록: [Docs/avalonia-v2/DESIGN.md](Docs/avalonia-v2/DESIGN.md)

![AgentZero Lite — Avalonia 호스트](Docs/avalonia-v2/agent-app.png)

AgentZero Lite는 **하나의 공유 코어 위에 두 개의 GUI 호스트**를 둡니다.

| | WPF 호스트 (`Project/AgentZeroWpf`) | Avalonia 호스트 (`Project/AgentZeroAvalonia`) |
|---|---|---|
| 실행 환경 | Windows | Windows **및 macOS** (arm64 `.app`) |
| 역할 | **Windows 퍼스트 실험실** — 모든 기능은 먼저 여기서 만들고 써 본다 | **멀티 OS 판** — 가치가 검증된 것을 영입한다 |
| 터미널 | WebView2의 xterm.js + ConPTY | `NativeWebView`의 xterm.js + ConPTY(Windows) / Porta.Pty(macOS) |
| 공유 | `Project/ZeroCommon`: 액터, 에이전트 루프, 툴 카탈로그, 설정 스토어, SQLite, CLI JSON 계약 | 동일 |

## 전략: WPF 퍼스트, 그 다음 합류

WPF를 걷어내지 않습니다. Windows에서 무언가를 가장 빨리 시도해 볼 수 있는 곳이 WPF이고(성숙한 UI 스택, 빠른 도구,
하루 대부분을 보내는 곳), 아이디어는 WPF에 먼저 들어가 실제로 쓰이고, 그중 값어치를 한 것이 Avalonia 호스트로
**변환**되어 macOS까지 닿습니다. 변환 자체가 계속 갈고닦을 만한 기술입니다. 변환을 거친 기능은 로직이 `ZeroCommon`으로
내려가고 UI는 얇아지므로 다음 변환이 더 싸집니다.

두 호스트를 정직하게 유지하는 규칙:

1. **UI 의존이 없는 것은 전부 `ZeroCommon`에.** 로직을 복사하지 않고는 변환할 수 없는 WPF 기능이 있다면 로직이 잘못된
   자리에 있는 것이니 먼저 내립니다.
2. **두 호스트는 같은 데이터를 읽고 씁니다** — SQLite 파일, 설정 JSON, 별칭 등록부, 터미널 분할 레이아웃. 한쪽에서
   배치한 워크스페이스는 다른 쪽에서 똑같이 열립니다.
3. **CLI는 하나의 계약입니다.** `AgentZeroLite -cli …`의 요청/응답 JSON은 두 호스트가 같고 전송만 다릅니다
   (WPF는 WM_COPYDATA, Avalonia는 명명 파이프). `-cli help agentzero`는 둘 다에 유효합니다.
4. **기능 변환은 WPF 프로젝트를 건드리지 않습니다.** 변환 커밋의 회귀 기준은 `git diff --stat main -- Project/AgentZeroWpf`가
   비어 있는 것. 공유 버그 수정은 `ZeroCommon`에 넣어 둘 다 혜택을 받습니다.
5. **Windows 전용은 Windows 전용으로, `OperatingSystem.IsWindows()`로 울타리** (LLamaSharp 로컬 LLM, DPAPI, BLE, OS 자동화).
   macOS는 External 공급자 경로와 AES-GCM 비밀 보호를 씁니다.

## 스택

| 계층 | 패키지 / 구성요소 | 버전 | 비고 |
|---|---|---|---|
| 런타임 | .NET | 10.0 (`net10.0`) | 순수 TFM — Avalonia 호스트·ZeroCommon 모두 `-windows` 접미사 없음 |
| UI | Avalonia · Avalonia.Desktop · Avalonia.Themes.Fluent · Avalonia.Fonts.Inter | 12.1.2 | MIT |
| 웹뷰 | Avalonia.Controls.WebView (`NativeWebView`) | 12.1.0 | Windows는 WebView2, macOS는 WKWebView |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 | `ObservableObject`, `[RelayCommand]` |
| 터미널 렌더러 | xterm.js (+ fit, web-links, webgl 애드온) | 5.5.0 (동봉) | `Project/AgentZeroWpf/Wasm/xterm/vendor`를 WPF 호스트와 공유 |
| PTY (Windows) | kernel32 P/Invoke ConPTY (`ConPtyHost`) | Windows 10 1809+ | 네이티브 DLL 동봉 없음 |
| PTY (macOS / Linux) | Porta.Pty | 2.2.2 | forkpty 심, 네이티브 동봉 |
| 액터 | Akka · Akka.Streams · Akka.DependencyInjection | 1.5.67 | WPF 호스트와 같은 토폴로지 |
| 영속 | Microsoft.EntityFrameworkCore.Sqlite · SQLitePCLRaw.bundle_e_sqlite3 | 10.0.0-preview.3 · 3.0.3 | 두 호스트가 DB 파일 하나를 공유 |
| 비밀 보호 | System.Security.Cryptography.ProtectedData (DPAPI, Windows) · AES-GCM 파일 키 (그 외) | 10.0.0 | `SecretProtection.Protector` |
| 로컬 LLM (Windows 전용) | LLamaSharp | 0.26.0 | External 공급자(OpenAI 호환 REST)는 전 OS |
| 폰트 | JetBrains Mono | OFL 1.1 | xterm 번들과 함께 동봉 |

## 빌드·실행·테스트

```bash
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Debug                 # Windows
dotnet build Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r osx-arm64  # macOS 크로스 컴파일
dotnet test  Project/AgentZeroAvalonia.Tests/AgentZeroAvalonia.Tests.csproj             # 헤드리스, 양 OS

Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe                            # GUI
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli status                # 셸에서 조작
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli selftest all          # ipc · secrets · pty, 화면 불필요
```

Rider/VS: `AgentZeroLite.slnx`를 열고 실행 프로필 **AgentZeroLite (Avalonia GUI)** 선택. Windows에서 WPF와 Avalonia GUI는
단일 인스턴스 뮤텍스를 공유하므로 하나를 닫고 다른 하나를 띄웁니다.

macOS: *Avalonia host* 워크플로 실행에서 `AgentZeroLite-Avalonia-v<ver>-osx-arm64.zip`을 받고
`xattr -dr com.apple.quarantine AgentZeroLite.app`(ad-hoc 서명) 후 실행. CLI 래퍼는
`AgentZeroLite.app/Contents/MacOS/AgentZeroLite.sh`. 손 점검표: [Docs/avalonia-v2/macos-smoke.md](Docs/avalonia-v2/macos-smoke.md).

## 변환 완료 목록

| 영역 | 상태 | 비고 |
|---|---|---|
| 셸: 액티비티 바, 워크스페이스 사이드바, 상태 표시줄, 봇 페인 | ✅ | `MainWindow`, `MainWindowViewModel` |
| 워크스페이스 + 터미널 탭, 공유 DB 영속 | ✅ | WPF와 같은 `CliGroups/CliTabs` 행 |
| 터미널: xterm.js 렌더러, 외관, IME, 링크, 헬스 배너 | ✅ | Windows는 ConPTY 사본, macOS는 Porta.Pty |
| 분할창(오른쪽/아래, 페인 닫기, 탭 이동, 방향 포커스), 레이아웃 복원 | ✅ | `WorkspaceLayout<T>`; `CliGroup.LayoutJson`이 WPF와 바이트 동일 |
| 단축키(렌더러가 포커스를 가진 상태 포함) | ✅ | 표 하나; macOS는 Ctrl→Cmd |
| AgentBot: CHT / KEY / AI 모드, 진행·툴 카드, 핸드셰이크 | ✅ | 같은 액터 토폴로지(`/user/stage/bot/loop`) |
| AgentBot UX: 승인 토스트+자동승인, URL 버블, 세션 헤더, 클립보드 첨부, 키 체인, 선택 가능 텍스트 | ✅ | 활성 터미널마다 `AgentEventStream`; 규칙은 `ZeroCommon/Agents` 공유 |
| AgentBot 도킹 페인 **및** 플로팅 창 (Ctrl+Shift+`) | ✅ | 뷰모델 하나에 뷰 둘. WPF와 같이 **하단 도크**(280px, 스플리터, 최대화 시 터미널 90px 유지), 활동바를 제외한 전 영역을 스팬. 오버레이가 아니라 형제 행이다 — macOS에서 터미널 네이티브 웹뷰 위에 그린 Avalonia 콘텐츠는 보이지 않는다. WPF의 나머지 하단 탭(OUTPUT/LOG/NOTE)은 미변환이라 탭 스트립은 없다 |
| 툴벨트: 터미널, 파일, `find_files/open_file/stop_media`, 웹(headless) | ✅ | `WorkspaceToolHost` |
| 설정: External LLM(+테스트), CLI 정의 CRUD, 터미널 외관(라이브) | ✅ | 로컬 LLM 섹션은 Windows에서만 |
| CLI: status, terminal-list/send/key/read/wait/alias(`--alias`), layout, bot-chat, bot-ask, web, selftest | ✅ | 파이프 `AgentZeroLite.cli`; `.ps1` / `.sh` 래퍼 |
| CI: windows-latest + macos-14, win-x64 zip, `.app` 번들 | ✅ | `.github/workflows/avalonia-build.yml` |
| AgentBot 스킬: SkillSync, 스타터팩 임포트, `.agent-zero/` 캐시, 슬래시 자동완성 | ⏳ 미착수 | 하나의 의존 사슬 — SkillSync 없이는 슬래시 목록이 항상 비고, SkillSync는 Windows 전용 셸 탐색으로 Claude CLI를 구동한다 |
| AgentBot 음성(마이크, VAD, STT, TTS, 음소거, 위임) | ⏳ 미착수 | 오디오 캡처 계층을 ZeroCommon으로 먼저 내려야 함; NAudio/WASAPI는 Windows 전용 |
| Browser 페이지(탭 웹뷰), OS 제어, Voice, Vision, Music, Remote, Wearable BLE, 노트/문서 뷰어, Scrap, WebDev 플러그인 | ⏳ 미착수 | 2차; 그때까지 웹 도구는 headless |
| macOS 로컬 LLM(Metal) | ⏳ 조사 | 당분간 External 공급자만 |
| 설치기, Developer ID 서명/공증 | ⏳ | 절차는 `harness/knowledge/_shared/code-signing.md` |

## 변환 플레이북 — 배운 것

**잘 먹힌 순서.** seam 먼저(`ZeroCommon`, 헤드리스 테스트), 그 다음 호스트 골격, 그 다음 터미널(가장 어렵고 가장 가치
있는 조각), 그 위에 얹히는 것들. 마일스톤마다 WPF 빌드를 초록으로 유지하고 CLI로 구동하는 스모크로 끝내서 회귀가
그날 드러나게 했습니다.

**WPF 코드비하인드는 이식할 소스가 아니라 명세로 읽습니다.** WPF 뷰는 뷰모델 없는 큰 코드비하인드입니다. 이식 결과는
같은 동작을 노출하는 뷰모델 + 얇은 뷰이고, WPF 파일은 그 동작이 무엇인지 알려 줍니다. 동작 그 자체가 가치인 곳만
그대로 복사합니다(ConPTY 호스트, 핸드셰이크 문구, 키 별칭 표, CLI JSON 형태).

**주제별 팁**

- *웹뷰.* `NativeWebView`는 폴더를 가상 호스트에 매핑할 수 없고 `WebResourceRequested`에서 응답을 합성할 수도 없습니다.
  루프백 `TcpListener` + 난수 토큰 경로로 자산을 서빙합니다(`LocalAssetServer`). JS→C#은 `window.invokeCSharpAction(string)`,
  C#→JS는 `InvokeScript`. 출력은 base64 배치(`out64`)로 보내 VT 바이트 이스케이프를 없애고, **배치당 스크립트 한 번**으로
  보내 화면 갱신이 반쯤 그려지는 일이 없게 합니다.
- *네이티브 컨트롤과 z-order.* 웹뷰는 네이티브 자식 창이라 macOS에서는 Avalonia가 그리는 어떤 것도 그 위에 보이지 않습니다.
  배너는 렌더러 *위쪽* 영역에, 메뉴는 팝업으로. `NativeWebView`를 재부모화하지 말 것: 모든 터미널을 한 `Canvas`에 두고
  페인 슬롯 위에 위치만 잡습니다(`TerminalsView`).
- *WPF 호스트가 겪지 않은 ConPTY 사실.* stdio가 파이프인 부모(테스트 러너, `-cli`)는 `bInheritHandles=false`여도 콘솔 자식에
  표준 핸들을 넘깁니다 — `CreateProcess` 앞뒤로 비웁니다. 파이프 EOF는 종료 신호가 아닙니다 — 프로세스 핸들을 기다립니다.
  현재 Windows의 ConPTY는 DEC 2026(동기화 출력)을 통과시키지 않으니 렌더러에 보내기 전 한 프레임(~12 ms) 모읍니다.
- *렌더러 입력은 키보드 입력이 아닙니다.* xterm.js는 프로그램의 터미널 질의(장치 속성, 커서 위치)에 키 입력과 같은 `in`
  채널로 답합니다. 이를 헬스 추적기에 넣으면 TUI 화면 갱신마다 "멈춤" 배너가 번쩍입니다. 입력 시도는 CLI·봇 쓰기만 셉니다.
- *Avalonia XAML.* 컴파일 바인딩은 경로를 빌드 시점에 검사합니다 — 틀린 바인딩은 빌드 실패이니 켜 두세요. `Button.Flyout`은
  `Click`보다 *먼저* 열리므로 메뉴는 클릭 핸들러가 아니라 미리 만듭니다. 같은 요소에 `DataContext=`와 `IsVisible=`을 함께
  두지 마세요(`IsVisible`이 새 컨텍스트에서 해석됨) — 바깥 요소로 감쌉니다. `Flyout` 안의 이름 있는 요소는 생성 필드가
  아니니 소유자를 통해 접근합니다.
- *스레딩.* Akka 동기화 디스패처는 `Initialize()` 시점의 `SynchronizationContext.Current`를 붙잡습니다 — null이면
  `AvaloniaSynchronizationContext`를 명시 설치. PTY 콜백은 백그라운드 스레드로 오니 컨트롤을 만지는 것은 전부
  `Dispatcher.UIThread.Post`. CLI 서버는 `Dispatcher.UIThread.InvokeAsync(Func<Task<string>>)`를 await해서 느린 동사(`web`)가
  accept 루프를 막지 않게 합니다.
- *영속과 비밀.* WPF 스토어를 그대로 재사용하고 OS 인식 보호기(`SecretProtection.Protector` = Windows DPAPI, 그 외
  AES-GCM 파일 키)와 `AppPaths` 루트만 더합니다.
- *테스트.* xUnit `Assert.DoesNotContain(string, string)`은 문화권 비교라 ESC를 무시 가능 문자로 봅니다 — 대신
  `text.Contains((char)27)`. 시간 기반 테스트는 고정 대기가 아니라 폴링으로; CI 러너는 느립니다. `C:\` 경로를 전제한
  픽스처는 OS 인식으로 만들거나 macOS에서 제외합니다.
- *CI에서 본 macOS 특성.* 수 ms 안에 끝나는 자식(`sh -c echo`)은 리더가 붙기 전에 사라져 출력을 잃습니다. 실제 셸은
  오래 살지만 자가진단 자식은 잠깐 머물러야 합니다. `sh` 스크립트는 git에 실행 비트를 설정합니다(`git update-index --chmod=+x`).

**어디에 있나**

- 설계 + 마일스톤별 발견: `Docs/avalonia-v2/DESIGN.md` · 미션 `harness/missions/M0033…M0040` · 기록
  `harness/logs/mission-records/M003x-수행결과.md`
- 호스트 소스: `Project/AgentZeroAvalonia/{Terminal,Layout,ViewModels,Views,Services,Cli,macos}`
- 이식을 위해 추가한 공유 seam: `Project/ZeroCommon/Platform/*`, `Services/{IPtyHost,XtermTerminalSession,TerminalHealthTracker,TerminalLaunchPlanner}.cs`,
  `Module/TerminalCatalogJson.cs`, `Security/AesGcmFileSecretProtector.cs`, `Llm/Tools/GemmaNativeToolCall.cs`
