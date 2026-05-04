---
id: M0012
title: Token-Remaining 위젯 단순화 + Active Session 패널 분리
operator: psmon
language: ko
status: done
priority: medium
created: 2026-05-04
started: 2026-05-04T22:30:00+09:00
finished: 2026-05-04T23:10:00+09:00
related: [M0011]
---

# 요청 (Brief)

M0011 로 출시한 token-remaining 위젯의 모델별 구분이 의미 없음을 확인 — Claude
Code `rate_limits` 는 **계정 단위 quota** 임 (Opus / Sonnet 동시 관측에서 5h%
/ 7d% 동일값 누적). 따라서 위젯의 모델 차원을 제거하고, 살아남는 "어떤 모델
이 어디서 활성 중인가" 라는 가치는 **별도 Active Session 패널** 로 분리한다.

사전조사: `harness/logs/code-coach/2026-05-04-22-16-token-remaining-active-session-research.md`
(Option A 추천 — 별도 `SessionHeartbeat` 테이블, 기존 `TokenRemainingObservation`
무손상).

## 두 단계로 분할

### Phase 1 — 토큰 잔여 위젯 단순화 (REQ-A)

- 위젯 본체에서 모델별 row 제거 → 계정 단위 단일 row 로 collapse
  (5h / 7d 두 줄만 표시, 모델명 / 모델 토글 UI 제거).
- Settings 의 "Models per account" 동적 토글 섹션 제거.
- 스키마 / wrapper / collector 변경 **없음** (마이그레이션 불필요).
- ON/OFF 토글은 위젯 자체 표시 토글로 유지 (다음 패널과 독립).

### Phase 2 — Active Session 패널 (REQ-B)

신규 패널: 최근 3 분 이내 statusLine tick 한 `(account, session, cwd, model)`
조합을 표 형태로 나열. ON/OFF 토글로 표시 제어. Phase 1 위젯과 동일한 호스트
플러그인에 두 번째 surface 로 추가.

#### 데이터 레이어

- 신규 entity `SessionHeartbeat`:
  ```
  AccountKey, SessionId, Cwd, ProjectDir, Model,
  FirstSeenUtc, LastSeenUtc, TickCount
  UNIQUE INDEX (AccountKey, SessionId)
  INDEX (LastSeenUtc DESC)
  ```
- EF 마이그레이션 1 개 — 기존 `TokenRemainingObservation` 무손상.
- 신규 collector (또는 기존 `TokenRemainingCollector` 의 두 번째 작업) — 매 tick
  마다 UPSERT (`LastSeenUtc = wrapper.writtenAt`, `TickCount++`).

#### Wrapper 변경 (v3.0 major bump)

- `parsed.session_id`, `parsed.cwd`, `parsed.workspace?.project_dir` 추출 추가.
- 스냅샷 경로: `cc-hud-snapshots/{account}/{sessionId}.json` (subdir per account).
  → 동일 계정의 동시 세션이 더 이상 서로를 덮어쓰지 않음.
- WrapperScript 버전 bump → installer 가 wrapper.js 디스크에 자동 재작성
  (settings.json 의 statusLine 명령은 동일 — 사용자 측 손작업 0).
- 기존 `cc-hud-snapshots/*.json` 평탄 파일은 v3 부팅 시 1회 cleanup 또는 무시.

#### Query / RPC

- `SessionHeartbeatQueryService.GetActive(TimeSpan window)` —
  `LastSeenUtc >= UtcNow - window` 정렬.
- `WebDevBridge` 에 `tokens.remaining.activeSessions` RPC + `activeSessions.tick`
  push 이벤트 추가.

#### UI

- 디자인-우선: `Docs/design/M0012-active-session-panel.pen` mockup 먼저 검토
  (M0011 동일 플로우).
- 패널 컬럼: `Account | Model | Project (basename) | Session (short id) | Last seen (s ago)`.
- 풋터: 활성 세션 수 + heartbeat collector tick age.

## Acceptance

### Phase 1
- [ ] 위젯에서 모델 row / 모델명 / 모델 토글 UI 제거
- [ ] 계정 단위 5h / 7d 두 줄 표시 (1 계정 = 2 줄)
- [ ] 기존 DB / wrapper / collector 무변경 확인
- [ ] localStorage prefs 의 `hidden models` 키 정리 (이주 또는 무시)

### Phase 2
- [ ] `Docs/design/M0012-active-session-panel.pen` 디자인 운영자 승인
- [ ] `SessionHeartbeat` entity + migration 적용
- [ ] Wrapper v3.0 — session_id / cwd / project_dir 캡처 + per-session 스냅샷
- [ ] Heartbeat collector — UPSERT 의미론, 중복 row 없음
- [ ] Active Session 패널 UI — 3 분 window, ON/OFF 토글
- [ ] WebDevBridge RPC + push 이벤트
- [ ] 동시 세션 시뮬레이션: 같은 계정에서 2 개 Claude Code 동시 실행 → 두 세션
      모두 패널에 노출 (덮어쓰기 없음)
- [ ] stale heartbeat (>1 day) 청소 정책 (cron 또는 collector 부팅 시)

## Notes

- 사전조사 산출물: `harness/logs/code-coach/2026-05-04-22-16-token-remaining-active-session-research.md`
- M0011 후속노트의 "M0013 = rate_limits empirical 확인" 은 본 검토로 사실 확정 →
  별도 미션 불필요. M0011 의 "M0015 잠재 = wrapper IO contention" 은 wrapper v3
  의 per-session fan-out 으로 IO 부하 패턴이 바뀌므로 Phase 2 끝나면 재평가.
- Phase 1 만 먼저 머지하고 Phase 2 는 .pen 디자인 리뷰 사이클을 한 번 가질지,
  한 번에 묶을지는 운영자 선택. 본 미션 카드는 두 Phase 를 한 미션으로 묶지만
  실제 커밋은 분리 가능.
- Wrapper major bump 정책: v2.x → v3.0. Installer 가 디스크의 wrapper.js sha256
  비교로 자동 재배포 (M0011 의 install state 매커니즘 그대로 활용).
