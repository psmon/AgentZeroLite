---
id: M0034
title: Avalonia v2 ② — AgentZeroAvalonia 호스트 골격 (프로젝트·부트스트랩·액터 시스템·메인 셸·CI 빌드 게이트)
operator: psmon
language: ko
status: done
started: 2026-09-19T00:06:00+09:00
finished: 2026-09-19T00:12:00+09:00
priority: high
created: 2026-09-18
related: [M0033]
---

# 요청 (Brief)

`Project/AgentZeroAvalonia`(net10.0, Avalonia 12.1.2, CommunityToolkit.Mvvm, `AssemblyName=AgentZeroLite`)를 만들고
부트스트랩 순서(로거 → `-cli` → 단일 인스턴스 → 비밀 보호기 → DB → 액터 → CLI 서버 → MainWindow)를 세운다.
xterm 자산은 WPF 폴더의 `vendor/**`를 링크하고 `index.html`·`term.js`는 사본. 계획 Phase 2.

## Acceptance
- [ ] `AgentZeroLite.slnx` 등록, Debug/Release/AgentCLI 구성, Windows 빌드 0 오류, `-r osx-arm64` 크로스컴파일 0 오류
- [ ] 앱 기동·두 번째 인스턴스 거부·공유 경로 DB 초기화·액터 시스템 기동(UI 스레드 SyncContext 확인)
- [ ] `AgentZeroLite -cli status`가 NamedPipe로 응답
- [ ] 메인 셸: 액티비티 바(터미널/봇/설정), 워크스페이스 사이드바, 빈 문서 영역, 봇 토글, 다크 테마(`Styles/Theme.axaml`)
- [ ] `.github/workflows/avalonia-build.yml` 잡 A(windows-latest) 녹색; `release.yml` 무변경
- [ ] WPF 무영향(`git diff --stat main -- Project/AgentZeroWpf` 비어 있음)

## Notes
- 806cf94에서 `Program.cs`·`App.axaml(.cs)`·`Styles/Theme.axaml`·`Actors/ActorSystemManager.cs` 구조를 살린다(12 API로 손질).
- Dock.Avalonia·Markdown.Avalonia·네이티브 터미널 컨트롤은 쓰지 않는다.
