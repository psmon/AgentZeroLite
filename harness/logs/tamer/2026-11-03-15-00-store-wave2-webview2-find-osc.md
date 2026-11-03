---
date: 2026-11-03T15:00:00+09:00
agent: tamer
type: improvement
mode: execution
command: "/harness-creator 제안 1+2+3 채택 — 워크트리 store-wave2 에서 순차 구현"
---

# Store판 2차 개선: WebView2 런타임 확인/설치, 터미널 검색, OSC 알림

채택안은 `2026-11-03-14-09-store-edition-next-ideas.md`의 1·2·3이다.
워크트리 `.claude/worktrees/store-wave2`(브랜치 `worktree-store-wave2`)에서 하나로 묶어 순차 구현했다.

## 실행 요약

### 1. WebView2 런타임 확인 + 설치 지원 (Windows, Store판 + 수동 설치판)
- ZeroCommon `Platform/WebView2Runtime`
  - 감지: Microsoft가 문서화한 `pv` 확인(HKLM WOW6432Node / HKLM / HKCU, 값이 `0.0.0.0`이거나 비어 있으면 없음).
  - 설치: fwlink에서 Evergreen 부트스트래퍼를 받고, WinVerifyTrust와 Microsoft 서명자를 확인한 뒤 `/silent /install`로 실행한다. 패키지 안에서는 `DesktopAppBreakaway`로 실행한다.
  - 테스트 스위치: `AGENTZERO_FAKE_NO_WEBVIEW2=1`.
- Avalonia 터미널 배너에 **Install WebView2 runtime** / **Download page** 버튼을 넣었다. 설치는 프로세스당 한 번만 진행하고, 끝나면 재시작을 안내한다.
- `-cli webview2 status|install`(GUI 불필요, JSON 출력, 설치돼 있으면 exit 0).
- `installer/AgentZeroLite.iss`: 설치 마법사의 wpReady 단계에서 런타임이 없으면 부트스트래퍼를 받고 [Run]에서 앱보다 먼저 설치한다. 다운로드에 실패해도 치명적이지 않다(앱 배너가 대신 안내).

### 2. 터미널 내 검색
- 동봉한 xterm이 **5.5.0**임을 확인했다(jsdelivr 크기 비교, 1바이트 차이). 짝 릴리스인 `@xterm/addon-search` 0.15.0을 Avalonia 전용 `Wasm/xterm/addons/`에 MIT 라이선스와 함께 넣었다. WPF 폴더는 건드리지 않았다.
- 검색창은 페이지(index.html)가 그린다. 일치 개수, 이전/다음, 대소문자, 정규식, Esc, 선택 글자로 시작을 지원한다.
- `HotkeyTable.TerminalFind` = Ctrl+Shift+F(macOS는 Cmd)이고 팔레트와 Shortcuts 페이지에 자동으로 노출된다. 렌더러 안에서 눌리면 그 렌더러의 검색창이 열린다.
- `Terminal/WebViewKeyboardFocus`: `ICoreWebView2Controller::MoveFocus`(vtable 12)를 호출한다.

### 3. OSC 9 / 777 / 99 알림
- ZeroCommon `Agents/TerminalNotificationParser`: ConEmu `9;N` 명령(9;4 진행률) 제외, kitty `p=`/`e=1` 처리, 제어 문자 제거, 240자 제한.
- `AttentionKind.Notify`, `AttentionAlert.Message`, `AttentionSettings.OnNotify`(기본 켬; 필드가 없는 이전 파일도 켬으로 읽힌다), `AttentionNotifier.ObserveNotification`(즉시 알림, 같은 문구는 10초 안에 한 번).
- term.js `registerOscHandler(9/777/99)` → 컨트롤 → TerminalsView → VM `OnTerminalNotification` → 기존 팝업과 깜빡임. 팝업 행은 노란색이고 프로그램이 보낸 문구를 보여 준다. 설정 체크박스와 `attention status`의 `onNotify`도 추가했다.

