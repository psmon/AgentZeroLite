---
id: M0041
title: Avalonia v2 ⑨ — AgentBot UX 파리티 (승인 토스트·플로팅 창·키 체인, 음성 제외)
operator: psmon
language: ko
status: done
started: 2026-09-19T00:00:00+09:00
priority: high
created: 2026-09-19
related: [M0037]
---

# 요청 (Brief)

M0037이 AgentBot의 코어(모드 순환·액터 토폴로지·툴벨트·진행 카드·`bot-chat`/`bot-ask`)를 이식했다. 이번엔 **표면**을 채운다:
WPF `AgentBotWindow`(xaml 456줄 / xaml.cs 2728줄)에 있고 Avalonia(`AgentBotView` 124줄 / VM 416줄)에 없는 UX —
승인 토스트 + 자동승인, URL 버블, 세션 헤더 + `AgentEventStream` 부착, 클립보드 첨부, 대용량 청크 전송,
`Ctrl+A~Z` 제어문자, Shift+Tab 모드 순환, Esc로 AI 취소, 미니 키패드, 입력창 리사이즈, Shift+Enter 줄바꿈,
선택 가능 텍스트, 모드 토스트, 그리고 **플로팅 창 ↔ 임베드 토글**. 음성(`.Voice.cs` 1298줄)은 범위 밖.

UI 없는 로직은 먼저 ZeroCommon으로 내린다(`BotOptions`, `ApprovalAutoResponder`, `UrlNoticeThrottle`,
`TerminalTextSender`, `KeyChordTranslator`) — 헤드리스 테스트 대상이고 WPF가 나중에 채택할 수 있다.
도크는 WPF와 같은 **하단 + 스플리터**(활동바를 제외한 전 영역 스팬, 280px, 최대화 시 터미널 90px 유지).
오버레이가 아니라 형제 행이어야 한다 — macOS에서 터미널 네이티브 뷰 위의 Avalonia 콘텐츠는 보이지 않는다.
임베드/분리는 **한 VM + 두 View**(재부모화 금지).

## Acceptance
- [ ] `ZeroCommon.Tests`: 지연 클램프(음수/31 → 0/30), 자동승인 중 토글 OFF 시 미전송, URL 30초 쿨다운 경계 + 100개 evict, 200자 경계 ±1 청크 분기, 개행 시 Enter 추가, `Ctrl+A`→0x01 · `Ctrl+Z`→0x1A
- [ ] 터미널 승인 프롬프트에서 토스트가 뜨고 옵션 클릭이 터미널에 도달; 자동승인 ON이면 지연 후 자동 선택
- [ ] 워크스페이스/탭 전환 시 세션 헤더가 갱신되고 `AgentEventStream`이 재부착됨(`ActiveTerminalChanged` 경유)
- [ ] CHT 모드가 200자 초과 텍스트를 `WriteAsync`로 보내고 개행 포함 시 Enter를 한 번 더 보냄; `UserInput`을 액터에 병행 전송
- [ ] KEY 모드에서 `Ctrl+A~Z`가 제어문자로, Esc가 Escape→300ms→Interrupt 시퀀스로 나감
- [ ] 봇이 **하단**에 도크되고 스플리터로 높이 조절·최대화(터미널 90px 유지)가 되며, 높이가 기억됨
- [ ] `Ctrl+Shift+\`` (macOS `Cmd+Shift+\``) 로 봇이 플로팅 창 ↔ 임베드 전환되고, 상태가 `AppWindowState.IsBotDocked`에 저장·복원됨
- [ ] 메인 창을 닫으면 프로세스가 종료됨 (`ShutdownMode.OnMainWindowClose`)
- [ ] `-cli bot-embed float|dock|toggle` 동작; `AgentZeroAvalonia.Tests`에 `bot-ask` 3갈래(미배선/빈 텍스트/정상) 테스트 추가
- [ ] `git diff --stat main -- Project/AgentZeroWpf` 비어 있음

## Notes
- **WPF 프로젝트를 수정하지 않는다.** 공유 수정은 ZeroCommon으로.
- `ApprovalParser`와 `AgentEventStream`은 **이미 ZeroCommon에 있고 WPF-free** — 새로 만들지 말고 그대로 쓴다.
- `AppWindowState.IsBotDocked`(`ZeroCommon/Data/Entities/WindowState.cs:25`)는 이미 공유 DB 컬럼인데 Avalonia가 읽지 않고 있다. 새 설정을 만들지 말 것.
- `SetBotUiCallback`에서 Avalonia가 `Chat`/`Error`를 렌더하는 것은 **의도적으로 유지** — WPF는 `System`만 보고 버린다(그 버그를 따라가지 않는다).
- `Markdown.Avalonia`는 DESIGN.md:88에서 미채택. 마크다운 렌더는 범위 밖.
- SkillSync · 슬래시 자동완성 · MD 첨부 · AgentZeroCLI Helper는 하나의 의존 사슬(슬래시는 `_syncedSkills`에 종속)이라 **M0042로 함께** 미룬다.
- 계획서: `C:\Users\psmon\.claude\plans\avalonia-crystalline-star.md`
