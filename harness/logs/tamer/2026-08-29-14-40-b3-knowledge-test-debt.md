---
date: 2026-08-29T14:40:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "B3 진행"
follows: harness/logs/tamer/2026-08-29-14-10-b1-reliability-adoption.md
---

# 개선 브리핑 B3 채택 — 지식/테스트 부채 3건

operator 가 브리핑 **B3(지식/테스트 부채)** 묶음 채택 지시. 조사로 각 항목의
실제 상태를 재확인한 뒤 처리.

## 실행 요약

### B3-1 · voice-curator 지식 [필수] → 이미 충족, 소폭 보강
조사 결과 **이미 해소됨**: `native-inference-lifetime.md`(2026-08-19)가 v0.16.0
회귀 리뷰가 지적한 Fail 축(chunk-scoped 메모리 + `_noteInferGate` 직렬화)을
리뷰 10일 뒤 이미 문서화. 현재 코드와도 무drift 확인(DiarizeAsync per-call
using, `[Diar] chunk-scoped` 로그, gate blocking/try-acquire 분리 모두 일치).
→ partial↔chunk **drop 정책 비대칭**(chunk만 "dropped N bytes" 로그, partial은
조용히 skip)만 1문단 보강.

### B3-2 · music agent-band 지식 → 실제 작업
- `agent-band-mapping.md` 가 **stale**(v0.6.0). M0031 신규 7연주자
  (elec-guitar/elec-bass/synth/keytar/dj-deck/drum-machine/edrum)·정렬 계약
  ·`(?<!speech )synthesizer` lookbehind·genre fallback 누락 + guitar/piano/drum
  행이 잘못된 매핑 명시. → 현재 코드(L638-704)로 전면 갱신.
- 유튜브 무대(M0026) 지식 **전무** → `agent-band-youtube-stage.md` 신설
  (oEmbed/classify host-op 계약, SSRF 가드=서버측 canonical URL 재구성,
  `llm-not-ready` 폴백, `MatchCategory` 클램프, parseVideoId↔ParseYouTubeId 미러).
- `music-curator.md` 백링크 2건 + watched files(agent-band.js, WebDevHost.cs,
  tools/agent-band-tests/) 추가 — 기존 orphan 상태 해소.

### B3-3 · JS 미니 테스트 러너 → 신설
`tools/agent-band-tests/` (zero-dep `node --test`). `pure.mjs` = plugin IIFE
의 순수 헬퍼(parseVideoId·labelToPerformer) hand-aligned 참조 사본(=repo 기존
C#/JS 미러 관습). 위치는 번들 밖: `Wasm/**` 은 앱 출력물로 복사되고 plugin 은
200파일 설치 상한이 있어 둘 다 부적합.

## 결과 / 검증

| 게이트 | 결과 |
|---|---|
| `node --test` (tools/agent-band-tests) | **20 통과 / 0 실패** |
| 지식 문서 코드 대조 | agent-band.js L638-704 / L2130-2141, WebDevHost.cs 계약과 일치 |

변경: `native-inference-lifetime.md`(보강), `agent-band-mapping.md`(갱신),
`agent-band-youtube-stage.md`(신설), `music-curator.md`(백링크/watched),
`knowledge/README.md`(트리에 music/voice-curator 추가), `harness.config.json`
(1.12.0→1.12.1), `harness/docs/v1.12.1.md`(신설), `tools/agent-band-tests/`(신설).

## 평가 (3축)

| 축 | 결과 |
|----|------|
| 워크플로우 개선도 | **A** — stale 지식 3건 정정/신설 + JS 회귀 인프라 부트스트랩, 전 항목 코드 대조. |
| Claude 스킬 활용도 | **3/5** — node --test, 코드 대조. |
| 하네스 성숙도 | **L4** — 지식층 정합성 회복 + 백링크 완비. hand-aligned 사본 drift 리스크가 잔여(추후 모듈 추출로 L5). |

creator-rule: Rule 3(무트리거)/4(per-agent 배치)/5(백링크) 통과.

## 다음 단계 제안
- **B3-1 실기기 검증**(5분+ diarizer buildMs/메모리) 은 여전히 operator 스모크 대기.
- `labelToPerformer`/`parseVideoId` 를 공유 ES 모듈로 **추출**해 `pure.mjs` 사본
  제거 → drift 리스크 근절 (별도 mission 권장).
- 남은 브리핑 묶음: **B2**(orca/herdr 채택), **B4**(제품 폴리시) operator 대기.
