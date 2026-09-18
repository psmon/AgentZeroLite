---
id: M0040
title: Avalonia v2 ⑧ — 패키징/CI (win-x64 zip·osx-arm64 .app·macOS 스모크)
operator: psmon
language: ko
status: done
started: 2026-09-19T03:30:00+09:00
finished: 2026-09-19T03:55:00+09:00
priority: medium
created: 2026-09-18
related: [M0039]
---

# 요청 (Brief)

Avalonia 호스트를 배포 가능한 형태로 만든다. win-x64 self-contained zip, osx-arm64 `.app` 번들(`macos/build-app.sh`,
`Info.plist`, ad-hoc codesign), `.github/workflows/avalonia-build.yml`(windows-latest + macos-14 매트릭스). `release.yml`은
손대지 않는다. 계획 Phase 8.

## Acceptance
- [ ] CI 잡 A: 테스트 + win-x64 publish 아티팩트; 잡 B: macOS 빌드·publish·`.app`·`-cli version`·`selftest pty|ipc|secrets`·ZeroCommon.Tests on macOS
- [ ] `Docs/avalonia-v2/macos-smoke.md` 체크리스트 작성, 실행 결과 기록(운영자 Mac 또는 대리)
- [ ] 서명·공증 절차를 `harness/knowledge/_shared/code-signing.md`에 기록(실행은 후속)
- [ ] (stretch) macOS 로컬 LLM: LLamaSharp Metal 백엔드 호환성 조사 결과 기록

## Notes
- 운영자의 Mac 보유 여부 미확인 — 없으면 CI 자가진단까지가 이 미션의 완료 조건.
