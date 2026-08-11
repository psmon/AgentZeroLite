---
date: 2026-08-11T17:50:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "다음 영입 — list_files 도구 + 예약 자동화"
engine: orca-adoption
phase: EX1
---

# list_files 파일 도구 + 예약 자동화(Automations) 완료

## 실행 요약

3차 배치 1탄. 사용자 테스트에서 드러난 "모델이 파일명 추측" 문제를 `list_files`로 해결하고,
프롬프트를 스케줄로 자동 실행하는 Automations를 추가.

## 결과 — list_files

- `FileToolCore.ListFiles`(순수) + `EnumerateDirs` — 워크스페이스 루트 하위 파일/디렉토리
  목록(무시디렉토리 prune, maxEntries bound). read_file 전에 정확한 이름 발견용.
- IAgentToolbelt.ListFilesAsync + grammar 3곳 lockstep + Local/External loop + 호스트 위임.
- FileTools 테스트 22개(+4).

## 결과 — Automations

- `AutomationSchedule`(ZeroCommon, 순수) — `every Nm/Nh` / `hourly` / `daily HH:mm` 파싱 +
  next-run 계산(UTC 결정적) + IsDue. 12 테스트.
- `Automation` 엔티티 + `AddAutomations` 마이그레이션 + DbContext 인덱스.
- `-cli automation create/list/remove/due`(in-process, EnsureDbReady).
- `AutomationScheduler`(WPF) — DispatcherTimer(60s)로 due 자동화를 봇 PostAiRequest로 발화 +
  next-run 갱신. MainWindow.OnLoaded에서 기동.

## 검증

- 헤드리스: list_files 4 + automation 12 = 신규, 전체 회귀 **419 통과 / 0 실패**(+16).
- E2E Tier 1: automation create/invalid/list+remove 추가 → **10/10 PASS**.
- E2E 하네스 개선: `Invoke-Cli`가 공백 포함 인자를 인용(Start-Process 배열 미인용 버그 대응) →
  `--schedule "every 30m"` 정상 전달. Tier 3 terminal-send에도 이로움.
- `dotnet build AgentZeroWpf` → 오류 0.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | list_files도 루트 샌드박스. 자동화는 로컬 DB + 봇 경유. 미파싱 스케줄은 발화 정지(안전). |
| 아키텍처 정합성 | Pass | 순수 로직(ListFiles/AutomationSchedule) ZeroCommon. 스케줄러만 WPF. |
| 테스트 가능성 | A | 파일목록·스케줄 계산 전부 헤드리스. |
| 이식 충실도 | Pass | orca automations 개념 이식. list_files는 사용성 개선. |

## 다음 단계 제안

- Automations: 워크스페이스 타겟팅(현재 활성 워크스페이스에 발화) 정교화, Settings UI.
- 남은 배치: W6 실배선, 커맨드 팔레트.
