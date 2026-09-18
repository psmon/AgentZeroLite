---
id: M0033
title: Avalonia v2 ① — ZeroCommon 플랫폼 seam (AppPaths·단일인스턴스·NamedPipe CLI IPC·IPtyHost/XtermTerminalSession·POSIX 환경·OS별 CLI 시드)
operator: psmon
language: ko
status: done
finished: 2026-09-19T00:05:00+09:00
started: 2026-09-18T22:30:00+09:00
priority: high
created: 2026-09-18
related: [M0032]
---

# 요청 (Brief)

Avalonia 크로스플랫폼 호스트(`Project/AgentZeroAvalonia`)가 기대는 공통 계층을 ZeroCommon에 **추가만**으로
마련한다. 계획 문서 `Docs/avalonia-v2/DESIGN.md` Phase 1 표(1.1~1.11)가 목록이다. 이 미션은 main에 먼저
합류할 수 있어야 하므로 **WPF 앱의 Windows 동작은 바이트 단위로 동일**해야 한다.

## Acceptance
- [ ] `Platform/AppPaths`, `ISingleInstanceGuard`(Windows 뮤텍스 `Local\AgentZeroLite.SingleInstance` / Unix 잠금 파일), `ICliIpcBridge`(NamedPipe, CurrentUserOnly, 1 MiB 캡, 비동기 핸들러)
- [ ] `IConsoleTabInfo`에 `Session`·`IsTerminalStarted` 추가(WPF 무편집 컴파일), `Module/TerminalCatalogJson`이 WPF `BuildTerminalListJson`과 같은 JSON을 낸다(골든 테스트)
- [ ] `TerminalEnvironment.BuildPosix()`·`PrependPath()`, `TerminalLaunchPlanner`/`CommandLineSplitter`(`.exe` 정의는 비Windows에서 숨김)
- [ ] `AppDbContext.EnsureDefaultCliDefinitions`가 비Windows에서 zsh/bash/Claude를 런타임 시드(마이그레이션 없음), OS 판정 주입 가능
- [ ] `LlmService._putenv_s`·`VulkanDeviceEnumerator`·`LlamaSharpLocalLlm`·`LlmGateway`가 비Windows에서 안전하게 실패/비활성
- [ ] `IPtyHost` + `XtermTerminalSession`(WPF `WebViewXtermTerminalSession` 사본) + `TerminalHealthTracker`, `FakePtyHost` 테스트
- [ ] `AesGcmFileSecretProtector`, `Agents/ChatModeCycle`
- [ ] `ZeroCommon.Tests` 신규 테스트 통과, WPF Debug 빌드·`AgentTest` 통과, `git diff --stat main -- Project/AgentZeroWpf` 비어 있음
- [ ] `dotnet build Project/ZeroCommon -r osx-arm64` 성공

## Notes
- Porta.Pty는 ZeroCommon에 넣지 않는다(네이티브 동봉). ZeroCommon은 `IPtyHost` 계약만 소유.
- 기존 20개 설정 스토어의 경로는 손대지 않는다(1차). `AppPaths`는 새 호스트만 사용.
