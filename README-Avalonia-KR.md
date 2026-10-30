# AgentZero Lite — Avalonia 호스트 (Windows + macOS)

> English: [README-Avalonia.md](README-Avalonia.md) · 본 매뉴얼: [README-KR.md](README-KR.md) · 설계 기록: [Docs/avalonia-v2/DESIGN.md](Docs/avalonia-v2/DESIGN.md)

![AgentZero Lite — Avalonia 호스트](Docs/avalonia-v2/agent-app.png)

AgentZero Lite는 **하나의 공유 코어 위에 두 개의 GUI 호스트**를 둡니다.

| | WPF 호스트 (`Project/AgentZeroWpf`) | Avalonia 호스트 (`Project/AgentZeroAvalonia`) |
|---|---|---|
| 실행 환경 | Windows | Windows **및 macOS** (arm64 `.app`) |
| 역할 | **Windows 퍼스트 실험실 / 레퍼런스 빌드** — 모든 기능은 먼저 여기서 만들고 써 본다 | **공식 판**(v0.25.4부터) — Releases 설치판, Microsoft Store. macOS는 Mac에서 확인 후. 가치가 검증된 것을 영입한다 |
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
dotnet publish Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r linux-x64 --self-contained true -p:UseAppHost=true -o publish-avalonia/linux-x64  # Linux, Windows에서도 가능
dotnet test  Project/AgentZeroAvalonia.Tests/AgentZeroAvalonia.Tests.csproj             # 헤드리스, 양 OS

Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe                            # GUI
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli status                # 셸에서 조작
Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe -cli selftest all          # ipc · secrets · pty, 화면 불필요
```

Rider/VS: `AgentZeroLite.slnx`를 열고 실행 프로필 **AgentZeroLite (Avalonia GUI)** 선택. Windows에서 WPF와 Avalonia GUI는
단일 인스턴스 뮤텍스를 공유하므로 하나를 닫고 다른 하나를 띄웁니다.

**Debug 빌드는 설치된 릴리즈 옆에서 함께 실행됩니다** (`BuildFlavor`): 자체 단일 인스턴스 뮤텍스(`AgentZeroLite-Debug`),
자체 CLI 파이프(`AgentZeroLite.cli.debug` — Debug exe의 `-cli`는 Debug GUI를 조종), 모든 창 제목의 `-DEBUG`, 그리고 자체
**데이터 프로필** `%LOCALAPPDATA%\AgentZeroLite-Debug`(DB·설정·로그·플러그인 — `AppPaths.UseProfile`)를 씁니다. 프로필은
처음 한 번 릴리즈 데이터로 채워집니다(설정 JSON + DB의 `VACUUM INTO` 스냅샷 — 릴리즈가 DB를 열고 있어도 안전). 다시 채우려면
폴더를 지우면 됩니다. 내려받은 모델은 공유합니다(`AppPaths.SharedRoot`). 프로필 이름은 `AGENTZEROLITE_PROFILE`로 웨어러블
호스트에 전달되고, 터미널 탭에는 넘기지 않습니다.

Releases(공식, `v*` 태그에서 `release.yml`): `AgentZeroLite-v<ver>-Setup.exe`(Inno Setup, `installer/AgentZeroLite.iss` —
WPF 설치판이 쓰던 AppId를 그대로 써서 WPF 설치본을 그 자리에서 업그레이드하고, `wearable\`을 포함한다),
`AgentZeroLite-v<ver>-win-x64.zip`. macOS zip은 같은 실행에서 만들어지지만 [macOS 스모크 체크리스트](Docs/avalonia-v2/macos-smoke.md)를
Mac에서 돌릴 때까지 `macos` 아티팩트로만 남긴다. WPF 레퍼런스 설치판은 필요할 때만:
`release-wpf.yml`(`installer/AgentZeroLite-WPF.iss`, 별도 AppId·폴더)이 릴리스에 `AgentZeroLite-WPF-v<ver>-*`을 붙인다.

macOS(테스트용): *Build & Release* 실행의 `macos` 아티팩트(또는 *Avalonia host* 실행의 `AgentZeroLite-Avalonia-osx-arm64`)를 받아 풀고
`xattr -dr com.apple.quarantine AgentZeroLite.app`(ad-hoc 서명) 후 실행. CLI 래퍼는
`AgentZeroLite.app/Contents/MacOS/AgentZeroLite.sh`. 손 점검표: [Docs/avalonia-v2/macos-smoke.md](Docs/avalonia-v2/macos-smoke.md).

Linux(Ubuntu 데스크톱, 테스트 빌드): `Project/AgentZeroAvalonia/linux/build-deb.sh <publish-dir> <out-dir> <version>`이
linux-x64 publish를 `AgentZeroLite-v<ver>-linux-x64.deb`(`/opt/agentzerolite`에 설치, PATH에 `agentzerolite`, 앱 메뉴
항목 + 아이콘; `Depends`는 WebKitGTK 4.1·GTK 3·ICU·OpenSSL·fontconfig — `sudo apt install ./….deb`가 함께 설치)와 포터블
`.tar.gz`로 만든다. 두 CI 워크플로가 모두 이것을 만들고, 작업 안에서 자기가 만든 `.deb`를 설치해 실행해 본다.
사용자는 `curl -fsSL https://psmon.github.io/AgentZeroLite/Home/install-linux.sh | bash`로 설치한다(`Home/install-linux.sh`:
최신 `v*` 릴리스의 `.deb`를 `/tmp`에 받아 apt로 설치). `~/Downloads`에서 바로 `.deb`를 설치하면 apt가
*"couldn't be accessed by user '_apt' … (13: Permission denied)"*를 띄운다 — Ubuntu 홈 폴더가 `0750`이라 apt의 샌드박스
사용자가 읽지 못해서다. 해는 없지만 실패처럼 보이므로 `/tmp`를 쓴다.
**Linux 장비 없이 GUI 시험하기**(Windows 11 + Docker Desktop, WSLg로 표시 — X 서버 설치 불필요):
`docker build -t azl-ubuntu-gui Project/AgentZeroAvalonia/linux/test` 후
`bash Project/AgentZeroAvalonia/linux/test/run-gui.sh publish-avalonia/linux-x64`(직접 조작),
`… --terminal`(워크스페이스를 넣고 bash 탭을 열어 입력·화면 읽기, 스크린샷 저장) 또는
`install-test.sh <deb>`(새 `ubuntu:24.04`에 apt로 패키지 설치). `run-tests.sh`는 .NET SDK 컨테이너에서 헤드리스
스위트를 돌린다. 현재 Linux에 없는 것: 오디오 캡처, OS 제어, 플러그인 프레임 캡처, 로컬 LLM, SSH 정의, 웨어러블 호스트 —
macOS와 같은 울타리다.

