---
id: M0035
title: Avalonia v2 ③ — xterm.js WebView 터미널 (자산 서빙·PTY 호스트 2종·브리지·헬스)
operator: psmon
language: ko
status: done
started: 2026-09-19T00:20:00+09:00
finished: 2026-09-19T01:00:00+09:00
priority: high
created: 2026-09-18
related: [M0034]
---

# 요청 (Brief)

`NativeWebView` 안의 xterm.js로 터미널 탭 하나를 띄운다. PTY는 Windows `ConPtyHost`(WPF `ManagedConPtyHost` 사본),
macOS `PortaPtyHost`(Porta.Pty 2.2.2). 세션은 ZeroCommon `XtermTerminalSession`. 자산은 `LocalAssetServer`(루프백,
토큰 경로) 1순위, `WebResourceRequested`/`file://`는 반나절 스파이크 후 최적화. 계획 Phase 3.

## Acceptance
- [ ] Windows `cmd`/`pwsh`/`Claude`, macOS `/bin/zsh` 탭 렌더링(색상·커서·한글 IME)
- [ ] 리사이즈 추종, `ready` 전 출력 버퍼링, 5 MB `cat`·`yes | head -200000`에서 입력 정지 없음(처리량 측정 기록)
- [ ] `-cli terminal-read`가 화면 텍스트(스냅샷)를 돌려주고 `terminal-send "dir"`이 에코됨
- [ ] Claude Code TUI 사용 가능; 막힌 탭에 헬스 배너와 "다시 시작"
- [ ] 링크 클릭 → 기본 브라우저(http/https만)
- [ ] 액터 바인딩(`CreateTerminalInWorkspace`·`BindSessionInWorkspace`·`SetActiveTerminal`)
- [ ] ConPTY 사본 vs Porta 대비 체크리스트(환경·종료·리사이즈·UTF-8·붙여넣기) 결과를 `Docs/avalonia-v2/DESIGN.md`에 기록

## Notes
- WPF `term.js`는 수정하지 않는다. 사본에 전송 shim(`invokeCSharpAction` 우선)·`out64`·`hotkey`를 더한다.