### 운영자 지침 반영 (지식 업데이트)
- `harness/knowledge/test-runner/gui-e2e-display.md`와 `Docs/scripts/place-on-secondary-monitor.ps1`: Debug GUI e2e는 보조 모니터에서 최대화로 진행한다. test-runner 정의와 knowledge README에 연결했다.

## 결과

- 테스트: ZeroCommon 1380 통과 / 36 건너뜀 / 0 실패(신규 WebView2RuntimeTests 10, TerminalNotificationTests 15), Avalonia 129 통과(신규 hotkey 1).
- e2e (Debug GUI, 보조 모니터 `\\.\DISPLAY2` 최대화):
  - `-cli webview2 install`(강제 누락): 다운로드 → `Installer signature ok (Microsoft Corporation)` → 부트스트래퍼 실행 → 재감지 `156.0.4314.58 (machine)`.
  - 강제 누락 GUI: 배너와 두 버튼이 표시됐다.
  - 검색: Ctrl+Shift+F → "alpha" → `2 of 31`(출력 30줄 + 명령 1줄), Esc 후 터미널에 `echo back-in-terminal` 입력이 동작했다.
  - OSC: `osc-emit.ps1`이 9;4 진행률은 무시하고 OSC 9(`hello from osc9`)와 OSC 777(`OpenCode — build done`)을 수신했다. 창이 활성일 때는 규칙대로 조용했고, 최소화한 뒤에는 `[Attention] infra/PW5=Notify`와 함께 팝업 창 `AgentZero — needs you`가 떴다.

### 실측으로 알게 된 것
- `Control.Focus()`만으로는 NativeWebView(WebView2)에 키보드가 가지 않는다. 검색창은 열렸지만 입력이 사라졌고, MoveFocus를 호출하자 해결됐다.
- 기본 내장 ConPTY(Windows 11 26300)는 OSC 9 / 777을 그대로 통과시킨다.
- PrintWindow 캡처는 per-monitor DPI 인식(`SetProcessDpiAwarenessContext(-4)`)이 없으면 배율 모니터에서 잘린다.
- PowerShell 변수는 대소문자를 구분하지 않아 `$h`(hwnd)와 파라미터 `$H`가 충돌했다(캡처 스크립트).

## 평가

| 축 | 등급 | 근거 |
|----|-----|------|
| 워크플로우 개선도 | A | 제안 → 채택 → 단일 워크트리 순차 구현 → 테스트 → 보조 모니터 e2e가 한 세션에서 이어졌고, 운영자 지침이 지식과 스크립트로 바로 고정됐다 |
| Claude 스킬 활용도 | 3 | agentzero-cli 경로(`-cli terminal-send/read`)는 썼지만 os 동사 대신 즉석 PowerShell 캡처를 반복해서 썼다. 캡처 스크립트를 스킬이나 스크립트로 승격할 여지가 있다 |
| 하네스 성숙도 | L4 | test-runner 지식에 e2e 화면 배치 규칙이 생겼다. Store 문서 §5의 낡은 항목도 정리했다 |

## 다음 단계 제안

- **패키지 실측**: 개발자 모드로 등록한 MSIX에서 breakaway 설치 경로를 확인한다(런타임이 없는 VM이나 Windows Sandbox가 이상적).
- **Inno 컴파일**: 로컬에 ISCC가 없어서 `.iss` 변경은 CI(`release.yml` / 수동 실행 `publish=false`)에서 처음 컴파일된다.
- **기존 현상(이번 범위 밖)**: 창을 다시 활성화(AppActivate)한 직후의 키 입력이 WebView 안으로 들어가지 않는다. `FocusTerminal`도 `Web.Focus()`만 쓰므로 `WebViewKeyboardFocus`를 적용할 후보다.
- macOS: 검색과 OSC 알림은 WKWebView에서 사람이 확인해야 한다(`macos-smoke.md`에 항목 추가 후보).
- 캡처 스크립트(per-monitor DPI, 창 핸들 기준)를 `Docs/scripts/`로 승격한다.