Microsoft Store(Windows): `Project/AgentZeroAvalonia/msix/build-msix.ps1` 가 MSIX(x64, 서명은 Store가 함)를 만든다.
스토어판은 자기 데이터(DB·설정)를 패키지 안에 격리해서 쓴다 — 의도된 동작이며 일반 설치판과 공유하지 않는다. 대신
터미널 탭과 설정의 설치 버튼은 desktop app breakaway 정책(`DesktopAppBreakaway`)으로 프로세스를 띄우므로, 사용자가
거기서 설치·실행한 것(npm -g, claude, git…)은 실제 사용자 환경에 남는다. 제한된 기능은 `runFullTrust` 하나뿐이다.
절차, 로컬 점검, 붙여 넣을 등록 문구:
[Docs/avalonia-v2/microsoft-store.md](Docs/avalonia-v2/microsoft-store.md).

## 변환 완료 목록

| 영역 | 상태 | 비고 |
|---|---|---|
| 셸: 액티비티 바, 워크스페이스 사이드바, 상태 표시줄, 봇 페인 | ✅ | `MainWindow`, `MainWindowViewModel` |
| 워크스페이스 + 터미널 탭, 공유 DB 영속 | ✅ | WPF와 같은 `CliGroups/CliTabs` 행 |
| 터미널: xterm.js 렌더러, 외관, IME, 링크, 헬스 배너 | ✅ | Windows는 ConPTY 사본, macOS는 Porta.Pty |
| 분할창(오른쪽/아래, 페인 닫기, 탭 이동, 방향 포커스), 레이아웃 복원 | ✅ | `WorkspaceLayout<T>`; `CliGroup.LayoutJson`이 WPF와 바이트 동일 |
| 터미널을 별도 창으로 분리하고 다시 도킹(WPF의 Float / Dock): 탭 메뉴, 페인의 ⤢/⤡, Ctrl+Alt+F / Ctrl+Alt+D, `-cli layout float\|dock` | ✅ Windows · ⏳ macOS 확인 | 탭의 렌더러 자체를 한 디스패처 턴 안에 `TerminalWindow`로 옮기므로 웹뷰·스크롤백·PTY가 그대로 이어진다(Windows에서 측정). 탭은 원래 페인의 레이아웃에 남아 있어 도킹하면 그 자리로 돌아가고, 저장되는 레이아웃은 바뀌지 않는다 — 재시작하면 모든 탭이 도킹된 상태로 돌아온다. 창을 닫으면 도킹되며, 분리된 탭의 분할/이동은 WPF처럼 거부한다 |
| 단축키(렌더러가 포커스를 가진 상태 포함) | ✅ | 표 하나; macOS는 Ctrl→Cmd |
| **커맨드 팔레트**(WPF의 Ctrl+J 팝업)를 **Ctrl+Shift+P**(macOS는 Cmd)로, 터미널에 포커스가 있어도 동작: 모든 터미널(에이전트 상태 포함)과 워크스페이스, 정의별 새 터미널, 모든 단축키 명령, 코드 패널의 면(파일, git log, diff, AI 변경), 모든 페이지와 설정 섹션을 퍼지 검색; ↑↓ / Enter / Esc. **설정 → Shortcuts**: 이 호스트가 실행하는 모든 명령, 기본값, 내 키(빈칸 = 기본값, `none` = 키를 터미널에 돌려줌), 충돌은 어느 명령과 겹치는지 표시; 저장하면 열린 모든 터미널에 즉시 반영 | ✅ Avalonia 먼저 · ⏳ WPF는 Ctrl+J 유지 | Ctrl+J가 아닌 이유: 터미널에서 그것은 줄바꿈(LF)이고 Claude Code의 개행 키다. `HotkeyTable.Effective`가 이제 WPF의 `WindowCommandIds`만이 아니라 이 호스트의 모든 id에 대해 사용자 바인딩을 읽고(사용자 키 우선, `none`은 기본값 제거), 페이지도 같은 함수로 검증하므로 실제 키와 어긋나지 않는다. `shortcut-settings.json`은 공유 파일: 이 호스트가 실행하지 않는 항목은 그대로 두고, 여기서 무언가를 바꿨을 때만 스위치를 켠다. 팔레트는 독립 창(macOS 오버레이 규칙)이며 ZeroCommon `FuzzyMatcher` 위에서 동작 |
| AgentBot: CHT / KEY / AI 모드, 진행·툴 카드, 핸드셰이크 | ✅ | 같은 액터 토폴로지(`/user/stage/bot/loop`) |
| AgentBot UX: 승인 토스트+자동승인, URL 버블, 세션 헤더, 클립보드 첨부, 키 체인, 선택 가능 텍스트 | ✅ | 활성 터미널마다 `AgentEventStream`; 규칙은 `ZeroCommon/Agents` 공유 |
| AgentBot 도킹 페인 **및** 플로팅 창 (Ctrl+Shift+`) | ✅ | 뷰모델 하나에 뷰 둘. WPF와 같이 **하단 도크**(280px, 스플리터, 최대화 시 터미널 90px 유지), 활동바를 제외한 전 영역을 스팬. 오버레이가 아니라 형제 행이다 — macOS에서 터미널 네이티브 웹뷰 위에 그린 Avalonia 콘텐츠는 보이지 않는다. WPF의 나머지 하단 탭(OUTPUT/LOG/NOTE)은 미변환이라 탭 스트립은 없다 |
| **코드 뷰** — 활성 워크스페이스의 파일을 하단 패널에서 보기(VS Code 탐색기 + 읽기 전용 에디터, WPF NOTE의 파일 트리 + 문서 뷰어): 액티비티 바 📄, Ctrl+Alt+E, `-cli code open [path] \| close \| status` | ✅ Windows · ⏳ macOS 확인 | 하단 패널이 VS Code처럼 탭이 됐다 — 🤖 BOT \| 📄 CODE가 한 도킹 영역(높이·최대화)을 함께 쓰고, ⧉ / ✕는 앞에 있는 탭에 작동하며 코드 뷰도 봇처럼 별도 창으로 띄울 수 있다. 문법 색칠은 VS Code의 TextMate 문법 + Dark+ 그대로(AvaloniaEdit 12 + AvaloniaEdit.TextMate, `libonigwrap` 포함 약 8 MB). 파일 규칙은 ZeroCommon `Workspace/WorkspaceFiles`(폴더 단위로 UI 스레드 밖에서 나열, WPF 트리의 숨김 목록 + 단순 `.gitignore`, 바이너리 판별, 2 MB 표시 한도, 경로 이탈 차단). 마크다운은 **렌더링해서** 연다(ZeroCommon `Workspace/MarkdownHtml`의 Markdig: GFM 표/작업 목록, YAML 머리말 숨김, 원시 HTML 이스케이프, 워크스페이스 이미지는 data URI로 인라인). **mermaid 다이어그램**은 WPF의 오프라인 `mermaid.min.js`(복사 없이 링크, `Wasm/markdown`을 전용 루프백 자산 서버가 제공, CSP는 같은 출처 스크립트만)로 그린다. **◧ Preview / ‹› Plain**으로 원문과 전환하고, 링크는 워크스페이스 파일이면 코드 뷰에서, 웹 주소면 브라우저에서 연다. `-cli code view preview\|plain`, `code status`가 `mermaid n/n`(그려진 수)을 알려 준다. 아직 없음: PDF, 편집 |
| 툴벨트: 터미널, 파일, `find_files/open_file/stop_media`, 웹(headless) | ✅ | `WorkspaceToolHost` |
| 설정: External LLM(+테스트), CLI 정의 CRUD, 터미널 외관(라이브) | ✅ | 로컬 LLM 섹션은 Windows에서만 |
| 설정: 에이전트 CLI 설치(Claude, Codex, AgentOne, Netclaw, Hermes) | ✅ 두 호스트 모두 | 빌트인 행은 셸 + 에이전트 명령이라, 에이전트를 설치하지 않아도 행 자체는 유효하다 — 그러면 탭이 "claude : The term 'claude' is not recognized"를 뱉고, 앱이 고장난 것처럼 보인다. 그런 정의를 선택하면 PATH에 있는지 보여주고 설치를 제안한다: npm 도구는 **모든 OS에서 npm -g**(`@anthropic-ai/claude-code` / `@openai/codex` / `@webnori/agent-one`; winget은 패키지가 릴리스보다 늦고 Windows 전용이라 뺐다), npm에 없는 Netclaw·Hermes는 **공식 설치 스크립트**. AgentOne·Netclaw 행은 `agent-one chat` / `netclaw chat`으로 실행한다(인자 없는 명령은 도움말만 출력). 규칙과 패키지는 `ZeroCommon/Services/AgentCliTools`에 있고, WPF도 CLI Definitions 탭에 같은 패널을 채택했다 |
| 앱 아이콘 | ✅ | WPF 호스트의 `agentzero.ico`를 프로젝트에 링크해 exe 아이콘과 창 아이콘으로 쓴다. `.app` 번들용 `macos/AgentZeroLite.icns`는 같은 파일에서 생성했다 |
| AgentBot: AI 답변마다 "⚑ Report" (Microsoft Store 정책 11.16) | ✅ Avalonia 먼저 · ⏳ WPF | 신고 패널(사유 + 메모)이 답변을 인용한 메일 또는 GitHub 이슈를 미리 채워 연다. 로직은 `ZeroCommon/Llm/AiContentReport`에 있어 WPF 봇 창도 그대로 쓸 수 있다. 스토어 인증이 요구해 여기서 먼저 만들었다 |
| 설정: AI 모드 툴 체인 최대 턴 | ✅ (WPF에도 역이식) | `AgentLoopMaxTurns`(기본 12, 1–200으로 클램프) → `AgentLoopOptions.MaxIterations`. 두 호스트 모두 턴당 토큰 상한과 temperature만 넘기고 있어 턴 예산이 사실상 상수였고, 긴 작업은 "max iterations (12) reached without 'done'"로만 끝날 수 있었다. 여기서 만든 뒤 WPF의 `AgentBotWindow` + LLM 탭의 *AIMODE Turns* 로 적용했으므로, 설정 파일이 공유되는 만큼 두 호스트가 같은 값을 쓴다 |
| 설정: AI 모드용 **MCP 서버** — stdio / Streamable HTTP / SSE, 헤더·환경변수 값 암호화 저장, 툴별 허용 스위치, 테스트 호출, `mcpServers` JSON 가져오기 | ✅ Avalonia 먼저 · ⏳ WPF | `ZeroCommon/Mcp` (자체 클라이언트: `McpClient` + 트랜스포트 3종, `McpSettingsStore` → `mcp-settings.json`, `McpHub`). 에이전트는 허용된 툴을 `server.tool`(`memorizer.search`)로 본다: `AgentLoopOptions.DynamicTools`가 그 이름을 루프의 알려진 툴 집합, 로컬 GBNF, 시스템 프롬프트에 더하고, 호출은 `IAgentToolbelt.CallDynamicToolAsync`로 간다. 프롬프트는 *Load tools* 때 캐시한 툴 목록으로 만들어서 세션 시작이 서버를 기다리지 않으며, 허용 목록은 호출 시점에 다시 강제된다. 로컬 문법은 문자열만 내보내므로 인자는 툴 스키마에 맞게 변환한다(`"5"` → 5, `"a, b"` → 배열). 변경은 다음 AI 세션(↻ New session)부터 적용된다. WPF는 툴벨트·옵션 팩토리를 `McpHub`에 연결하고 설정 탭만 더하면 된다 |
| CLI: status, terminal-list/send/key/read/wait/alias(`--alias`), layout, bot-chat, bot-ask, web, mcp, selftest | ✅ | 파이프 `AgentZeroLite.cli`; `.ps1` / `.sh` 래퍼 |
| CI: windows-latest + macos-14, win-x64 zip, `.app` 번들 | ✅ | `.github/workflows/avalonia-build.yml` |
| **Linux(Ubuntu 데스크톱 x64)**: 빌드, 헤드리스 스위트, 자체 테스트, `.deb` + `.tar.gz`, 릴리스 첨부 | ✅ 빌드 · ⏳ 사람이 데스크톱 확인 | `avalonia-build.yml`의 작업 C와 `release.yml`의 `linux` 작업(Windows를 붙잡지 않음). Docker + WSLg의 Ubuntu 24.04에서 측정: GUI 창이 뜨고, bash 탭이 Porta.Pty와 WebKitGTK 렌더러로 돌고(입력 + 화면 읽기), 코드 뷰가 마크다운을 렌더링하고, `-cli`가 Unix 소켓 파이프로 응답. 그 과정에서 고친 것: POSIX 행이 `/bin/zsh`를 가정했다(기본 Ubuntu에는 없음) — 이제 `ZeroCommon/Platform/PosixShell`이 실제로 있는 셸만 넣고 에이전트는 `$SHELL` 또는 bash로 실행; 새 Unix 계정에서 `LocalApplicationData`가 `""`라 데이터 폴더가 작업 폴더에 생겼다 — `AppPaths.LocalAppData`가 이제 폴더를 만든다 |
| 설정: Voice(STT Whisper.net / OpenAI, TTS Supertonic / OpenAI, 모델 다운로드, 마이크 없는 Speak + Round trip 테스트) | ✅ | WPF·웨어러블 호스트와 같은 `voice-settings.json`. 이식 가능한 부분은 ZeroCommon으로 내렸다: `Voice/WhisperNetStt`(WPF `WhisperLocalStt`에서 WMI GPU 선택만 뺀 것), `Voice/WavPcm`(NAudio 없이 WAV → 16 kHz 모노), `Voice/PortableVoiceRuntime`(WPF가 아닌 호스트가 돌릴 수 있는 공급자 생성). 재생은 `Services/WavPlayer`: Windows는 winmm `PlaySound` (비동기라 Stop이 즉시 먹음), macOS는 `afplay`. 저장은 이 화면의 필드만 쓰고, WPF 전용 공급자(SAPI, LocalGemma)는 선택된 채로 둔다. Supertonic이면 목소리를 `TtsVoice`와 `SupertonicVoice` 둘 다에 쓴다 — 실측에서 둘이 어긋나 있었다(데스크톱 M2, 워치 F1). 마이크 캡처는 아래 AgentBot 음성 행에 남아 있다 |
| 모델 로드: 사용 시, 저장 시, 시작 시(유형별 선택) | ✅ Avalonia 먼저 · ⏳ WPF | `ZeroCommon/Models/ModelResidency`. 음성 모델은 프로세스에 한 번 로드해 유지(Voice 탭 테스트가 클릭마다 다시 로드했다). 저장하면 고른 음성·STT를 백그라운드로 로드하고 바뀐 음성은 내린다. AI 모드는 첫 턴에 로컬 모델을 로드한다(Load 버튼 불필요). LLM·Voice 화면의 "시작 시 로드"(`model-preload.json`, 기본 모두 off)는 창이 뜬 뒤 백그라운드로 로드한다 |
| 설정: Voice — **KittenTTS 2**(온디바이스, 한국어 포함 다국어, Windows) | ✅ Avalonia 먼저 · ⏳ WPF | 음성 LM은 앱의 llama.cpp(Vulkan)에서 Q8_0 GGUF로, chatterbox-turbo 디코더는 CPU ONNX로, 텍스트 정규화는 C#으로 이식(`Voice/KittenTts2`). 선택: 설치된 묶음 manifest의 보이스, 답변 언어, 장치(Auto / GPU / CPU — 쓸 만한 GPU가 없는 PC는 CPU). Download는 AgentZero GitHub Release `model-kittentts2-v1`에서 약 3.3 GB 묶음을 받고(이어받기, 파일별 SHA-256 검증), Install은 폴더에서 복사한다. 데스크톱과 시계가 같은 `IVoiceEngine`(`VoiceEngines.Create`)으로 말하고, 웨어러블 호스트의 엔진별 코드도 이것으로 바뀌었다. WPF 음성 화면에는 없다: 거기서 저장하면 TTS가 Off가 된다 |
| Wearable 페이지: 링크 / 브레인 / 허용 폴더 / 연결 시 설정, Start / Stop / Test brain / Test HUD, 호스트 로그 실시간 | ✅ Windows | `ZeroCommon/Wearable/WearableHostLauncher`(WPF `WearableHostProcess`와 종료 시 함께 죽이는 Job Object를 내린 것)와 `WearableDiagnostics`. 액티비티 바 ⌚, 호스트가 WinRT BLE라 Windows가 아니면 숨긴다. 페이지에는 기본 작업공간 **`home`**과 **OS 제어** 스위치(워치의 데스크톱 도구, 감사 로그 링크)도 있으며, 규칙은 ZeroCommon(`Wearable/Actors/OsToolActor`, `Os/IOsControl`)에 있다. csproj는 Windows 빌드에서만 `ZeroWearable`을 빌드하고 `wearable\`로 복사·publish한다. **Store MSIX에는 아직 넣지 않았다** (`build-msix.ps1`가 `-p:IncludeWearableHost=false`를 넘김): 패키지 안의 BLE 자식 프로세스는 별도의 인증 문제다 |
| AgentBot 스킬: SkillSync, 스타터팩 임포트, `.agent-zero/` 캐시, 슬래시 자동완성 | ⏳ 미착수 | 하나의 의존 사슬 — SkillSync 없이는 슬래시 목록이 항상 비고, SkillSync는 Windows 전용 셸 탐색으로 Claude CLI를 구동한다 |
| 설정: Voice — **스트리밍 STT**(sherpa-onnx `OnlineRecognizer`; **Nemotron 3.5 ASR이 공식 모델**, 한국어 Zipformer는 가벼운 대안; 말하는 중 텍스트) | ✅ | `SherpaStreaming`이 `PortableVoiceRuntime`의 첫 번째 STT 공급자다(실시간 마이크는 `CreateStreamingStt`, Round trip·음성 노트·"시작 시 로드"는 `StreamingSttBatchAdapter`). 화면에 모델 선택기(Nemotron에 *official* 표시), Download(k2-fsa `asr-models` 릴리스), 끝점 무음(ms), **🎙 Listen live**를 더했다: 마이크 → 선택한 모델, 말하는 동안 *Heard*에 단어가 나타난다 |
| AgentBot 음성 — **마이크 입력**(입력창의 🎙) | ✅ Windows · ⏳ macOS | `ZeroCommon/Voice/Streaming/VoiceMicSession`이 공용 캡처 계층의 마이크를 ZeroCommon 음성 액터(`/user/stage/voice`, WPF와 같은 Akka.Streams 그래프)에 넣는다. 스트리밍 공급자면 인식기의 부분 결과가 입력창을 채우고 끝점에서 문장이 전송된다(한국어 클립을 실시간 속도로 흘려 측정: 말이 끝나고 ~0.9초 뒤 전송). 다른 공급자는 VAD + 배치 그래프로 돈다. 부분 결과는 절대 전송하지 않는다 — 에이전트 루프는 확정된 말에서 시작한다. macOS는 `IAudioCapturePlatform`이 필요하다(아직 마이크가 없어 🎙을 숨긴다) |
| AgentBot 음성 — 답변 TTS, 끼어들기, 음소거, 위임 | ⏳ 미착수 | 음성 액터의 OUTPUT 그래프는 ZeroCommon에 있다; 재생 큐(`IAudioPlaybackQueue`)가 WPF의 NAudio 전용이다 |
| **Plugins** 페이지(WPF의 WebDev): 빌트인 샘플 + 설치된 플러그인, 공식 목록 / GitHub 폴더 URL / .zip 설치, 제거, 플러그인별로 캐시되는 웹뷰. 음성 노트·라이브 음악·비전 포함 | ✅ Windows · ⏳ macOS | WPF용으로 작성된 플러그인이 **수정 없이** 돈다. op 테이블은 `ZeroCommon/Browser/ZeroBridgeDispatcher`로 내렸고(WPF `WebDevBridge`는 메시지를 그쪽으로 나르기만 함), 이식 가능한 호스트는 `ZeroBrowserCore`(chat, summarize, classify, oEmbed, YouTube/MP3 플레이리스트, 주어진 프레임에 대한 vision), 공용 이벤트는 `ZeroBridgeEvents`, `mp3.local` 경로·Range 규칙은 `Mp3Resource`, 매니페스트·카탈로그·설치기는 `ZeroCommon/Browser/Plugins`로 옮겼다. `zero-bridge.js`는 WPF 파일을 바이트 동일하게 링크한다. `Services/Plugins/PluginAssetServer`가 `zero.local` / `plugin.local` / `mp3.local`을 루프백으로 서빙하고(텍스트 응답은 주소를 재작성, `localStorage`가 유지되도록 포트를 기억), 모든 페이지 맨 앞에 `Wasm/common/zero-webview-shim.js`를 주입해 두 엔진 모두에서 `chrome.webview`를 대신한다. 플러그인 폴더는 WPF와 같다. **음성 노트 / 라이브 음악**: 파이프라인(VAD, STT, 화자 분리, AST 루프)을 ZeroCommon `Audio/IAudioCapturePlatform` 위의 `ZeroBrowserCore`로 옮겼다 — Windows는 `WindowsAudioCapture`(NAudio WaveIn + WASAPI 루프백, WPF 캡처 서비스도 이제 이것을 감싼다), 그 외 OS는 `UnsupportedAudioCapture`(→ `ok:false, unsupported-host`). **비전**: `Services/Plugins/IWebViewFrameCapture` — `WebView2FrameCapture`가 `IWindowsWebView2PlatformHandle`을 통해 `ICoreWebView2::CapturePreview`(vtable 슬롯 30)를 호출한다. Windows에서 `-cli plugin call`로 실측: Whisper medium(GPU)이 루프백 음성을 화자 라벨(Sherpa)과 함께 전사, AST가 440+880 Hz 톤을 "Sine wave 91%"로 분류, Florence-2가 캡처한 플러그인 프레임에서 952 ms에 동작. macOS는 `IAudioCapturePlatform`(AVAudioEngine / ScreenCaptureKit)과 `IWebViewFrameCapture`(WKWebView 스냅샷) 구현이 필요하다 — 미착수. 아직 아닌 것: 플로팅 창 분리 |
| 플러그인을 별도 창으로 분리하고 다시 도킹(WPF의 FloatingWebDevWindow): Plugins 툴바의 **⧉ Detach**, 📌 항상 위, 투명도, `-cli plugin detach\|dock <id>` | ✅ Windows · ⏳ macOS 확인 | 플러그인의 웹뷰 자체를 한 디스패처 턴 안에 `PluginWindow`로 옮기므로 페이지가 계속 돈다(창에서도 브리지 호출과 `vision` 프레임 캡처가 된다). 항상 위·투명도·창 위치/크기는 플러그인별로 WPF의 `WebDev\floating-prefs.json`에 기억되어(ZeroCommon `PluginWindowPrefs`, WPF처럼 DIP 단위) 두 호스트가 같은 자리를 복원한다. 창에는 소유자가 없어서 메인 창을 최소화해도 터미널 옆에 남는다. 창을 닫으면 도킹된다 |
| 설정 → **Models**: AST AudioSet, Florence-2, Sherpa 화자 분리 — 상태, 다운로드, 화자 라벨 스위치 | ✅ | WPF의 Music / Vision / Diarization 탭이 쓰는 ZeroCommon 다운로더 그대로; 같은 `models\` 폴더와 `diarization-settings.json` |
| 사이드바 **ACTIVE SESSIONS**: 모든 워크스페이스의 실행 중인 터미널 전체, 클릭하면 그 세션으로 이동; 행마다 에이전트 상태(blocked / working / done / idle 점 + 칩), 헤더와 창 제목에 attention 수 | ✅ | WPF SESSIONS 패널의 herdr H1/H2 상태. 감지는 `ZeroCommon/Agents/AgentActivityTracker`로 내렸고(WPF `AgentStateMonitor`는 이제 타이머로 이것을 돌린다), 두 호스트가 같은 매니페스트와 "seen" 규칙으로 화면을 판정한다. `-cli agent-state`는 WPF 계약 그대로 응답. Claude Code 탭은 화면보다 Claude 자신의 상태 파일(`~/.claude/sessions/<pid>.json`: busy / waiting / idle)을, Codex 탭은 그 스레드 rollout의 턴 이벤트(`task_started` / `task_complete`)를 먼저 본다 — 끝난 턴은 1초 안에 **done**으로 표시 |
| **에이전트 호출 알림**: 터미널의 에이전트가 blocked가 되거나 아직 보지 않은 턴을 끝내면 화면 구석에 작은 팝업(클릭 → 그 터미널)과 작업 표시줄 깜빡임(Windows) / Dock 바운스(macOS); 설정 → Notifications(무엇을, 언제, 어떻게, 팝업 시간, Test); `-cli attention status \| test` | ✅ Avalonia 먼저 · ⏳ macOS 확인 | 규칙은 ZeroCommon `Agents/AttentionNotifier`(전환 순간에만 알림, 터미널별 20초 쿨다운, 첫 패스는 조용히, 요청하지 않으면 앱이 활성일 때 조용히) + `AttentionSettingsStore` → `attention-settings.json`. 팝업은 오버레이가 아니라 독립 최상위 창(`AttentionPopup`, topmost, 포커스를 빼앗지 않음) — 터미널의 네이티브 웹뷰 위에 그린 Avalonia 표면은 macOS에서 보이지 않는다. 깜빡임/바운스: `Services/WindowAttention`(`FlashWindowEx` / `-[NSApplication requestUserAttention:]`); Linux는 팝업만. WPF는 자기 작업 표시줄 깜빡임을 유지 |
| **Budget**: 터미널의 에이전트가 이번 달에 쓴 비용 — 설정 → Budget(월 상한 대비 지출, 상한 근접 시 주황 / 초과 시 빨강; 모델별·프로젝트 폴더별; 전체 누적; 모델별 가격 오버라이드와, 가격이 매칭되지 않은 모델마다 한 번에 행을 추가하는 버튼), 상태 표시줄의 지출 칩(클릭 → 그 페이지, 1분마다 다시 읽음), GUI 없이 `-cli cost [--all] [--json]` | ✅ Avalonia 먼저 · ⏳ WPF는 새 가격표만 | ZeroCommon `Telemetry/CostReport`: 합계는 데이터베이스에서 묶는다(비용은 토큰에 선형 — 12만 8천 건이 약 1초), cwd는 그것을 담은 가장 안쪽 워크스페이스 폴더로 집계(에이전트는 `Assets/`, `bin/` … 으로 `cd`한다), 매칭되지 않는 모델은 조용히 $0이 되지 않고 목록으로 표시. `TokenCostCalculator.DefaultTable`에 세대별 Claude 행(Fable 5.x $10/$50, Opus 5.5 $4/$20, Opus 5–4.5 $5/$25, Sonnet 5.x $2/$10, Haiku 5.5 / 4.5)을 기존 계열 폴백 앞에 추가 — 이전 표는 Opus 5.5를 Opus 4 가격 $15/$75로 계산해 약 6배 높았다; WPF Budget 탭도 같은 표를 읽는다 |
| Claude / Codex 탭의 **세션 복원**: 프로세스 시작 전에 그 에이전트가 워크스페이스 폴더에 대해 기록해 둔 세션을 대화상자로 나열(최신이 위: 제목, 첫 프롬프트, 경과 시간, 크기) — *Resume selected*는 `claude --resume <id>` / `codex resume <id>`로 다시 띄우고, *New session*은 새로 시작; "Don't ask again"은 에이전트별로 질문을 끈다(Settings → Terminal → Session resume에서 다시 켬) | ✅ Avalonia 전용 | 에이전트 자신의 저장소에서 읽는다(`$CLAUDE_CONFIG_DIR`/`~/.claude/projects/<slug>`, `$CODEX_HOME`/`~/.codex/sessions` + `session_index.jsonl`), 앞/뒤 일부만 읽음 — `ZeroCommon/Agents/AgentSessionCatalog`, `AgentResumeLaunch`, `agent-resume.json`. Codex가 스스로 띄운 서브에이전트 스레드(guardian review)는 제외 |
| 사이드바 **git 워크트리**: 저장소의 작업 트리인 워크스페이스는 브랜치를 표시하고, 그 저장소의 다른 워크트리를 하위(⎇)에 나열 — 열린 것은 전환, 감지만 된 것은 클릭하면 워크스페이스로 열리고 그 아래에 중첩 표시; 에이전트가 만든 워크트리는 5초 안에 나타나고 제거되면 사라진다 — Claude Code의 `<repo>/.claude/worktrees/<name>`(브랜치)과 Codex의 `$CODEX_HOME/worktrees/<id>/<repo>`(detached; `codex <id> (<commit>)`으로 표시되고 `<repo>`가 하나 더 생기지 않게 `<repo>-codex-<id>`로 열림)를 모두 인식 | ✅ Avalonia 전용 | `ZeroCommon/Workspace/GitWorktreeScanner`가 git 자체 파일(`.git/worktrees/*/gitdir`, `commondir`, `HEAD`)을 읽는다 — `git` 프로세스 없음 — UI 스레드 밖에서 `PathAvailability` 뒤로; 중첩 규칙은 `WorktreeNesting`(메인 트리가 호스트, 없으면 처음 나열된 트리). 표시만 중첩되고 평평한 워크스페이스 순서(CLI 그룹 인덱스, 저장된 행)는 그대로. `-cli worktree list \| open \| close <path\|branch>` |
| 사이드바: 워크스페이스 우클릭 → **Open folder** / 워크트리 → **Open worktree folder**, 그리고 **절대·상대 경로 복사**(워크스페이스: `~/…`; 워크트리: 저장소 행 기준, 예 `.claude/worktrees/x`); 하단 패널이 높아지면 헤더는 고정된 채 사이드바가 스크롤된다; 패널의 작은 ‹ / ›로 접고 편다(액티비티 바 항목은 제거) | ✅ Windows · ⏳ macOS 확인 | `ZeroCommon/Platform/IFolderOpener` — `explorer.exe <folder>` / `/usr/bin/open <folder>` / `xdg-open`, 고정 프로그램에 인자 하나로 넘기고 경로를 ShellExecute하지 않으며, 존재 확인은 `PathAvailability`. 경로 문자열은 `ZeroCommon/Workspace/PathCopyText`(절대 경로는 OS 형식, 상대 경로는 `/`) |
| 코드 뷰: **폴더 목록**(트리에서 폴더 선택 → 그 폴더의 파일을 크기·수정일·생성일과 함께; 읽기 전용, 하위 폴더는 트리에만), **⧉ path**는 상대/절대 선택 팝업, **↗ open**은 폴더를 탐색기/Finder로 연다 | ✅ Windows · ⏳ macOS 확인 | `WorkspaceFiles.ListDetailed` — 트리와 같은 필터·순서. 패널을 열면 워크스페이스 루트를 나열. `code status`가 `view: folder`를 알려 준다 |
| 코드 뷰: 📄 CODE 옆 **⎇ GIT LOG** 탭 — 워크스페이스의 최근 커밋 100개를 최신순 단순 목록으로(제목; 작성자 · 경과 시간; 짧은 해시; 마우스를 올리면 전체 메시지) | ✅ Avalonia 전용 | `ZeroCommon/Workspace/GitLogReader`: 단위/레코드 구분자를 쓴 로컬 `git log`, 셸 없이, 프롬프트 끔, stdin 닫음, 10초 제한. git이 읽지 못하는 폴더면 탭이 비활성화되고 툴팁에 git의 이유가 나온다. 탭은 코드 패널의 다른 면이라 함께 떠 있고 함께 닫힌다(별도 창에도 같은 CODE \| GIT LOG 쌍). `-cli code tab files\|gitlog`; `code status`에 `gitLog` |
| 코드 뷰: GIT LOG 옆 **± DIFF** 탭(WPF 호스트의 Diff Review): **Working tree** — 저장소의 HEAD 대비 변경, untracked 파일 포함; **AI changes** — 봇의 AI 모드가 이번 실행에서 쓴 것, 어떤 폴더든, 파일마다 **↶ Revert / ✓ Keep**과 Revert all. 줄의 💬로 메모를 달고, 메모는 **➤ 활성 터미널**(한 줄로, 그 변경을 만든 에이전트에게) 또는 **🤖 봇**(AI 모드)으로 보낸다; `-cli code tab diff\|ai`, `code status`에 `diff` 포함 | ✅ Avalonia 먼저 · ⏳ WPF는 기존 오버레이 유지 | 웹뷰가 아니라 네이티브로 그린다(macOS 오버레이 규칙, 그리고 테스트 가능한 행). ZeroCommon: `Workspace/GitDiffRunner`(WPF `GitDiffService`를 내려 GIT LOG 방식으로 실행: 셸 없음, 프롬프트 끔, 15초; 그리고 `ls-files --others`로 새 파일 표시), `Module/TextDiff`(같은 `GitDiffReader` 모델로 내는 Myers 줄 diff, 상한 있음), `Workspace/AiChangeJournal`(AI의 첫 `write_file` / `edit_file` 전 각 파일의 원본을 이번 실행 동안 메모리에 보관; 되돌리기는 AI가 만든 파일을 삭제), `Module/DiffReviewPrompt`(WPF 프롬프트 + 해당 줄 인용). 메모는 WPF처럼 공유 `DiffComments` 테이블에 저장 |
| 사이드바: 활성 워크스페이스 행(그리고 활성 워크트리의 하위 행)을 **틸**로 표시 — 강조선·옅은 배경·이름 — 포커스된 터미널의 파랑과 구분; 중첩 워크트리가 활성인 상위 행은 옅은 틸 강조선; `SESSIONS · <워크스페이스>` 헤더 | ✅ Avalonia 전용 | `WorkspaceViewModel.HoldsActiveWorktree`, 전환 시와 워크트리 재검색 후 갱신 |
| 플러그인 브리지 **`sessions.list` / `sessions.focus`** + 공식 플러그인 **Agent Stage**(`Project/Plugins/agent-stage`): 워크스페이스는 존, 터미널은 움직이는 에이전트 아바타(일하는 중 / 확인 필요 / 완료), Claude 탭의 서브에이전트는 아바타를 둘러싼 크루로 표시, 클릭하면 그 터미널로 이동 | ✅ Avalonia 먼저 · ⏳ WPF | `ZeroCommon/Agents/WorkspaceSessionsFeed`: 호스트가 ACTIVE SESSIONS 갱신마다 그 상태를 게시하고 "터미널로 이동"을 등록; 연산은 하나뿐인 디스패처에 있으므로 WPF는 SESSIONS 모니터가 게시하기 전까지 `supported: false`로 답한다(`AgentStateMonitor` → `WorkspaceSessionsFeed.Publish`). 서브에이전트: `ClaudeSubagentReader`가 Claude 세션의 `subagents/agent-*.jsonl`을 이어 읽기로 접어 읽고, 세션은 상태 파일(`AgentLiveStatus.Session`)에서 온다 |
| CLI: `plugin list` / `open` / `call` / `capture` | ✅ Avalonia 전용 | `call`은 `window.zero.invoke`를 페이지 **안에서** 실행하므로, 응답이 페이지 → shim → 브리지 → 디스패처를 거쳐 돌아온다 — 셸에서 하는 플러그인 종단 테스트 |
| Browser 페이지(탭 웹뷰), OS 제어, Vision, Music, Remote, 노트/문서 뷰어, Scrap | ⏳ 미착수 | 2차; 그때까지 웹 도구는 headless |
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
