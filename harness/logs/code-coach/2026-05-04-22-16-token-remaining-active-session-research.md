---
date: 2026-05-04T13:16:55Z
agent: code-coach
type: research
mode: log-eval
trigger: "코드코칭 소환해 문제검토"
related: [M0011, M0013]
---

# Token-Remaining 위젯 — Active Session 패널 추가를 위한 수집장치 검토

## 운영자 의도 (Brief)

M0011 로 출시한 token-remaining 위젯의 모델별 구분이 의미 없음을 확인 (M0011
수행결과 line 164–166 — Opus / Sonnet 의 5h%/Weekly% 가 동일 — Claude Code
rate_limits 가 계정 단위 quota 임). 이에 두 가지 변경:

- **REQ-A** (단순화): 토큰 잔여 표시는 유지하되 모델명 / 모델별 on-off 필터 제거,
  계정 단위 단일 row 로 평탄화.
- **REQ-B** (신규): 별도 "Active Session" 패널 추가 — `(account, session, cwd,
  model)` 튜플 중 **최근 3 분 이내 tick 한** 세션을 나열. ON/OFF 토글 지원.
  모델 정보는 여기서 "어떤 모델로 활성 중인지" 의미로 살아남음.

UX 변경 전 수집장치 / 스키마 검토 — 데이터 마이그레이션 필요 여부 결정.

## 현재 데이터 흐름 (3-단계 파이프라인)

```
Claude Code statusLine (~300ms tick)
    │  stdin = full JSON (session_id, cwd, model, workspace, rate_limits, ...)
    ▼
az-hud-wrapper.js  (StatusLineWrapperInstaller.cs:642–684, embedded WrapperScript)
    │  → only extracts: model.display_name, rate_limits.{five_hour,seven_day}
    │  → writes ONE file per account (overwrites each tick):
    │     %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\{account}.json
    │  → drops: session_id, cwd, workspace.{current_dir,project_dir},
    │           transcript_path, version, output_style, model.id
    ▼
TokenRemainingCollector  (30s cadence, dedupe on (account, model, 5h%, 7d%, resetsAt))
    │  → INSERT TokenRemainingObservation when tuple changes
    ▼
SQLite TokenRemainingObservation
    columns: Vendor, AccountKey, Model, FiveHourPercent, FiveHourResetsAtUtc,
             SevenDayPercent, SevenDayResetsAtUtc, ObservedAtUtc
    index: (AccountKey, Model, ObservedAtUtc DESC)
```

### 핵심 갭 — 셋 다 같은 원인 (wrapper drop)

| 갭 | 위치 | 영향 |
|----|------|------|
| `session_id` 미캡처 | wrapper.js line 651 (parse 후 버림) | REQ-B 의 "session" 차원 불가 |
| `cwd` / `project_dir` 미캡처 | 같음 | REQ-B 의 "directory" 차원 불가 |
| 스냅샷 파일 1개/계정 (overwrite) | wrapper.js line 566 (`account + '.json'`) | 동일 계정의 동시 세션이 서로 덮어씀 → 다중 세션 자체가 보이지 않음 |
| DB 에 `SessionId` / `Cwd` 컬럼 부재 | TokenRemainingObservation.cs | "active 3 min" 쿼리 불가 |
| 현재 Dedupe 의미론은 "rate-limit 상태변화 로그" | Collector.cs:146–164 | "session heartbeat" 의미론 (매 tick 마다 LastSeen 갱신) 와 충돌 |

### REQ-A 영향 분석

- **Display 만 변경**: 위젯이 row 를 (계정) 기준으로 collapse 하면 끝. 스키마 변경 0.
- **Model 컬럼 제거?**: 비추천. M0013 이후로도 "마지막으로 본 모델명" 은
  Active Session 패널에서 살아남음. 스키마 보존 비용 ≈ 0 byte 압박.
- 결론: REQ-A 는 **순수 UX 변경**, 마이그레이션 불필요.

### REQ-B 영향 분석 — 의미론 충돌

현재 `TokenRemainingObservation` 은 **state-change log** 의미론 (rate-limit 가 변할 때만 row 추가). REQ-B 가 요구하는 것은 **heartbeat** 의미론 (매 tick 마다 "이 세션 살아있다" 갱신). 두 개를 같은 테이블에 섞으면:

