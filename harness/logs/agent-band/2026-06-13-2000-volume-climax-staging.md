---
date: 2026-06-13T20:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "클라이막스 구분 — idle=일반노래, play/dance=볼륨 클때 발동. 음량체크로 일반노래 빈도 약간↑. 일반=가수 하나만 연주, 볼륨 클때=다같이 연출개선"
---

# Agent Band — 음량 기반 클라이막스 연출 (v0.11.0)

## 실행 요약

여가수 그룹의 play/dance ↔ idle 전환을 **AST 라벨 score 가 아니라 음량(loudness)** 으로
재설계해, "일반노래"와 "클라이막스"를 분리했다.

### 연출 규칙 (요청 → 구현)
1. **idle = 일반노래, play/dance = 볼륨 클때 발동**
   → 프레임마다 `updateClimax()` 가 스펙트럼 평균 에너지(`volumeLevel`, 0~1)를 읽어 climax 판정.
2. **일반상태 = 가수 중 하나만 play/dance** / **볼륨 클때 = 다같이**
   → `performState(p)`:
   - 여성풀(FEMALE_POOL) 멤버: `climaxActive || id===MAIN_VOCAL_ID ? 'play' : 'idle'`
     → 평상시엔 **메인보컬(vocal-ex)만** 연주, 백업 vox7 은 idle. climax 면 **전원** play/dance.
   - 악기/남성보컬: 기존 score 기반 p.state 유지(영향 없음).
   - fading 중이면 idle.
3. **일반노래 상태를 조금 더 오래/자주 유지**
   → 히스테리시스 `CLIMAX_VOL_ENTER(0.30) > CLIMAX_VOL_EXIT(0.20)` + ENTER 를 약간 높게 잡아
   클라이막스 진입을 보수적으로 → 일반노래 빈도 소폭 증가. `CLIMAX_VOL_EASE(0.18)` 로 스파이크 완충.

### 구현 디테일
- 상태 결정은 **렌더 루프(60fps) 단위**라 음량 변화에 실시간 반응. `tickSmoothSpectrum()`+
  `updateClimax()` 를 퍼포머 그리기 **앞으로** 이동시켜 같은 프레임 음량으로 그리게 함.
- presence(무대 잔류) score 와 animation(play/idle) 결정을 **분리**: `upsertIdolGroup` 은
  멤버를 SCORE_PRESENT 로 잔류시키기만 하고, 실제 모션은 `performState` 가 매 프레임 판정.
- climax 진입 시 idle↔play(=dance) 시트 전환이 끊기지 않게 멤버 스폰 시 **양 시트 프리로드**.
- `onStop` 에 `volumeLevel/climaxActive` 리셋 추가.

## 결과 (변경 파일)
- `Project/Plugins/agent-band/agent-band.js`
  - 신규: 튜너블 `CLIMAX_VOL_ENTER/EXIT/EASE`, 상태 `volumeLevel/climaxActive`,
    `updateClimax()`, `performState(p)`
  - 수정: `drawPerformer`(performState 사용), `renderLoop`(순서 재배치), `upsertIdolGroup`
    (score/animation 분리 + 시트 프리로드), `onStop`(리셋), v0.11 changelog
- `Project/Plugins/agent-band/manifest.json` — 0.10.0 → 0.11.0
- `node --check` 통과

## 평가 (3축)

| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | **A** | presence/animation 책임 분리로 score 플로어 꼼수 제거. performState 순수 함수, climax 는 히스테리시스로 플리커 방지. 양 시트 프리로드로 전환 팝 방지. syntax OK. |
| 아키텍처 정합성 | **A−** | "음량→연출"을 렌더 루프의 단일 게이트로 캡슐화, 기존 score 기반 악기 경로는 무변경. 다만 임계값(ENTER/EXIT)이 정규화 스펙트럼 평균의 실제 분포에 의존 → 라이브 튜닝 1회 필요(상수화로 리스크 격리). |
| 테스트 가능성 | **B+** | performState/updateClimax 는 입력(climaxActive/volumeLevel)만으로 결정되어 단위 테스트 용이. 실제 음량 분포 검증은 실시간 필요. |

## 다음 단계 제안
- **라이브 튜닝(중요)**: `CLIMAX_VOL_ENTER/EXIT` 는 스펙트럼 평균값 분포에 맞춘 추정치(0.30/0.20).
  실제로 ① 잔잔한 곡에서 메인만 연주 ② 후렴/드롭에서 전원 폭발 ③ 진입이 너무 잦/드물지 확인 후 조정.
  (전원 연출이 안 터지면 ENTER 하향, 너무 자주면 상향)
- (선택) climax 시 전용 글로우/노트 입자 강화로 클라이막스 시각 임팩트 추가.
- (선택) 메인보컬도 진짜 잔잔한 구간선 idle 로 두는 "초저음량" 3단계 분리 검토.
