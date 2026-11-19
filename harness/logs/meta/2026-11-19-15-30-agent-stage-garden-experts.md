---
date: 2026-11-19T15:30:00+09:00
agent: meta:agent-stage
type: improvement
mode: execution
trigger: "플러그인에 AgentStage 플러그인을 업그레이드 — 하위 task를 자식으로 시각화, 하네스 전문가가 채택되면 전문가로 시각화 (클로드 한정, 이 하네스만)"
---

# Agent Stage — 정원 하네스 전문가 시각화

## 실행 요약

Agent Stage는 이미 Claude의 Agent/Task 서브에이전트를 아바타 주위 크루(자식)로 그리고 있었다. 이번 작업은
그 크루 가운데 **이 정원 하네스(harness-kakashi, `"$schema": "kakashi-harness"`)의 전문가**를 가려내 전문가로
그리고, 서브에이전트 없이 **대화가 직접 전문가를 맡은 경우**도 보이게 하는 것이다. 지원 범위는 Claude 한정,
이 스키마 한정이다.

패턴은 이 PC의 실제 트랜스크립트(`~/.claude*/projects/*`)에서 측정했다.
- **서브에이전트 전문가**: music-harness의 리뷰 8건 모두 `general-purpose`이고, 프롬프트는 "You are acting as the
  **lyricist (작사가)** specialist … your definition `harness/agents/lyricist.md`", description은
  "lyricist review of Comma" 형태였다. 같은 저장소의 Explore 서베이는 agent 파일을 **여러 개** 언급했다. 그래서
  판정 규칙을 "agent 파일을 정확히 하나 지목 → 그 전문가(여러 개면 acting-as로 결정, 결정 못 하면 전문가 아님)
  → acting-as 이름 → description의 첫 단어" 순서로 정했다.
- **인라인 채택**: 메인 트랜스크립트에 `Read harness/agents/<name>.md`가 남는다(한 세션에 최대 16회). 정원지기는
  `<command-name>/harness-kakashi:harness-creator</command-name>` 사용자 줄과 Skill 호출로 남는다.

## 결과

- **ZeroCommon** `Agents/KakashiHarness.cs`: `HarnessLocator`(cwd에서 저장소 루트까지 위로 탐색, 15초 캐시,
  config 수정 시각이 바뀌면 다시 읽음), `KakashiHarness.Load`(스키마 확인 + agent frontmatter의 persona/type/description),
  `ExpertOf` / `ExpertOfPath`(다른 저장소의 agent 파일은 제외).
- `ClaudeSubagentReader`: 첫 user 줄을 프롬프트로 보관하고 `SubagentView.Expert`를 채운다(locator가 있을 때만).
- `Agents/ClaudeHarnessDuty.cs`: `ClaudeHarnessDutyReader` — 메인 트랜스크립트를 처음엔 마지막 512 KB부터,
  이후엔 추가된 부분만 읽는다. 한 턴 단위 duty를 기록한다(`end_turn` 또는 사람의 다음 프롬프트에서 끝남, isMeta와
  tool_result는 턴 경계로 보지 않음, agent 파일 Edit/Write는 정원 손질로 보고 duty를 해제).
- `WorkspaceSessionView.Harness` / `SubagentView.Expert` → `sessions.list`의 `harness`·`expert` 필드(null이면 생략되므로
  기존 플러그인과 호환된다). Avalonia `MainWindowViewModel.ActiveSessions`가 locator 하나를 두 리더에 공유한다.
- **플러그인 0.4.0**: 전문가 서브에이전트는 금색 테두리 헥사곤 + 덩굴색 테더 + 페르소나 라벨로 그린다. 인라인 duty는
  아바타 옆에 금색 끈으로 단 헥사곤 엠블럼이고, 턴이 끝나면 혜성으로 돌아간다. 아바타에는 ❀ 정원 마크,
  존 헤더에는 정원 이름, 상단에는 "experts on duty" 카운터를 표시하고, 툴팁에 정원·전문가·via를 보여 준다.
  `?demo`에서는 두 정원으로 시연한다.
- 실측 프로브(실제 세션): music-harness 리뷰 8건은 전부 정확한 페르소나로 식별했고, Explore 2건은 전문가 아님으로
  판정했다. 인라인으로는 tamer(command)와 6명(read)을 잡았고, 이 세션에서는 tamer(command)를 잡았다.
- 테스트: `KakashiHarnessTests` 15건, 브리지 1건 추가. ZeroCommon.Tests 전체 1499 통과, Avalonia 빌드 오류 0.
  데모는 headless Chromium 스크린샷으로 확인했다(콘솔 오류 0).