- Dedupe 키에 SessionId 추가 → 세션마다 동일한 rate-limit row 가 N 배로 부풀음 (계정 quota 인데 N 명이 같은 값을 N row 로)
- Dedupe 키에서 SessionId 빼고 별도 LastSeenAt 컬럼 → row 의미가 둘로 갈라짐

→ **분리된 entity 가 정답**.

## Option A / B / C — 트레이드오프

### Option A — 추천 ★ 별도 SessionHeartbeat 테이블

**스키마 추가** (마이그레이션 1개):
```csharp
public class SessionHeartbeat
{
    public long Id { get; set; }
    public string AccountKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Cwd { get; set; } = "";          // workspace.current_dir
    public string ProjectDir { get; set; } = "";   // workspace.project_dir
    public string Model { get; set; } = "";        // model.display_name (last seen)
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long TickCount { get; set; }
}
// UNIQUE INDEX (AccountKey, SessionId)
// INDEX (LastSeenUtc DESC)  — for "active in last 3 min" hot path
```

**Wrapper 변경** (3 줄 정도):
1. `parsed.session_id`, `parsed.cwd`, `parsed.workspace?.project_dir` 추출
2. 스냅샷 경로: `cc-hud-snapshots/{account}/{sessionId}.json` (subdir per account)
3. 페이로드에 위 3 필드 추가

**Collector 변경**: 새로 `SessionHeartbeatCollector` (또는 기존 collector 의 두 번째
작업) — 매 tick 마다 UPSERT (`LastSeenUtc = wrapper.writtenAt, TickCount++`).
기존 `TokenRemainingObservation` 의 dedupe 로직은 **건드리지 않음**.

**Query** (3 분 active):
```csharp
db.SessionHeartbeats
    .Where(s => s.LastSeenUtc >= DateTime.UtcNow.AddMinutes(-3))
    .OrderByDescending(s => s.LastSeenUtc)
    .ToList();
```

**트레이드오프**:
- 👍 의미론 깨끗 (state-log vs heartbeat 완전 분리).
- 👍 기존 `TokenRemainingObservation` row 한 줄도 안 건드림 — 데이터 마이그레이션 0.
- 👍 인덱스 LastSeenUtc 가 핫패스에 직진. 오래된 heartbeat 청소 (>1 day) cron 일감도 자연스러움.
- 👎 추가 entity / migration 1 개 / collector 한 단 추가 (코드 ~100줄).
- 👎 wrapper 변경 = 운영자 환경 재배포 필요 (wrapper.js 의 v 번호 갱신 → installer 가 자동 재작성).

### Option B — 단일 테이블 확장

`TokenRemainingObservation` 에 `SessionId / Cwd / ProjectDir / LastSeenUtc / TickCount` 추가, dedupe 키 변경.

**트레이드오프**:
- 👍 마이그레이션 1 개로 끝 (테이블도 1 개).
- 👎 의미론 혼재 — row 하나가 "rate-limit 상태 + 세션 heartbeat" 둘 다 표현. 향후
  추가 차원 (예: 세션별 토큰 누적) 들어오면 더 더러워짐.
- 👎 기존 row 들을 `SessionId=""` 로 후행 채움 — 계정 단위 집계 시 빈 SessionId 가 노이즈.
- 👎 dedupe 키 변경이 historical 분석을 깨뜨림 (이전엔 모델 단위 변화 추적, 새 키는 세션 단위 변화 추적).

### Option C — 파일시스템만, DB 무변경

스냅샷을 `cc-hud-snapshots/{account}/{sessionId}.json` 로 fan-out 만 하고, Active
Session 패널은 **파일 mtime 으로** 직접 계산. DB 쪽은 token-remaining 만.

**트레이드오프**:
- 👍 가장 빠른 ship — 마이그레이션 0, entity 0, query service 0.
- 👍 Wrapper 변경만 (scope 작음).
- 👎 앱 재시작 / 파일 정리 시 active 정보 사라짐. "어제 X 디렉토리에서 활동" 같은
  히스토리 불가.
- 👎 stale 파일 청소 정책 필요 (cron 또는 wrapper 가 부팅 시 자기 것 cleanup).
- 👎 WebDevBridge RPC 가 fs 직접 읽기 → 추후 DB 로 옮기면 API 모양 바뀜
  (forward-compat 부담).

## 추천 — Option A, 단계 분할 가능

**단일 큰 PR 보다 2 단계로 분할 가능:**

- **Phase 1 (REQ-A only, 무위험)**: 위젯에서 모델 row collapse + 모델명 / 토글 제거.
  스키마 / 수집기 / wrapper 모두 그대로. 30 분 작업.
