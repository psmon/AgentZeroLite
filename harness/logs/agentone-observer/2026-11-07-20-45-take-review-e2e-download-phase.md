---
date: 2026-11-07T20:45:00+09:00
agent: agentone-observer
type: improvement
mode: execution
trigger: "음악생성(다비드)→생성된 음악 검토→뮤비(2d/3d 검토)→유니티 녹화 플로우를 팁 없이 완주하도록 e2e 관찰-결과-개선 루프"
---

# agent-one — 생성 곡 검토(2단계) e2e: 다운로드 단계에서 페이즈 종료

## 실행 요약

operator 목표: Claude 하네스 흐름(생성 → 검토 → MV 플랜 → Unity 녹화)을 agent-one이 기존 스킬 지침만으로 완주.
1단계(deevid 생성, 12크레딧)는 앞 페이즈에서 성공. 이번 페이즈는 2단계 "생성된 곡들 검토해서 추천해줘" 반복.
operator 결정으로 **"만든 곡을 다운로드까지 성공하나"까지 보고 종료**. 영상 단계는 진행하지 않음(추후 재개).
워크트리 `C:\code\psmon\AgentZeroLite-browser`, 브랜치 `feat/agent-one-workspace-grounding`(미커밋). 테스트 832/832.

## 결과

### 관찰된 실패와 대응 (시간순)

| 관찰 | 원인 | 수정 |
|---|---|---|
| 재개 세션에서 깨진 한국어 반복("좀석각 체을…") | 같은 요청의 실패 5회분이 쌓인 컨텍스트 | `/new`로 재시험(코드 수정 없음) |
| 새 세션 redo 플랜에 선례 없음 | `_workFolder`가 쓰기에서만 정해짐 | `WorkFolderLookedAt` — 턴이 가장 많이 본 폴더 |
| 한 단계 34분 생각 | gemma ~15 tok/s, 60k자 한도 미달 | `ThinkingLimitTime` 12분(강모델 40분) |
| Jev `unbacked_review` 0.97 무시 | `Get-ChildItem` 성공을 "실행함"으로 간주 | `ListsOnly` — 나열 명령은 검토 근거 아님 |
| ffprobe 0개 후 "업로드해 주세요" | 쓰기+실행 턴은 되넘김 검사 면제 | 도구 쓴 턴은 모두 검사 |
| 선례에 notes.md 이름만, 안 열어봄 | 이름만으로는 절차가 안 보임 | `how:` 줄 — 선례 노트/로그의 스크립트 실행 줄 |
| 자기 selection.md 재낭독, "backed" 0.31 | 낮은 확신 backed 통과 | `UnsureBackedYields` |
| dv-fetch `--match` 값 추측 4회 | 값을 찾지 않고 시도 | `GuessNote`(빈 결과 후 다른 값 2번째) |
| 없는 `out/C2_v2.mp3` 전사 요청 | 자기 순위로 파일명 지어냄 | `MissingMediaInputs` — 사람에게 묻기 전 거절 |
| ```json 펜스 속 깨진 호출이 답변으로 | 펜스가 envelope 판별을 피함 | `LooksLikeEnvelope` 펜스 처리 |

operator 승인으로 agent-one이 측정 없이 만든 파일 16개 삭제(두 번).

### 페이즈 결론 — 다운로드: **실패**

- 스스로 해낸 것: 선례 `how:` 줄로 `dv-fetch.cjs`를 찾음 → 경로 오류를 `find_files`로 교정 → CDP로 deevid 기록 8건 수신.
- 못 한 것: `--match` 값. "Apocalypse Day" / "Apocalypse" / "apocalypse-day"는 0건.
- 정답: deevid 제목은 프롬프트 첫머리("A cinematic, epic orchestral-pop fusion with a…") —
  `harness/knowledge/tools/input-types.md` 270줄에 이미 기록("`--match`는 제목 대신 프롬프트 낱말로 걸린다").
  관찰자(나)도 UI의 "그 날의 기록"을 제목으로 오판해 operator 힌트 문구가 부정확했다.
- 같은 시각 정식 Claude 세션이 6테이크(`take-170890743` 외)를 받아 `out/`에 둠 — agent-one의 산출 아님.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 워크플로우 개선도 | B | 검토 거짓 보고는 매번 잡히고, redo 플랜이 다운로드→측정 절차를 처음으로 담음. 단 완주 0 |
| Claude 스킬 활용도 | 3 | 하네스 지식(input-types.md)에 답이 있었으나 agent-one·관찰자 모두 찾지 못함 |
| 하네스 성숙도 | L3 | 관찰 표·측정 근거는 남았으나, 실패 턴 산출물이 다음 턴의 근거가 되는 오염 문제는 미해결 |

## 다음 단계 제안

1. **값 찾기를 지식 검색으로**: `GuessNote` 이후에도 못 찾으면, 스크립트 이름(`dv-fetch`)으로 `harness/knowledge`를 grep한 줄을 자료로 붙이기.
2. **실패 턴 산출물 격리**: `unverified` 턴이 쓴 파일과 그 턴에서 배운 graph 사실을 다음 턴 자료에서 빼거나 표시.
3. **느린 서버 대응**: 한 턴 20~60분 — 보통 모델 반복 루프(재시도 2회 × 수 분) 비용 재검토.
4. 재개 시: `out/`에 테이크가 있는 상태에서 "측정 → 추천 → operator 승인"부터.
