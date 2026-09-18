# Long-lived & experimental branches — do NOT auto-flag as "stale"

> `pr-review` / `tamer`가 브랜치 지형을 평가할 때 **먼저 이 표를 확인**한다.
> 여기 등재된 브랜치는 "main 대비 N커밋 뒤처짐"만으로 rebase/폐기 권고를 내면 안 된다.
> 의도된 상태이므로, 리뷰 리포트에서는 `intentional`로 분류하고 조치 권고를 생략한다.

| 브랜치 | 분류 | 병합 정책 | 근거 |
|--------|------|-----------|------|
| `feat/avalonia-v2` | **experimental — 장기** | ZeroCommon 심(M0033)은 main에 PR로 먼저 합류; `Project/AgentZeroAvalonia` 호스트는 M0040 수용 후 합류. 어느 쪽도 `Project/AgentZeroWpf/`를 바꾸지 않는다(회귀 기준: `git diff --stat main -- Project/AgentZeroWpf` 비어 있음). | WPF 앱을 그대로 둔 채 Avalonia 호스트를 옆에 추가하는 크로스플랫폼(Windows + macOS) 전환. 계획: `Docs/avalonia-v2/DESIGN.md`, 미션 M0033~M0040. 첫 시도 `feat/avalonia-crossplatform`(삭제됨, tip `806cf94`는 객체로 남아 `git show`로 참조 가능)는 네이티브 터미널 컨트롤 때문에 화면 PTY와 에이전트 PTY가 갈려 막혔고, v2는 xterm.js + 단일 `ITerminalSession`으로 그 문제를 없앤다. 워크트리: `C:\code\psmon\AgentZeroLite-avalonia-v2`. |

## 갱신 규칙

- 새 실험/장기 브랜치가 생기면 여기 등재한다.
- 실험이 종료(main 합류 또는 폐기)되면 해당 행을 제거한다.
- **자동 정리 금지** — 브랜치 삭제/rebase는 항상 operator 승인 후에만.

_등재: 2026-08-21 (PR #12 리뷰 세션). 갱신: 2026-09-18 — `feat/avalonia-crossplatform` 폐기(운영자 승인), `feat/avalonia-v2` 등재; 정리 후보였던 `fix/voice-note-loopback-crash`는 삭제 완료._