- **Phase 2 (REQ-B, Option A)**: SessionHeartbeat entity + migration + wrapper v3 +
  새 collector + WebDevBridge RPC + Active Session 패널 UI. 4–6 시간 작업.

운영자가 위젯 단순화부터 보고 싶다면 Phase 1 만 먼저 보내고, 그 사이 Active
Session 패널 디자인을 .pen 으로 한 번 더 검토 가능.

## 참고 — Claude Code statusLine stdin 페이로드 (공식 문서 기준)

```json
{
  "session_id": "uuid-...",
  "transcript_path": "/path/to/transcript.json",
  "cwd": "C:/Code/AI/AgentZeroLite",
  "model": { "id": "claude-opus-4-7", "display_name": "Opus 4.7 (1M context)" },
  "workspace": {
    "current_dir": "C:/Code/AI/AgentZeroLite",
    "project_dir": "C:/Code/AI/AgentZeroLite"
  },
  "version": "...",
  "output_style": { "name": "default" },
  "rate_limits": {
    "five_hour": { "used_percentage": 19, "resets_at": 1714829292 },
    "seven_day": { "used_percentage": 9,  "resets_at": 1715347692 }
  }
}
```

→ REQ-B 에 필요한 필드는 모두 stdin 에 이미 도착. **wrapper 가 버리고 있을 뿐.**

## 결론 (TL;DR for 운영자)

| 질문 | 답 |
|------|-----|
| REQ-A (모델 표시 제거) 마이그레이션 필요? | **불필요**. 순수 UX. |
| REQ-B (Active Session 패널) 가능? | **현재 데이터로는 불가능**. wrapper 가 session_id / cwd 를 버리고 있음 + 스냅샷 파일이 계정당 1개 (concurrent session overwrite). |
| 스키마 마이그레이션 필요? | **필요 — 신규 SessionHeartbeat 테이블** (Option A 추천). 기존 TokenRemainingObservation 은 무손상. |
| Wrapper 재설치 필요? | **필요**. WrapperScript 버전 bump → installer 가 wrapper.js 디스크에 자동 재작성. settings.json 은 손대지 않음 (statusLine 명령 모양은 동일). |
| 작업 분할? | Phase 1 (REQ-A, 30 min) → Phase 2 (REQ-B, 4–6 h). 사이에 운영자 .pen 디자인 리뷰 가능. |

## 평가 (code-coach Mode 3 rubric)

| 축 | 평가 | 메모 |
|----|------|------|
| Cross-stack judgment | A | wrapper (Node) ↔ collector (.NET/EF) ↔ widget (JS) 세 층의 의미론 충돌 (state-log vs heartbeat) 짚음. |
| Actionability | A | Option A/B/C 각각 구체적 entity 스킴 + wrapper 라인 + migration 개수 명시. Phase 1/2 분할로 작업 단위까지. |
| Research depth | A | stdin payload 공식 스펙 인용, M0011 의 line 164–166 사실 확인 (rate_limits = account quota), wrapper.js drop 위치 라인 번호로 특정. |
| Knowledge capture | Pass | 본 로그가 곧 knowledge 후보. M0011 의 "rate_limits 가 모델 무관" 사후 검증을 본 로그가 확정 — `harness/knowledge/code-coach/` 로 codify 여지 있음 (다음 미션에서). |
| Issue handoff | N/A | Mode 3 (research consult), pre-commit 아님. 운영자 결정 후 미션화. |

## 다음 단계 제안

- **M0013 후보 갱신**: "rate_limits empirical 확인" 은 M0011 후속 데이터로 사실
  확정됨 (Opus/Sonnet 동일 값) — 별도 미션 불필요. 대신 **M0013 = 본 로그의
  Phase 1+2 구현 미션**으로 재정의 권장.
- **펜슬 디자인 리뷰**: Active Session 패널은 신규 UI surface. `Docs/design/`
  하위에 .pen 으로 mockup → 운영자 확정 후 코드 진입 (M0011 와 동일 설계-우선
  플로우).
- **Wrapper 버전 정책**: 현재 v2.2 (M0011 follow-up). REQ-B 적용 시 v3.0 으로
  major bump (snapshot 디렉토리 구조 변경 = breaking). installer 가 v3 wrapper
  배포 시 기존 `cc-hud-snapshots/*.json` 파일 정리 + subdir 마이그레이션 1회 실행.
