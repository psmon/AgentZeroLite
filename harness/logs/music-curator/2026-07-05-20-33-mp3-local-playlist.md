---
date: 2026-07-05T20:33:51+09:00
agent: music-curator
type: creation
mode: log-eval
trigger: "M0029 — 로컬 MP3 플레이리스트 (채팅 브리프 → 미션 등록 후 수행)"
---

# Agent Band v0.16.0 — 로컬 MP3 플레이리스트 (M0029)

## 실행 요약

유튜브 전용이던 Agent Band 재생 소스에 로컬 MP3를 추가. 스캔(백그라운드 잡) →
태그+파일명+폴더명 힌트 LLM 분류 → SQLite 영속 목록 → `mp3.local` 가상호스트
`<audio>` 재생 → 기존 SystemLoopback 파이프라인이 그대로 밴드 구동.
확장 2건 포함: 재생 중 보유악기 실시간 누적(+악기 검색 필터), 스캔 증분 목록.

## 결과

- 호스트: `WebDevHost.Mp3.cs` 파셜 + `mp3.*` 브릿지 op 9종 + 이벤트 3종
- 데이터: `Mp3Track` 엔티티 + `AddMp3Tracks` 마이그레이션 (FilePath unique upsert)
- 플러그인: 소스 탭/MP3 플레이어/악기 필터 — MP3 모드는 일반(악기·가수) 전용,
  Singer solo 강제 (걸그룹 비전은 유튜브 iframe 캡처 기반이므로)
- 검증: 빌드 0/0, 헤드리스 298 pass, `node --check` OK

상세: `harness/logs/mission-records/M0029-수행결과.md`

## 평가

- 큐레이션 관점: 카테고리 세트를 YT_CATEGORIES와 공유해 탭 UX 일관성 유지 — 적절.
- 악기 어휘: `labelToPerformer` 캐논 키를 그대로 저장·검색 어휘로 씀 — 비전(M0028)과도
  같은 어휘라 향후 교차 활용 여지 있음.
- 리스크: LLM 분류는 스캔 잡 내 순차 실행이라 대용량 폴더 + 느린 로컬 모델 조합에서
  분류 지연 가능 (재생은 즉시 가능하므로 UX 차단은 아님). 필요 시 분류 큐 분리 검토.

## 다음 단계 제안

- 대용량 라이브러리용 분류 배치 상한/야간 배치 옵션
- 앨범아트(ID3 APIC) 추출 → 목록 썸네일
- 유튜브 목록도 SQLite로 이관해 영속 계층 통일 (localStorage 한계 제거)
