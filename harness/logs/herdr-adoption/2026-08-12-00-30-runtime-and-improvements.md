---
date: 2026-08-12T00:30:00+09:00
agent: code-coach
type: improvement
mode: log-eval
trigger: "계속 중단없이 개선사항 진행"
engine: herdr-adoption
phase: H-runtime
---

# herdr 영입 — 런타임 + 연속 개선

## 실행 요약

H1~H5 순수 코어에 이어 실동작 런타임 + 데이터 튜닝 + 세션 발견을 중단 없이 추가.

## 결과

- **H1/H2 런타임** — `AgentStateMonitor`(1.5s 틱): 터미널 화면 스냅샷 → 상태 분류 →
  탭별 (state, seen) 추적, 변경 시에만 이벤트. `-cli agent-state`(IPC) + 타이틀바 주의 카운트.
- **H2 UI 칩** — SESSIONS 각 행에 상태 색상 칩(blocked=빨강/working=노랑/done미확인=파랑/
  idle=회색) + 라벨. 라이브 검증(Claude → idle 칩).
- **H2 주의 알림** — `TaskbarFlasher`: 새 blocked/done 시 작업표시줄 플래시(비활성 창).
- **H1 데이터 튜닝** — `AgentManifestJson`: `~/AppData/Local/.../agent-detection/<agent>.json`
  오버라이드(빌드 없이). 캐시 + Reload. Claude working 정규식 강화(스피너/토큰 카운터). 6 테스트.
- **H3 세션 발견** — `ClaudeSessionLocator`: cwd→slug→최신 세션 발견 + `-cli agent-resume-cmd`.
  라이브 검증(CodeScan → claude --resume 8151ecda-...). 6 테스트.
- **문서** — README-EX 양 언어판 "11. 에이전트 상태 감지" 섹션 + CLI 요약.

## 검증

- 헤드리스: 전체 **473 통과 / 0 실패**(+12: JSON 6 + ClaudeSession 6, 기존 herdr 31 포함 누적).
- 라이브: agent-state(idle Claude), SESSIONS idle 칩, agent-resume-cmd(실 세션 발견). WPF 빌드 0 오류.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 세션ID/경로 안전. 오버라이드 파싱 실패 시 built-in 폴백. 플래시 best-effort. |
| 아키텍처 정합성 | Pass | 감지/JSON/로케이터 ZeroCommon 순수. 모니터/칩/플래시만 WPF. |
| 테스트 가능성 | A | 감지·JSON·롤업·resume·세션발견 전부 헤드리스. 런타임/칩은 라이브 스크린샷 검증. |

## herdr 영입 현황

| 항목 | 코어 | 런타임 |
|---|---|---|
| H1 상태 감지 | ✅(+JSON 오버라이드) | ✅ 모니터+CLI |
| H2 롤업 | ✅ | ✅ SESSIONS 칩+타이틀바+플래시 |
| H3 세션 복원 | ✅ 발견+커맨드 | ⏳ 자동 재기동 |
| H5 wait --until | ✅ | ✅ |
| H4 권위 모델 | ✅ | ⏳ 다중 CLI 훅 인스톨러 |

## follow-up
- H3 자동 재기동(터미널 relaunch로 --resume), codex/cursor 세션 로케이터.
- H4 codex/cursor hook 파일 인스톨러 + 모니터에 authority 반영.
- H2 워크스페이스 레벨 롤업 점, done 상태(훅 Stop 연동).
- H5 안정적 에이전트 별칭(alias→tab).
