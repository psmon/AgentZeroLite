---
date: 2026-08-30T15:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "b4 진행 (worktree feat/b4-polish)"
follows: harness/logs/herdr-adoption/2026-08-29-15-30-h3-launch-h5-alias-h4-authority.md
---

# 개선 브리핑 B4 채택 — 제품 폴리시 (무효화 훅 + YouTube→SQLite)

operator가 브리핑 **B4(제품 폴리시)** 채택. 실착수 가능한 코드 2건을 워크트리
`feat/b4-polish`에서 구현. (viola/oboe 매핑은 모델 blocked, 실기기 스모크는
operator 몫이라 제외.)

## B4-1 · classifier 인스턴스 무효화 훅 (M0025 #12)

문제: `WebDevHost._musicClassifier`(Agent Band 라이브 분류)가 `??=`로 캐시되어
프로세스 종료 전까지 교체 안 됨 → Music 설정에서 모델 경로를 바꿔도 다음 Start가
**옛 모델 경로**로 동작(기존 우회책=플러그인 리로드).

- `MusicSettingsStore.Changed` **정적 이벤트** + Save 말미 raise + `NotifyChanged()`
  (다운로드 경로용). Save 경유 두 지점(OnMusicSave, Start-time save) 자동 커버.
- `WebDevHost` ctor에서 구독 → `_musicClassifierStale` volatile 플래그. 추론 루프가
  **single-flight 게이트 안에서** 안전한 시점에 dispose+재빌드(fresh settings 재로드)
  → 재시작 없이 새 모델 경로 반영. Dispose에서 구독 해제 + classifier dispose(기존
  ~347MB ONNX 세션 누수도 함께 해소).
- 모델 다운로드(Save 우회)도 `NotifyChanged()`로 커버 (3번째 지점).
- 테스트: `MusicSettingsStoreTests` 3건(이벤트 발화/해제/무구독 안전). Save는 실
  파일을 건드리므로 이벤트 메커니즘만 헤드리스 검증.

## B4-2 · YouTube 목록 → SQLite 이관 (music-curator #29 / M0026)

YouTube 플레이리스트가 플러그인 localStorage에만 있어 영속 계층이 MP3(SQLite)와
분리돼 있던 것을 통일.

- 엔티티 `YouTubePlaylistItem` + DbSet + **VideoId 유니크 인덱스** + EF 마이그레이션
  `20260830055426_AddYouTubePlaylist`(스냅샷 갱신).
- 호스트 ops `WebDevHost.YouTube.cs`: `YtList/YtUpsert(dedupe+60 prune)/YtRemove/YtClear`.
  브릿지 케이스 `youtube.list/upsert/remove/clear`.
- 순수 규칙 `YouTubePlaylistRules`(IsValidVideoId·NormalizeCategory·MaxItems=60) —
  플러그인 상수와 lock-step. **YouTubePlaylistRulesTests 15+건**.
- `agent-band.js`: `savePlaylist`/`loadStoredPlaylist`를 host 호출로 교체 +
  **1회 localStorage→DB 마이그레이션**(레거시 store drain 후 removeItem). `bindYouTube`
  async화(리스너는 동기 바인딩 후 목록 hydrate). 낙관적 UI(host 실패 비치명).

## 검증

| 게이트 | 결과 |
|---|---|
| `dotnet ef migrations add` | 성공 (테이블+유니크 인덱스+스냅샷) |
| WPF 빌드 | **오류 0** |
| headless 전체 | **561 통과 / 24 skip / 0 실패** (+23) |
| 라이브 스모크 | 앱 기동(=마이그레이션 적용) 확인 — 별도 기록 |

## 평가 (3축)

| 축 | 결과 |
|----|------|
| 코드 안전성 | A — 무효화는 single-flight 게이트 내 재빌드로 mid-inference dispose 회피 + 누수 해소. YouTube는 VideoId 유니크로 중복 방지, host 실패 비치명. |
| 아키텍처 정합성 | Pass — 순수 규칙은 ZeroCommon(테스트), 영속은 MP3 패턴 미러, 무효화는 기존 정적-이벤트 관습(LlmService/AppLogger). |
| 테스트 가능성 | A — 이벤트 3 + YouTube 규칙 15+ 헤드리스. DB/WPF 경로는 빌드+라이브 스모크. |

## 다음 단계 제안
- YouTube remove/clear UI 노출(localStorage엔 없던 신규 역량 — host op는 준비됨).
- 모델 다운로드가 Save를 안 거치는 구조라 file-watch로 완전 자동화 검토(현재 NotifyChanged로 커버).
- 남은 브리핑: viola/oboe(모델 blocked), operator 실기기 스모크(M0024/26/29/30).
