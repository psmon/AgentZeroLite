---
date: 2026-11-03T14:09:00+09:00
agent: tamer
type: evaluation
mode: log-eval
command: "/harness-creator ms store 공식 출시버전 다음 개선점 — 내부 로그 + 외부 유사기능 조사, 아이디어 5개 발굴"
---

# MS Store판(Avalonia) 다음 개선 아이디어 5개 발굴

전제: Store판 = `Project/AgentZeroAvalonia` MSIX, 최신 제출 17 (1.26.7.0, 2026-10-30).
채택된 아이디어는 같은 세션에서 워크트리로 나눠 진행할 예정이다. 그래서 서로 파일이 겹치지 않는 단위를 골랐다.

## 실행 요약

- 내부: Explore 에이전트 1개가 `Docs/avalonia-v2/microsoft-store.md`, `README-Avalonia.md`의 ⏳ 행, M0041/M0042, 2026-09 이후 `harness/logs/**`의 "다음 단계 제안", orca/herdr 채택표를 수집했다. 후보 16개.
- 제외(이미 구현): 알림(팝업·창 깜빡임, `AttentionNotifier`), Budget/비용, DIFF 리뷰 + 노트 터미널 전송(`DiffReviewViewModel`), AI 변경 Revert, 팔레트, Claude/Codex resume 질의, `terminal-wait`/`cost` CLI. M0042(d90a7cb~ef1f9d1).
- 외부: Warp(유니버설 에이전트 지원, 알림 센터, 리뷰 코멘트를 에이전트로), Orca(태스크별 worktree, diff 코멘트, 모바일), cmux(OSC 9/99/777 알림 링, 사이드바에 브랜치·PR·포트·최근 알림), Superset(worktree별 setup/teardown 스크립트, diff 스테이징), Wave(xterm v6, 위젯, 터미널 내 AI).
- 코드 대조(grep으로 확인): WebView2 런타임 확인 0건, xterm search addon 0건, OSC 알림 핸들러 0건(vendor 제외), 리스닝 포트 탐지 0건. `GitWorktreeBuilder`(worktree add)는 WPF `CliHandler`만 사용하고, Avalonia `worktree` 동사는 list/open/close뿐이다.

## 결과 — 제안 5개

| # | 아이디어 | 내부 근거 | 외부 근거 | 규모 | 주 파일 영역 |
|---|---------|----------|----------|-----|-------------|
| 1 | WebView2 런타임 확인 + 설치 안내 (터미널이 왜 안 뜨는지 알려 줌) | store.md §5 "후속: 시작 시 런타임 확인" / `XtermWebViewTerminalControl.axaml.cs:108` 로그만 | — (Store 인증/리뷰 리스크) | S | 시작 경로, 터미널 컨트롤 |
| 2 | 터미널 내 검색 (Ctrl+Shift+F, xterm addon-search) | 10-28 sweep "두 호스트 모두 없다" | Warp/Wave/cmux 기본 기능 | S | `Wasm/xterm` (js) + 키 바인딩 |
| 3 | OSC 9/777 터미널 알림 수신 → 기존 Attention 경로로 (에이전트 무관 알림) | AttentionNotifier는 화면/상태파일 감지만, Claude·Codex·Hermes 외 에이전트는 감지 불가 | cmux 알림 링(OSC 9/99/777), Warp "any CLI agent" 알림 | S~M | term.js 파서 훅 → ZeroCommon AgentActivityTracker |
| 4 | 태스크 → 새 worktree 생성 + setup 스크립트 + 에이전트 탭 자동 시작 | Avalonia `worktree` 동사에 create 없음, `GitWorktreeBuilder`는 WPF만 사용 | Orca/Superset 핵심 흐름, `.superset/config.json` setup/teardown | M | Worktrees VM, ZeroCommon/Module |
| 5 | AgentBot 답변 음성 출력 + barge-in (포터블 재생 큐) | README-Avalonia:146 ⏳, voice-curator 10-30 "Portable IAudioPlaybackQueue … barge-in", `AgentBotViewModel.Voice.cs:23` | Wave/Warp 쪽은 없음 → 차별점 | M | ZeroCommon Audio/Voice, AgentBot Voice partial |

### 차점 (이번 5개에서 제외)

- FileToolCore reparse point 해제 + AutoApprove/봇→터미널 감사 로그 (S, 보안 위생): 다음 security-guard 점검의 1순위.
- 라이트/시스템 테마 (M, xterm·TextMate 색 연동).
- 자동화 스케줄러 Avalonia 이식 (M), 오케스트레이션 (L).
- arm64 msixbundle + msstore CI 제출 (M, Store 전용).
- 사이드바 리스닝 포트 표시 (cmux): 3번 다음 단계로 묶을 수 있다.
- 웨어러블/OS control은 Store 인증 사유가 따로 필요해 보류.

### 정리할 것 (아이디어 아님)

- store.md §5 "읽기 전용 설치 폴더" 항목이 낡았다. `PtyHostFactory.cs:27`에서 이미 해결됐으니 문서만 고친다.
- M0042 status `in_progress`(이미 1.26.5.0 출시), M0041 Acceptance 체크박스가 비어 있다.

## 평가

| 축 | 등급 | 근거 |
|----|-----|------|
| 워크플로우 개선도 | B | sweep(10-28) 로그를 출발점으로 써서 M0042로 이미 된 것을 빼고 중복 제안을 막았다. 외부 조사는 서드파티 출처라 버전/날짜 정확도가 낮다. |
| Claude 스킬 활용도 | 3 | Explore 위임 + 웹 조사를 썼다. 기존 orca/herdr 문서셋은 재사용했지만 cmux/Superset 문서셋은 정원에 없다. |
| 하네스 성숙도 | L4 | 로그의 "다음 단계 제안"이 다음 기획의 근거로 실제로 쓰였다(피드백 루프 작동). 채택 전 상태 정리(M0042)가 밀려 있다. |

## 다음 단계 제안

- 채택된 아이디어마다 미션(M0043~) 파일을 만들고 `EnterWorktree`로 하나씩 분리 진행한다. 1·2·3은 S급이고 영역이 겹치지 않아 병행할 수 있다. 다만 2와 3은 모두 `term.js`를 건드리니 순차로 머지한다.
- cmux/Superset을 `Docs/agent-cmux/` 문서셋으로 남길지는 사용자가 판단한다.
