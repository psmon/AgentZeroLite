---
id: M0008
title: 음성노트 WebDev 플러그인 본체 (Project/Plugins/voice-note/)
operator: psmon
language: ko
status: inbox
priority: medium
created: 2026-05-03
related: [M0006, M0007]
---

# 요청 (Brief)

M0007 에서 깔린 plugin runtime 인프라 (git URL 설치 채널 + native API:
VAD on/off, STT stream, sensitivity, summarize) 위에 **음성노트 플러그인**을
실제 빌드한다.

## 위치

- 프로젝트 경로: `Project/Plugins/voice-note/`
  - `manifest.json` (id: voice-note, name: Voice Note, entry: index.html)
  - `index.html`
  - `voice-note.js`
  - `voice-note.css`
- 분리된 외부 프로젝트 — `AgentZeroWpf.csproj` 빌드에는 포함되지 않는다
  (M0007 에서 `Project/Plugins/` 빌드 제외 보장).

## 기능

- **노트 목록 + new** — 새 음성노트 생성 / 기존 노트 선택 / 삭제
- **STT 스트리밍 캡처** — `window.zero.stt.start()` 로 시작, VAD 이용해
  발화 구간만 텍스트로 추가됨 (전체 녹음 X)
- **일시정지 / 재개** — 캡처 중 토글
- **민감도 슬라이더** — VAD threshold 실시간 조절 → 인식률 체크
- **요약** — `window.zero.summarize()` 호출. Max-token 초과 시 1/2 분할
  청크 후 재요약 (M0007 인프라가 처리)
- **3-계층 저장**:
  - raw timeline 텍스트 (시간대별)
  - summary 텍스트
  - meta (모델 / 토큰 소요 / 시작·종료 시각)
- 저장 매체: 1차 IndexedDB (단일 origin local), 2차 export to JSON

## Acceptance
- [ ] `Project/Plugins/voice-note/` 4-파일 (manifest + html + js + css) 신설
- [ ] zip 배포 가능 (Project/Plugins/dist/ 또는 README 명시 절차)
- [ ] git URL 설치 가능 — M0007 의 git installer 가 첫 reference user 로 사용
- [ ] 노트 목록 / new / 삭제 / STT 스트림 / 일시정지 / 민감도 / 요약 / 3-계층 저장 모두 동작
- [ ] `Project/Plugins/README.md` 카탈로그에 voice-note 항목 추가 + 설치 가이드 (양 방식)

## Notes
- M0007 가 done 이 되어야 본 미션이 dispatch 가능 (의존).
- UI 톤: 다크 (WebDev 메인 톤과 일치, `#0A0A14` 캔버스).
- 요약 prompt 는 system 측에서 standard wrapping ("다음 텍스트를 핵심
  요점으로 요약…") — 추후 사용자 커스터마이즈 메뉴 분리 가능.