- 문서: README/README-KR, README-Avalonia/-KR, Project/Plugins/README.md, CLAUDE.md.

### 디버그 실측 후 보정 (15:50)

Debug 호스트에서 이 저장소 Claude 탭으로 "/harness-creator" → "에이전트제로 빌드가 어떻게 되는지 분석해"를 실행했는데
전문가가 표시되지 않았다. 트랜스크립트(42f4fdfb…)를 보니 그 턴은 build-doctor로 일하고 `harness/logs/build-doctor/`에
로그까지 남겼지만, 정의 파일을 **Read 도구가 아니라 Bash `cat harness/agents/build-doctor.md`** 로 읽었다. 판정을 넓혔다.
- Bash/PowerShell 명령이 agent 파일을 정확히 하나 지목하면 read(여러 개·`ls`는 서베이로 제외, 리다이렉트·`sed -i`·
  `Set-Content` 같은 쓰기는 손질로 보고 해제), Grep 도구의 path도 read.
- `harness/logs/<전문가>/`에 쓰면 `via: log` — 정의가 이미 대화에 있어 다시 읽지 않는 턴도 잡힌다.
- 같은 세션을 프로브로 다시 읽으면 tamer(command 15:39:00–15:39:12), build-doctor(read 15:40:39–15:41:54).
  테스트 2건 추가(KakashiHarnessTests 17건).

### 재테스트 — 표시 없음이 맞는 경우 (16:50)

"AgentZero가 기능구성이 어떻게 되었나 설명해죠"는 README만 읽고 답한 턴이었다. agent 파일 읽기, 전문가 로그, 정원지기 호출이
모두 없었고, 이 질문을 맡는 전문가도 정원에 없다. 표시하지 않는 것이 맞다. operator 확인: 전문가가 필요 없는 일을 Claude가
직접 처리하는 것은 토큰을 아끼는 정상 동작이다. Agent Stage는 전문가를 **채택한 흔적이 있을 때만** 그린다(추정해서 붙이지 않는다).

### 표시 상한 — 아바타당 전문가 5개 (17:10)

operator 요청: 전문가가 10명 넘는 정원에서 무제한으로 쌓이면 혼잡하다. 끝난 전문가는 원래도 호스트가 2분 뒤에 빼지만,
그 2분 안이나 리뷰처럼 한꺼번에 뜰 때는 쌓인다. 플러그인 `capExperts`가 아바타 하나의 전문가(전문가 서브에이전트 + 인라인
duty)를 **최대 5개**만 그린다. 넘치면 끝난 것부터, 그중 오래전에 끝난 것부터 빼고, 그다음은 가장 오래 일한 working 순으로
뺀다. 빠진 수는 아바타 좌상단 "+N" 칩으로, 이름은 아바타 툴팁에 보인다. 상단 "experts on duty" 카운터는 빠진 것까지 센다.
Explore·Plan 같은 비전문가 서브에이전트는 상한과 무관하다. 선택 로직은 노드 단독 검증(8개 → working 4 + 최근 done 1,
오래된 done 3개 숨김), 데모 60초 스크린샷에서 "+1" 칩 확인(콘솔 오류 0).

## 평가

| 축 | 등급 | 근거 |
|----|------|------|
| 워크플로우 개선도 | A | 정원지기가 전문가를 심고 부르는 흐름이 터미널 밖에서도 보인다. 병렬 리뷰 8명이 점 8개가 아니라 페르소나 8명으로 보인다. |
| Claude 스킬 활용도 | 4 | harness-creator(정원지기) 호출을 직접 감지하고, 리뷰 엔진이 띄우는 전문가 서브에이전트 관례를 판정 규칙으로 고정했다. |
| 하네스 성숙도 | L4 | 정원의 agents/ frontmatter(persona/type)가 외부 도구(Agent Stage)의 표현 데이터가 되었다. 다만 frontmatter에 persona가 없는 전문가는 이름으로 대체된다. |

## 다음 단계 제안

- WPF 호스트는 여전히 `sessions.list`에서 `supported:false`이다. WPF에 SESSIONS 게시가 붙으면 같은 필드가 그대로 동작한다.
- 실제 앱에서 Claude 탭 하나로 `/harness-creator 전체 점검해`를 실행해 크루와 엠블럼이 실시간으로 뜨는지 육안으로 확인해야 한다(아직 하지 않았다).
- 각 전문가 frontmatter에 아이콘(`icon:`)을 두면 헥사곤 글리프를 그것으로 바꿀 수 있다. 하네스 스키마 쪽 제안이다.
