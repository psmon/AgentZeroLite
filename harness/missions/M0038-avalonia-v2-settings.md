---
id: M0038
title: Avalonia v2 ⑥ — 설정 (External LLM·CLI 정의 CRUD·터미널 외관·Windows 로컬 LLM)
operator: psmon
language: ko
status: done
started: 2026-09-19T02:30:00+09:00
finished: 2026-09-19T02:55:00+09:00
priority: medium
created: 2026-09-18
related: [M0037]
---

# 요청 (Brief)

좌측 내비(LLM / CLI 정의 / 터미널)의 설정 페이지. 기존 스토어(`LlmSettingsStore`, `TerminalSettingsStore`, `AppDbContext`)와
`SecretProtection`(Windows DPAPI 사본 / macOS AES-GCM)을 재사용. 계획 Phase 6.

## Acceptance
- [ ] External LLM: 공급자·모델·키(마스킹)·MaxTokens·Temperature 저장/복원, "테스트" 버튼 왕복, 키가 보호되어 저장됨(양 OS)
- [ ] CLI 정의 CRUD(`IsBuiltIn` 삭제 불가, 파일 선택기, 정렬), 비Windows에서 `.exe` 정의 숨김·SSH 필드 읽기 전용, 새 탭 메뉴 즉시 반영
- [ ] 터미널 외관(폰트·크기·행간·커서·WebGL·테마)이 열린 터미널에 라이브 반영
- [ ] Windows: 로컬 모델 카탈로그·다운로드·Cpu/Vulkan·로드/언로드; 비Windows: Local 라디오 비활성+툴팁
