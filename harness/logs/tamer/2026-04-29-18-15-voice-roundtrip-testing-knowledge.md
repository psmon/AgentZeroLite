---
date: 2026-04-29T18:15:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "지금 유닛테스트 중요한것을 발견 ..관련전문가 지식업데이트"
---

# Knowledge Update — Voice Round-Trip Testing Methodology

## 실행 요약

The pure-model TTS↔STT round-trip test suite (commit `b1a0ec8`) was
the first time the voice subsystem got a deterministic, repeatable
quality measurement that doesn't depend on a microphone, speaker, or
human in the loop. While building it, six non-obvious lessons emerged
that are worth binding into agent convention sets so they don't have
to be re-derived next time.

## 결과

### 신규 파일
- **`harness/knowledge/voice-roundtrip-testing.md`** — Six-lesson
  methodology document. Sections: purpose, topology, six lessons
  (normalisation, Whisper auto-correction, dual output channel,
  WAV evidence, performance baseline, public-helpers-for-tests),
  operational rules, and "where this knowledge applies."

- **`harness/docs/v1.1.7.md`** — version bump record.

### 수정 파일
- **`harness/agents/test-sentinel.md`** — Added a new
  "Owned convention sets" section. The voice-round-trip knowledge
  is binding for any diff touching `TtsSttRoundTripTests.cs` or
  new voice-quality tests. The existing `dotnet-test-execution.md`
  rule was already enforced in the Procedure section; promoted into
  the new "Owned convention sets" structure for consistency.

- **`harness/agents/code-coach.md`** — Added the new knowledge file
  to its "Owned convention sets" with a generalised binding: ANY
  LLM-output comparison logic (STT, OCR, generation evaluators)
  should adopt the case+punct+whitespace+lowercase fold pattern.
  Cites the Whisper auto-correction case as the canonical example
  of "model-correctness ≠ test-strictness".

- **`harness/harness.config.json`** — version 1.1.6 → 1.1.7, lastUpdated
  → 2026-04-29.

## 평가 (3축)

| 축 | 등급 | 근거 |
|----|------|------|
| 워크플로우 개선도 | **B** | Two agents (test-sentinel, code-coach) get binding rules for voice / LLM-output comparison testing. Cost to add: ~45 min. The "fold and surface" pattern is directly portable to OCR / generation evaluator tests we'll write later. The Whisper auto-correction insight is genuinely non-obvious — without a written record, the next person to hit it will re-debate "is this a flake?" until the same conclusion is reached. |
| Claude 스킬 활용도 | **2 / 5** | Pure knowledge work — no external tools. The downstream payoff is internal: code-coach Mode 2 reviews now have a checklist for LLM-output assertions, and test-sentinel's review has fixture-parity + WAV-evidence rules. |
| 하네스 성숙도 | **L3+ → L3++** | Knowledge count 9 → 10. Each new knowledge file owned by ≥ 1 agent (test-sentinel + code-coach both reference). Engine count unchanged (2). Agent count unchanged (5). |

## 이번 사이클의 핵심 발견 (knowledge file에 반영됨)

1. **Whisper 자동 의미 교정**: 사용자 입력 typo `모래의날씨` → 모델이 의미적으로 올바른 `모레의 날씨`로 transcribe. 1글자 drift / similarity 96.8%. **테스트는 이를 정직하게 surface 해야 함**, 더 느슨한 threshold로 숨기지 않음 — 모델 능력의 측정 신호.
2. **정규화 layering**: `==` 너무 strict (대소문자/구두점에 fail), substring 너무 loose (실제 환각 못 감지). `char.IsPunctuation` + `IsWhiteSpace` 제거 + `ToLowerInvariant` 가 적절한 중간점. 한국어는 case 영향 없음 → 안전하게 일괄 적용.
3. **이중 출력 채널 (ITestOutputHelper + Console.WriteLine)**: xUnit 통합 vs detailed-logger 양쪽에서 가시성 보장.
4. **WAV 증거 자동 저장**: 매 케이스마다 `-tts.wav` (원본) + `-stt-input-16k.wav` (resample 후) 두 파일을 `%TEMP%` 에 저장. 실패 시 사람이 직접 들어보고 진단 가능 → 재실행 불필요.
5. **Whisper Medium CPU 성능 베이스라인** 기록 (1.7–6.3× realtime; 짧은 발화일수록 per-call overhead 비중 큼) — 향후 GPU/cloud 전환 시 회귀 감지 기준.
6. **InternalsVisibleTo 보다 public 헬퍼**: `WavToPcm.To16kMono`는 production이 부르는 것과 정확히 같은 transformation. 테스트 가시성용 InternalsVisibleTo 추가보다 helper를 public으로 노출하는 게 cleaner — 캡슐화할 비밀이 없을 때.

## 다음 단계 제안

- [ ] 같은 테스트 패턴을 cloud STT (OpenAI Whisper) 에도 적용 → 모델 swap 시 회귀 측정 데이터 확보
- [ ] Whisper small (현재 운영 default) vs medium (테스트 default) round-trip 정확도 비교 테스트 추가
- [ ] Stream pipeline (Akka.Streams) 도입 시 같은 fixtures 로 측정 → 베이스라인과 delta 비교
- [ ] 사용자가 명시적으로 "노션에 작성" 도 요청했음 — `psmon-doc-writer` 스킬을 별도 호출하여 노션 게시
