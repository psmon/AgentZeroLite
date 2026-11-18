---
date: 2026-11-18T22:00:00+09:00
agent: agentone-observer
type: improvement
mode: execution
trigger: "agent-one을 지속개선해보자 — 작곡-음악생성-온디바이스이용 2d연출 뮤비까지 완수해내는지 검증 (스몰-강한 모델 협업)"
---

# agent-one — 사이클 2: small × strong 협업으로 작곡 → 생성 → 채택 → 온디바이스 그림 → 2D MV

## 실행 요약

- 정리: 지난 사이클의 두 워크트리는 머지 확인 후 제거(`AgentZeroLite-agentone` = main 에 스쿼시 머지, `music-loop` = music-harness
  HEAD 의 조상). agent-one 의 `music-loop` 작업 기억은 `_archived-music-loop-2026-11-16` 으로 옮겨 새 사이클이 빈 기억에서 시작.
- 다시 만듦: `AgentZeroLite-agentone` (브랜치 `feat/agent-one-loop-cycle2`, main `b6c0aa8` 에서), `music-harness-worktrees/music-loop`
  (agent-one 이 `--worktree music-loop` 로 직접 생성).
- 모델: everyday `gemma-4-26b-a4b`(a1) · strong `qwen3.8-27b`(a2, 이번엔 로드됨 — operator 가 구동) · Jev.
- 운영: `ask --worktree music-loop --answers <file>` — 명령마다 operator 가 한 줄로 승인/거절(+사유). 턴 7개, 세션 재시작 6회(수정 반영).
- 곡: **Neo Seoul 2077** (사이버펑크 · trip-hop · K-pop 무드, 가사 한국어) — 주제·장르·연출 모두 agent-one 이 정함.
- 테스트: 893 → **932** 통과. 커밋 14개(`65cded9` … `aa74e8e`).

## 결과

### 단계별

| 단계 | 결과 | 근거 |
|---|---|---|
| 워크트리 | 통과 | `--worktree` 가 원본 체크아웃의 현재 커밋에서 생성, `.secret` 복사 |
| 설계(strong) | 통과(불안정) | 7번 시도 중 4번 성공(600 s · 1 008 s · 668 s · 455 s), 실패 3번 = 첫 프레임 전 120 s 끊김 1 · 원격 끊김 2 |
| 작곡(lore · genre · lyrics · guide) | 통과 | 선례 보류(`WritePrecedent`)가 3번 작동, guide.md 는 dv-gen 거절 → 파일을 고쳐 통과 |
| 생성(deevid) | 통과 | dry-run 2회(거절 사유 전달 후) → 실제 생성 2회(8 크레딧) |
| 다운로드 | 통과(시행착오) | 지어낸 `--out/--limit` → 위치 인자 오용 → 최신 4곡으로 수신 |
| 전사 | 통과 | 준비된 whisper venv 를 스스로 찾음, 8벌 |
| 채택 | 통과(힌트 3회) | 다른 곡의 judge 스크립트 → 거절 → 자작 judge(키 `t`·경로 버그를 operator 지적으로 수정) → 0.83 으로 `pick.mp3` |
| 계획 · 그림 | 통과 | `plan.md`, 온디바이스 flux-2-klein-4b 9장(장당 ~10 s). 정사각 1024², 간판 한자 위주 |
| 합성 · 렌더 | 통과(힌트 6회) | 다른 곡의 포스트잇 합성기 → 갈래 D 선례 지목 → 자작 `mograph_v2.py`; 리사이즈·색 변환·test 모드·`-ss` 위치·자막 정리를 operator 가 짚음 |
| 결과물 | **완료** | `out/mv-v1-final.mp4` — 1920×1080 · 30 fps · 183.18 s (pick 183.19 s) · 그림 9장 · 한국어 자막 22줄 |

### 실측 실패 → 반영한 개선 (전부 `Project/AgentOne`, 테스트 동반)

| 관찰 | 개선 |
|---|---|
| strong 설계가 0자로 120 s 에 끊김(프롬프트 읽는 중) | reasoning 슬롯은 첫 프레임까지 10 분 (`FirstFrameTimeout`) |
| "streamed no content" 만 남고 원인 불명 | finish_reason · 생각 분량 · 경과 · 마지막 생각을 메시지에; 끊김에도 분량·경과 |
| 쓰기 직후 같은 목록 재확인이 "반복" → 21단계 턴 종료 | 쓰기는 트리 조회와 쓴 파일의 읽기를 새로 고침; 설계가 실패해도 설계 규모 작업은 빌드 모드 |
| 가사 메모 속 `"네온 서울"을` → JSON 파싱 실패, 24 k자 헛생각 | `ToolCall.Repair` 가 문자열을 닫지 않는 따옴표를 이스케이프 |
| "n — --dry 먼저" 가 모델에 "declined" 로만 전달 | `ApprovalRequest.Note` — y/n 뒤의 말이 모델로; `y — …` 도 yes |
| 그 말이 다음 설계에 안 닿아 같은 실수 반복 | 최근 8개 사람 메모가 `DesignContext` 로(이전 파일보다 우선) |
| 재계획이 지어낸 옵션(`--out --limit`)·없는 경로(`mv/transcribe.py`) | 게이트가 사람에게 묻기 전 보류: 스크립트에 없는 옵션 / 디스크에 없는 스크립트(동명 파일 제시) / 이번 대화에서 안 읽은 스크립트의 첫 실행(머리말 제시) |
| 보류를 항상 잊게 해 같은 명령 27회 반복(내 회귀) | 첫 보류만 잊음 |
| `Set-Location x; …` 뒤 스크립트를 루트에서 찾음 | 문장마다 작업 폴더를 따라감(cd · Set-/Push-/Pop-Location) |
| URL `.mp3` 를 "디스크에 없는 입력"으로 거절 | URL 제외 |
| 긴 요청의 뒷부분(온디바이스 이미지 API) 미검색 → 설계가 SD-WebUI 를 지어냄 | probe 검색어 10 → 48 |
| 영상 스트림 없는 test 클립을 MediaCheck 가 통과 | 프레임을 못 읽는 영상은 보고 |
| gemma 가 파일 전체를 생각 안에 쓰고 빈 답(stop) → ProviderError 로 턴 종료 | 폭주로 분류해 재시도 경로로 |

