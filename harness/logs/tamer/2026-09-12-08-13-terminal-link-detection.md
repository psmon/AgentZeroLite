---
date: 2026-09-12T08:13:00+09:00
agent: tamer
type: creation
mode: execution
trigger: "터미널 문자가 웹링크(https 등, OAuth 인증 링크)일 때 감지해서 내 로컬 브라우저로 방문할 수 있게"
---

# Terminal hyperlink detection → "Open in Browser" strip (both terminal backends)

## 실행 요약

운영자 요청: 터미널 출력에 OAuth 인증 링크 같은 URL이 나오면 감지해서 로컬 브라우저로 열 수 있게.

조사 결과:
- URL 감지는 이미 `AgentEventStream.UrlDetected`에 있었으나 **봇 창(AgentBotWindow) 말풍선에만** 노출되고,
  봇이 타깃한 세션 하나만 감시하며, 줄바꿈으로 잘린 긴 OAuth URL은 첫 줄만 잡혔다.
- 기본 백엔드 EasyConPty의 `Microsoft.Terminal.Wpf.TerminalControl`은 리플렉션/네이티브 export 확인 결과
  **하이퍼링크 API가 전혀 없다** (OSC 8·Ctrl+클릭 불가, `Columns`/`Rows`만 노출). 따라서 클릭 가능한 텍스트가 아니라
  **호스트 측 감지 + 터미널 위쪽 스트립** 방식이 유일한 공통 해법. HwndHost airspace 때문에 오버레이가 아닌
  `TerminalHost` row 0(터미널 셀 바깥)에 배치.
- WebView xterm.js 백엔드는 `@xterm/addon-web-links@0.11.0`(UMD, 3 KB)을 오프라인 vendor로 추가해 Ctrl+클릭 지원.

## 결과

신규 / 변경 파일:
- `Project/ZeroCommon/Services/TerminalLinkDetector.cs` — `TerminalLinkScanner`(순수 함수, soft-wrap 이어붙이기,
  http/https 스킴 검증) + `TerminalLinkDetector`(세션 스트리밍, 600 ms idle flush, 60 s 중복 억제).
- `Project/ZeroCommon/Services/AgentEventStream.cs` — `ScanForUrls`가 스캐너를 재사용 → 봇 말풍선도 잘린 URL 대신 전체 URL.
  버퍼 초기화 시 `_urlScanOffset`도 리셋(기존 잠재 버그).
- `Project/AgentZeroWpf/UI/APP/MainWindow.LinkStrip.cs` — 탭별 detector 배선(`BindSessionToActors`에서 세션당 1회),
  링크 스트립(Open in Browser / Copy / ✕, 3분 자동 숨김, 버튼 `Focusable=false`로 터미널 포커스 보존).
- `Project/AgentZeroWpf/Services/ExternalLinkOpener.cs` — ShellExecute 단일 관문, http/https 외 거부.
- `Project/AgentZeroWpf/Module/CliConsoleModels.cs` — `StripHost`(row 0 StackPanel), `LinkDetector`, `LinkStrip*` 필드.
- `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs` — row 0를 StackPanel로 감싸 REDOCK/링크 스트립 공존, 재시작·탭 닫기 경로에서 detector dispose.
- `Project/AgentZeroWpf/UI/Components/XtermTerminalControl.xaml.cs` — `link` 메시지 처리, `Columns` 노출.
- `Project/AgentZeroWpf/Wasm/xterm/{index.html,term.js,vendor/addon-web-links.js}` — Ctrl+클릭 → host `link` 메시지, OSC 8 linkHandler.
- `Project/ZeroCommon.Tests/TerminalLinkDetectorTests.cs` — 18 케이스(3행 wrap 이어붙이기, 짧은 줄/새 URL/비-URL 다음 줄 비결합,
  스트리밍 MayContinue, ANSI 제거, 중복 억제, Flush, 스킴 거부, EventStream wrap).

검증:
- `dotnet build AgentZeroWpf -c Debug` — 오류 0 / 경고 0.
- `ZeroCommon.Tests` 전체 — 585 통과 / 24 skip / 0 실패 (신규 18 포함).
- `AgentTest` (AgentEventStream·ApprovalParser·TerminalSession) — 108 통과 / 0 실패 → 기존 승인 파서 회귀 없음.
- GUI 스모크는 미수행 (GUI 미기동). 운영자 확인 항목: claude `/login` 화면에서 스트립 표시 → Open in Browser.

## 평가

| 축 | 등급 | 근거 |
|----|------|------|
| 코드 안전성 | A | 터미널 출력은 신뢰 불가 입력 — `IsOpenableWebUrl`로 http/https만 셸에 전달, file:/javascript:/커스텀 스킴 테스트로 차단. detector는 세션 교체·탭 닫기 3경로 모두에서 dispose. |
| 아키텍처 정합성 | A | 감지 로직은 ZeroCommon(WPF-free)에, 열기·UI는 WPF에. `ITerminalSession` seam 위에 있어 두 백엔드 동일 동작. `AgentEventStream`이 같은 스캐너를 공유해 중복 정규식 제거. |
| 테스트 가능성 | B+ | 스캐너/디텍터는 헤드리스 18 케이스. 스트립 UI와 xterm Ctrl+클릭은 자동 테스트 없음(GUI 스모크 의존). ConPTY가 실제로 wrap 행에 `\n`을 넣는지는 실로그가 없어 휴리스틱(열 폭 미상 시 60자)으로 방어. |

## 다음 단계 제안

- 운영자 스모크: claude `/login` 또는 `gh auth login` 으로 스트립·Open 동작 확인. EasyConPty에서 wrap된 URL이
  잘려 보이면 실제 ConPTY 로그(`LogConPTYOutput`)를 첨부 — 휴리스틱 폭 조정 근거로 사용.
- EasyConPty의 `TermPTY`가 열 수를 노출하면 `ResolveTerminalColumns`에 연결(현재 xterm 백엔드만 실제 폭 전달).
- 설정 옵션(자동으로 브라우저 열기 on/off)은 요청 시 `TerminalSettings`에 추가 — 현재는 클릭 기반(안전 기본값).
