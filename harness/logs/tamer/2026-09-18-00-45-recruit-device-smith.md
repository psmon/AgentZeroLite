---
date: 2026-09-18T00:45:00+09:00
agent: tamer
type: recruit
mode: recruit
trigger: "C:\\code\\psmon\\Arduino 여기에 기기를 컴파일하고 제어하는 방법있고 기기가 usb 연결되어있으니 해당 기기 빌드,디버그 하는 방법이 곳에 영입을 해죠"
---

# device-smith 영입 — 기기 쪽 빌드/디버그를 정원에 들이다

## 실행 요약

사용자가 형제 저장소(`C:\code\psmon\Arduino`)의 기기 빌드·제어 방법을 이 정원에 영입할
것을 명시적으로 요청했다 (Mode B 승인에 해당). creator-rule 을 먼저 읽고 Rule 5 의
수정 프로토콜을 따라 진행했다.

함께 들어온 요구 두 가지:
- README 에 형제 git 프로젝트로 등록
- 웨어러블 기기(주로 아두이노 계열이지만 **계열 무관**) 방법은 **별도 문서로 분리** 소개

## 결과

### 원천 분석

| 원천 | 추출한 것 |
|---|---|
| `Arduino/CLIBUILD.md` | arduino-cli 1.5.2, 스케치별 FQBN, VID 기반 포트 판별, 실패 패턴 |
| `Arduino/project/samples/claude_hud_amoled/idf-env.ps1` + README | ESP-IDF v5.5.5, `WS_AMOLED_REPO`, `idf.py -p COM7 build flash monitor`, 공장 펌웨어 복구 |
| 현장 실측 | COM7 = `VID_303A&PID_1001` (ESP32-S3 네이티브 USB). `VID_1A86`(CH343) 보드는 현재 미연결 |

### 타입 판정

`specialist`. 도구·도메인 지식을 들고 산출물(기기)을 다룬다. sage/keeper 아님.

### 중복 책임 (Rule 5-1)

| 대상 | 판정 |
|---|---|
| `build-doctor` | **겹치지 않음** — .NET 파이프라인/네이티브 DLL/버전 vs USB/툴체인/플래시/시리얼 |
| 형제 저장소 `device-resource-warden`, `ble-contract-sentinel` | **겹치지 않음** — 그쪽은 펌웨어 코드 리뷰, 이쪽은 빌드·플래시·관찰. 정원 자체가 다름 |

엔진은 만들지 않았다 — 단일 에이전트 작업이라 Rule 2 (2인 이상) 조건 미해당.

### 생성/변경 파일 (5-파일 패턴 + 제품 문서 2)

| 파일 | 상태 |
|---|---|
| `harness/agents/device-smith.md` | 신규 (트리거 14개) |
| `harness/knowledge/device-smith/wearable-device-toolchain.md` | 신규 (Rule 4: 에이전트별 소유) |
| `harness/harness.config.json` | `agents` + `knowledge_subdirs` 추가, 1.12.1 → **1.13.0** |
| `harness/docs/v1.13.0.md` | 신규 |
| `Docs/wearable-device.md` | 신규 — 사용자 요구한 "별도 분리" 문서 (EN) |
| `Docs/wearable-device.kr.md` | 신규 — KR 판 (후행 요청 "KR문서도 업데이트해죠") |
| `README.md` | 형제 저장소 섹션 + 기기 문서 링크. `ZeroWearable` 이 빠져 있던 Project layout 표 보강 |
| `README-KR.md` | 동일 내용 미러링 (형제 저장소 섹션 + KR 기기 문서 링크 + layout 표) |

지식 문서에 트리거를 넣지 않았다 (Rule 3). 지식 ↔ 제품 문서 분리 근거는 v1.13.0 에 기재.

### 비-Arduino 대비

지식 §7 과 `Docs/wearable-device.md` §5 에 **새 기기 5칸 체크리스트**(식별 / 툴체인
진입점 / build·flash·monitor / 복구 / 실패 패턴)를 남겨 계열 중립으로 확장 가능하게 했다.

## 평가

- **워크플로우 개선도: A-** — 매번 형제 저장소 문서를 재탐색하던 비용을 제거하고,
  "호스트인가 펌웨어인가" 분리 절차를 절차로 고정했다. 실기 플래시로 끝까지 검증하지는
  않아 A 는 아니다.
- **Claude 스킬 활용도: 2/5** — `/harness-creator` Mode B 절차만 사용.
- **하네스 성숙도: L3 → L3+** — agents 9명 / knowledge 9개 서브디렉토리 / engine 10개.
  기기 레인이 처음 생겼다.

### 구조 검증 (Mode E)

- config ↔ 파일 정합: **누락 0, 고아 0**
- `device-smith` 트리거 14개, **충돌 0**
- 같은 레이어 내 트리거 중복: **0**. 검출된 13건은 전부 agent↔engine 쌍
  (`tamer`↔`mission-dispatch`, `security-guard`↔`crash-dump-triage`,
  `build-doctor`↔`release-build-pipeline`) — Rule 3 이 허용하는 의도된 패턴
- 3-Layer 균형: 경고 없음

## 다음 단계 제안

1. 실기 검증 — `device-smith` 로 `claude_hud_amoled` 를 한 번 빌드·플래시해 knowledge 의
   명령을 실측 확인 (현재는 형제 저장소 문서 + 환경 존재 확인까지만)
2. 플래시 직후 호스트 재기동이 **와치 재부팅과 같은 재접속 경로**를 타므로, 기기 재접속
   회귀 검증 절차로 엔진화할지 검토 — 이때는 `device-smith` + 재접속 확인이 2인 협업이
   되므로 Rule 2 에 따라 엔진이 필요해진다
3. 형제 저장소 문서가 갱신되면 knowledge 가 드리프트한다. 1차 출처를 명시해 두었으나
   동기화 주기는 정하지 않았다
