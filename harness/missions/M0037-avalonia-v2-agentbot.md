---
id: M0037
title: Avalonia v2 ⑤ — AgentBot 채팅 + 에이전트 루프 (모드 순환·진행 카드·툴벨트)
operator: psmon
language: ko
status: inbox
priority: high
created: 2026-09-18
related: [M0036]
---

# 요청 (Brief)

액터 토폴로지(`StageActor`/`AgentBotActor`/`AgentLoopActor`)를 그대로 쓰는 AgentBot 패널. `AgentBotViewModel`
(CHT/KEY/AI 모드, `ChatModeCycle`), `AgentLoopWiring`(WPF `AgentBotWindow` 1211–1300 이식), `WorkspaceToolHost : IAgentToolbelt`
(터미널 4동사 + 첫 접촉 소개, `FileToolCore`, `FileOpenPolicy`, `HeadlessWebToolSurface`). 계획 Phase 5.

## Acceptance
- [ ] CHT 모드가 활성 터미널에 `WriteAndEnter`, KEY 모드가 `SendControl`
- [ ] AI 모드(External, Webnori/OpenAI 호환)로 `list_terminals → send_to_terminal → read_terminal` 왕복, `read_file/grep`, `find_files/open_file`, `web_search`(headless) E2E
- [ ] 진행 카드(Thinking/Generating/Acting 툴 카드/Done·Error)
- [ ] `-cli bot-chat "DONE(…)" --from X`가 채팅에 도착하고 `TerminalSentToBot`으로 라우팅
- [ ] Windows 로컬 LLM(LLamaSharp, 기존 `runtimes/win-x64-*`) 경로 동작; 비Windows는 External만(안내)
- [ ] `AgentZeroAvalonia.Tests`: 모드 순환, 진행→카드 매핑
