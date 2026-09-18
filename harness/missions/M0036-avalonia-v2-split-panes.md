---
id: M0036
title: Avalonia v2 ④ — 분할창·탭·워크스페이스 (SplitTree·단축키·DockPaneLayout 호환 저장)
operator: psmon
language: ko
status: inbox
priority: high
created: 2026-09-18
related: [M0035]
---

# 요청 (Brief)

분할창이 핵심 요구다. Dock.Avalonia 대신 직접 구현한 `SplitTree`(재귀 Grid + GridSplitter)와 `TerminalSurfaceHost`
(모든 터미널 컨트롤을 한 Canvas에 두고 절대 좌표로 배치 — 네이티브 WebView 재부모화 없음)로 탭·분할·워크스페이스를
만든다. 영속은 `ZeroCommon/Services/DockPaneLayout.cs` JSON(WPF 호환). 계획 Phase 4.

## Acceptance
- [ ] 오른쪽/아래 분할, 페인 닫기(탭은 이웃으로 병합), 탭 이동, 페인 포커스 이동, 탭 이름 변경·재시작
- [ ] 워크스페이스 복수(사이드바), 새 탭 메뉴가 CLI 정의를 반영
- [ ] 재시작 후 레이아웃 복원; **Avalonia가 저장한 `CliGroup.LayoutJson`을 WPF가 동일하게 연다**(실제 WPF 저장 행 골든 테스트)
- [ ] 터미널이 포커스를 가진 상태에서 모든 단축키 동작(`hotkey` 메시지 경로), macOS는 Cmd 코드
- [ ] 비활성 탭의 PTY가 계속 실행됨; 분할·이동 시 스크롤백 유지
- [ ] `AgentZeroAvalonia.Tests`: SplitTree 모델/매퍼, 사각형 매핑