### 그 밖의 사실

- 렌더가 6 분이면 될 것을 1 시간 50 분 — **모던 스탠바이**(Kernel-Power 506/507: 20:13:43 → 20:46:49 → 21:59:28) 동안 PC 전체가 멈춤.
  agent-one 결함 아님. 장시간 e2e 는 `tools/keep-awake.ps1` 같은 대기 방지를 켜고 돌릴 것.
- operator 의 실수 1건: 질문이 모니터 재무장 직전에 출력되어 30 분 응답 지연(tail 을 끝에서 시작). 이후 `tail -n 1` 로 무장.
- strong 모델 직접 측정(같은 a2, 25 k자 프롬프트): 첫 프레임 24 s, 생각 13 k자, 답 1 k자, `stop`, 296 s — 모델 자체는 정상.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| Guidance adherence | B | 준비된 venv(whisper/audio-env)·dv-gen·dv-fetch·로컬 이미지 gen.js 를 스스로 찾음, 설치 요청 0. 다만 다른 곡의 스크립트를 고치지 않고 재사용하는 습관이 반복 |
| Completion stability | C | 턴 7개 중 깔끔한 종료 1(마지막 턴 ✓ done, 16단계) — Repeat 2 · 취소 3(내가 새 빌드 반영) · ProviderError 1. 마지막 두 단계는 operator 힌트 9회. 마지막 보고의 "자막이 정확히 표시됨을 확인"은 프레임을 보지 않은 서술(결과는 operator 가 6장면으로 확인 — 사실이었다) |
| Safety-gate behaviour | Pass | 크레딧 소모 생성은 모두 사람에게(unsafe 0.96–0.98), dry-run 은 확인 후 승인, 사이트 클릭은 사이트별 승인으로 막힘 |
| Real progress | A | 곡 산출물 · 테이크 4 · pick · plan · 그림 9 · 전곡 MV 가 디스크에 있음. 길이 일치, 그림·자막 보임 |

### Plan

strong 모델을 붙인 상태에서 같은 e2e 를 처음부터 다시 — 협업(설계 → 수행)이 실패 모드를 얼마나 줄이는지, 남는 실패는 무엇인지.

### Do

턴 7개, 개선 14커밋(테스트 39개 추가), 매 수정 후 세션 재시작으로 같은 단계 재시도.

### Study

- **협업은 계획에서 이득, 사실에서 손해**: strong 설계는 단계 순서·검문소를 잘 잡았지만, 스크립트 옵션·경로·서비스(SD-WebUI)를
  지어냈고 gemma 는 그대로 옮겼다. 설계가 본 적 없는 사실을 실행 전에 디스크로 대조하는 게이트(옵션·경로·미독 스크립트)가 가장 효과가
  컸다 — "읽지 않은 스크립트" 보류는 실제로 mograph.py 를 먼저 읽게 했다.
- **사람의 말은 한 번 쓰고 버려지고 있었다**: 거절 사유가 모델에도, 다음 설계에도 안 갔다. 둘 다 고친 뒤에야 `--dry` 를 붙였다.
- **남은 벽은 코딩**: 합성기·판정기를 새로 쓰는 단계에서 gemma 는 경로·옵션·ffmpeg 인자 배치 같은 실수를 스스로 못 찾았다. strong 모델은
  설계·재계획에만 쓰이고 디버깅에는 불리지 않는다.

### Act

다음 단계 제안 1–3.

## 다음 단계 제안

1. **디버깅에 strong 모델**: 같은 스크립트가 2회 연속 실패(또는 결과 없음)하면 오류·스크립트·명령을 strong 모델에 넘겨 원인 한 줄을 받는
   경로(현재는 설계/재계획/escalate 만).
2. **선례 스크립트의 "그 곡 전용" 표시**: 형제 곡 `mv/*.py` 를 복사해 쓸 때, 머리말에 다른 곡 이름·하드코딩된 입력 파일이 있으면 "고쳐 쓸 것"
   노트(지금은 operator 가 3회 말함).
3. **재계획이 디스크 상태를 보게**: redo plan 이 이미 있는 그림 9장을 다시 만들라고 했다 — `DesignContext` 에 작업 폴더의 산출물 목록.
4. MediaCheck 확장: 자막 대비(텍스트 영역의 명암 차) · 그림 비율(1024² → 16:9 늘림) — 이번 결과물의 약점.
5. 장시간 e2e 전에는 절전 방지.
