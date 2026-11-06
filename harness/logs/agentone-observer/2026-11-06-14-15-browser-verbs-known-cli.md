---
date: 2026-11-06T14:15:00+09:00
agent: agentone-observer
type: improvement
mode: execution
trigger: "agent-one 툴 활용 개선 — 알려진 CLI(플레이라이트) 펑션콜/내부 툴화, 워크트리 작업, 로그 파악"
---

# agent-one — 알려준 툴을 못 쓰던 문제: 브라우저 펑션콜 + 알려진 CLI 내장

## 실행 요약

operator 요청: music-harness AO 탭에서 "플레이라이트 cli를 cdp 모드로 쓰라"고 알려줘도 agent-one이 활용하지 못함.
로그 파악 → 알려진 툴(특히 Playwright)을 펑션콜/내부 툴로 관리해 학습 없이 쓰게 개선 → 워크트리
(`feat/agent-one-browser-tools`, `C:\code\psmon\AgentZeroLite-browser`)에서 작업 → AO 탭을 워크트리 빌드로 교체해 e2e.

> 관찰자 역할 경계 메모: 소스 수정·AO 구동은 operator가 명시 요청했다(이전 skill-scope 활동과 같은 방식).

## 결과

### 1. 원인 (세션 `music-harness/20261105-165229`, Debug 빌드)

| turn | 요청 | 행동 |
|---|---|---|
| 7 | "플레이라이트 구동해볼래? 내가 채널 열고 알려주께" | 도구 0회. "CLI 에이전트라 브라우저 화면을 볼 수 없다" + 사용자에게 URL을 달라고 반환. `learned (constraint)`로 이 오답을 **지식으로 저장** |
| 8 | "플레이라이트 cli 툴 알아봐, cdp 모드로 가능할꺼야" | `web_search` 1회 → `unwritten` 넛지 → `playwright-cdp-test.cjs`(`require('playwright')`) 작성 → "크롬을 디버그 모드로 띄우고 알려달라"고 반환. 에스컬레이션 20분 후 스트림 무응답 |

실측: 그 시각 `playwright-cli 0.1.22` 전역 설치, 9222에 Chrome CDP LISTEN. 카탈로그에 브라우저가 없고 프롬프트에 설치된 CLI 정보가 없어
모델이 툴을 "웹검색으로 학습"하려다 실패하고 일을 사람에게 넘김.

### 2. 소스 개선 (워크트리, 테스트 747/747)

| 변경 | 내용 |
|---|---|
| `Tools/Browser/` (신규) | `browser_open/snapshot/read/find/tabs/click/fill/press` — `@playwright/cli` 구동. CDP Chrome이 응답하면 attach + **새 탭**, 아니면 자체 브라우저. 종료 시 attach는 **detach만**(close 금지). 첫 행동은 사이트별 1회 사람 승인. Windows는 npm shim의 `.js`를 node로 직접 실행(인자 리스트, cmd 인용 회피). 산출 파일은 워크스페이스 밖 |
| `Tools/Cli/` (신규) | `KnownCli` 12종(playwright-cli, gh, git, ffmpeg, yt-dlp, node, python, dotnet, docker, rg, curl, jq) PATH/PATHEXT 감지 + 사용 카드. 설치된 것만 시스템 프롬프트에 노출, `cli_guide`(route-exempt). 미설치는 설치 안내만, 자동 설치 금지 |
| `ToolCatalog.RepeatRule` | 관찰(`snapshot/read/find/tabs/process_status`)은 연속일 때만 반복, 행동(click/fill/press)은 반복 아님 |
| `SmartRouter` / `SystemPrompt` | web·workspace 라우트에 browser 허용, "브라우저를 쓸 수 있다 · 사람에게 넘기지 말 것 · 스크립트 대신 browser_*" 규칙 |
| `ChatSession` | 브라우저 게이트(`BrowserGateAsync`), 클레임 체크에서 브라우저 행동을 실행 근거로 인정 |
| `AgentConfig.browserCdpUrl` | 기본 `http://127.0.0.1:9222`, `off` |
| 잘림 안내 | e2e에서 실측: 채널 트리 16K 컷 아래 항목 누락 → 컷 문구가 미표시 사실과 `browser_read`/`browser_find`를 안내 |

라이브(`AGENT_ONE_BROWSER_LIVE=1`): 실제 Chrome attach → example.com 새 탭 → 스냅샷/본문 → 그 탭만 닫고 detach, 5 s. 사용자 탭 무변경 확인.

### 3. e2e (AO 탭, 워크트리 Debug, gemma-4-26b × qwen3.8-27b, smart)

요청 "플레이라이트로 샌디의 지식탐험 유튜브 채널 열어서 역사 주제 영상들 제목이랑 등록일 확인해줄래?"
→ route web 0.80 → `web_search` → `browser_open`(YouTube 검색) → `browser_open`(채널 /videos) → 답(7건, 제목·상대 등록일이 스냅샷과 일치)
→ claims `backed 0.83` → escalation(qwen, 5분) → Final. 총 약 9분, 스크립트 작성·사람에게 반환 0회.
누락 1건(「로마… 역사 여행 #1」, 6일 전 — 컷 아래) → 위 잘림 안내 수정의 근거.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| Guidance adherence | A | 같은 의도의 요청에 설치·학습 요구 없이 내장 browser_* 를 바로 사용. 스크립트 작성·`npm install`·사용자 반환 0회 |
| Completion stability | B | 1/1 턴 Final 정상 종료, 가드 오탐 없음. 에스컬레이션이 턴의 절반 이상(5분)을 차지 |
| Safety-gate behaviour | Pass | 읽기만 수행(행동 게이트 미발동), attach 세션은 detach로 정리, 사용자 탭 무변경 |
| Real progress | B | 요청한 목록을 페이지 근거로 제시. 컷 아래 1건 누락(수정 반영, 재관찰 필요) |

## 다음 단계 제안

- 머지 → agent-one 릴리스 후 같은 요청 재관찰: 잘림 안내 뒤 `browser_read`/`browser_find`로 누락 없이 채우는지.
- 행동 경로(e2e 미검증): deevid 생성처럼 클릭이 필요한 작업에서 사이트별 승인 1회 → 반복 클릭이 매끄러운지.
- 에스컬레이션이 "이미 근거가 충분한 조회 답"에도 0.98로 발동 — 조회형 턴(브라우저/웹 근거 + 짧은 표)의 에스컬레이션 기준 검토 후보.

### Plan
알려준 툴을 못 쓰는 원인을 로그로 확정하고, 알려진 툴을 내장(펑션콜 + 카드)해 같은 요청을 학습 없이 완주시킨다.

### Do
로그 판독 → playwright-cli 실측(attach/tab-new/snapshot/find/eval/에러 형태) → 브라우저 패밀리·KnownCli·RepeatRule 구현(테스트) → 라이브 테스트 → AO e2e → 드러난 잘림 누락 수정(테스트).

### Study
툴 활용: met (내장 browser_* 즉시 사용). 정확도: partial (컷 아래 1건 누락 → 수정). 안정성: met (1/1 Final).

### Act
1. 머지·릴리스 후 재관찰(누락 해소 여부, 행동 경로).
2. 조회형 턴 에스컬레이션 기준을 operator에게 제시.
