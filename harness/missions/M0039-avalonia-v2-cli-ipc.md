---
id: M0039
title: Avalonia v2 ⑦ — `-cli` 표면 (NamedPipe 서버/클라이언트·1차 동사·셸 래퍼)
operator: psmon
language: ko
status: inbox
priority: medium
created: 2026-09-18
related: [M0038]
---

# 요청 (Brief)

`AgentZeroLite -cli …`가 Avalonia 호스트에서 NamedPipe(`ICliIpcBridge`)로 동작하게 한다. 요청/응답 JSON은 WPF와 동일해
`-cli help agentzero` 가이드·프린터를 그대로 쓴다. 계획 Phase 7.

## Acceptance
- [ ] 동사: `help [topic]`, `version`, `status`, `terminal-list/-send/-key/-read/-wait/-alias`, `bot-chat`, `web open|search|read|tabs`, `open-win`, `close-win`
- [ ] 숨은 자가진단 `selftest pty|ipc|secrets`(CI용)
- [ ] GUI 없음 → 명확한 오류(Windows 힌트: WPF 빌드는 WM_COPYDATA)
- [ ] `AgentZeroLite.ps1`(사본)·`AgentZeroLite.sh`로 Windows/macOS 스크립트 왕복 통과
- [ ] `web`은 UI 스레드 밖에서 `req` 에코
- [ ] `AgentZeroAvalonia.Tests`: `CliCommandRouter`(가짜 세션) 요청→응답
