---
date: 2026-08-31T00:53:35+09:00
agent: tamer
type: review
mode: log-eval
trigger: "pdsa 활용 워크트리로 분리 후 모던 터미널 조사·교체 시도 (worktree feat/modern-terminal-webview)"
---

# Modern terminal spike — xterm.js-in-WebView2 behind the ITerminalSession seam

## 실행 요약

운영자 요청: PDSA를 활용해 워크트리로 분리한 뒤, 현재 도입된 AgentZero 터미널보다 더 모던하고
윈도우에서 터미널 창을 더 잘 제어하며 호환성 높은 터미널을 조사·교체 시도하되 기존 기능은 전부
호환되어야 함.

조사 결과 전제를 교정: AgentZero는 **이미 최신 Windows Terminal 백엔드**(EasyWindowsTerminalControl
→ Microsoft.Terminal.Control.dll + conpty.dll, HwndHost) 위에 있었다. 반복되는 통증(승인 오버레이가
터미널 위에 못 뜸 / ConPTY 입력 파이프 wedge / 하드코딩 네이티브 DLL 버전 불일치)은 "구식"이 아니라
**HwndHost + ConPTY 구조 자체**에서 온다. 운영자 확정 방향에 따라 xterm.js-in-WebView2 백엔드를
`ITerminalSession` 두 번째 구현으로, `terminal-settings.json` 플래그(기본 EasyConPty) 뒤에 PoC로 구현.

## 결과

- 신규: `ManagedConPtyHost`(P/Invoke ConPTY), `XtermTerminalControl`(WebView2 + 오프라인 xterm.js),
  `WebViewXtermTerminalSession`, `TerminalSettings`/`TerminalControlSequences`(공유 VT 테이블).
- 커플링 인터페이스화 + `MainWindow.InitializeWebViewTerminal` 플래그 분기(액터 바인드 경로 공유).
- 빌드 Debug/Release 0/0. 테스트 **707 green**(ZeroCommon 561 + AgentTest 146, 신규 라이브 ConPTY
  통합 테스트 2건 포함). 기존 EasyConPty 경로 무변경 → 회귀 0.
- 라이브 ConPTY 호스트 spawn/attach/output/stdin 전부 검증.
- 미완: 운영자 데스크톱 스모크(렌더/IME/승인 토스트 airspace 데모). PDSA 판정 PARTIAL.

## 평가 (3축)

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | A− | 신규 백엔드는 플래그 뒤 opt-in, 기본 경로 무변경; 네이티브 핸들 수명 관리(Dispose 순서: stdin→PC→outRead, 프로세스/스레드 핸들 SafeHandle)·CSP로 오프라인 asset 격리. 감점: P/Invoke ConPTY는 라이브 테스트로 검증됐으나 예외/에지(리사이즈 경합, 조기 종료) 커버리지는 아직 얕음 |
| 아키텍처 정합성 | A | `ITerminalSession` 심(seam) 위 증축 — 모든 소비자(CLI IPC/handshake/approval/health)가 인터페이스로 통일; WebView2 호스팅은 기존 `WebDevBridge`의 virtual-host + JS 브리지 패턴 재사용; VT 테이블 단일화로 3-map drift 위험 축소; ZeroCommon Win32-free 규칙 준수(호스트는 WPF측) |
| 테스트 가능성 | A− | 계약/파서/오케스트레이션 707 green + 신규 라이브 ConPTY 통합 테스트로 "fake만 테스트" 공백을 일부 메움. 감점: WebView↔세션 브리지 end-to-end와 IME·airspace는 헤드리스로 관찰 불가라 운영자 스모크 의존 |

## 다음 단계 제안

- 운영자 스모크: `terminal-settings.json`에 `{"Backend":"WebViewXterm"}` 후 탭 열기 → ANSI 출력/리사이즈
  → 한글 IME → 승인 요청 발생 시 토스트가 터미널 **위**에 뜨는지(airspace 승리) → DONE/handshake → health.
- 통과 시 넓은 opt-in으로 승격; IME/스크레이프 갭이 있으면 XtermTerminalControl 브리지와
  `GetConsoleText`(SerializeAddon 가시 화면 스냅샷)부터 개선.
- 채택 확정 전 WebView용 wedge-recovery(Restart)·floating/redock 동등 기능 추가.
