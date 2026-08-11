# herdr 영입 분석 — AgentZero Lite

> 스냅샷: 2026-08-11 · 대상: [herdrdev/herdr](https://github.com/herdrdev/herdr) (Rust, 0.8.0)
> 참조 클론: `E:\git-other\herdr` (읽기 전용, 지속 분석용)

## herdr 한 줄 요약

**Rust TUI 터미널 멀티플렉서 서버** — AI 코딩 에이전트(Claude Code/Codex/Cursor 등 ~30종)를
백그라운드 서버에 호스팅하고, **에이전트 상태(working/blocked/idle/done)를 1급으로** 다룬다.
tmux 유사 detached 서버 + thin 클라이언트 + SSH 리모트. LLM 코디네이터/보이스/웹뷰 없음(린함).

## AgentZero 대비

- **이미 동등/우위**: 멀티탭 ConPTY, 오케스트레이션, 자동화/스케줄, worktree CLI, diff 리뷰,
  커맨드 팔레트, 비용 추정, trust preset, 보이스/뮤직.
- **herdr가 강한 것(영입 대상)**: 에이전트 상태 감지·롤업, 세션 영속/복원, agent-to-agent
  대기 프리미티브, 훅 주입 통합 모델.

## 영입 선택 (operator)

| # | 기능 | herdr 위치 | 상태 |
|---|------|-----------|------|
| **H1** 🥇 | 스크린 매니페스트 상태 감지 | `src/detect/manifest.rs`, `src/detect/manifests/*.toml` | 진행 |
| **H2** | 상태 롤업 + 미확인 done | `src/workspace/aggregate.rs`, `src/ui/sidebar/` | 선택 |
| **H3** | 네이티브 세션 복원 | `src/agent_resume.rs`, `docs/.../session-state.mdx` | 선택 |
| **H4** | 훅 파일 주입 인스톨러 + 권위 우선순위 | `src/integration/`, `src/cli/integration.rs` | 선택 |
| **H5** | wait --until + 에이전트 별칭 + stall 가드 | `src/cli/agent.rs`, `src/app/api/panes.rs` | 선택 |

## 핵심 통찰

AgentZero 에이전트 훅(W1)은 **훅 노출 CLI만** 잡는다. herdr **H1(화면 스크래핑 기반 감지)**는
그 공백을 메워 **모든 CLI**의 상태를 잡는다 — AgentZero가 이미 유지하는 터미널 셀 버퍼 위에
TOML/JSON 규칙만 얹으면 됨. H1+H2 = "10개 에이전트 중 나를 기다리는 것" 킬러 UX.

## 매니페스트 규칙 형식 (herdr claude.toml 참고)

```
[[rules]] id, state(working|blocked|idle|unknown), priority,
  region(osc_title | bottom_non_empty_lines(N) | after_last_horizontal_rule | prompt_box_body | whole_recent),
  contains[], regex[]/line_regex[], any[{...}], not[{...}], skip_state_update
```
priority 내림차순 평가, 첫 매치 승. skip_state_update = 상태 유지(unknown). 미매치 → idle 폴백.

## 하네스 추적

- 엔진: `harness/engine/herdr-adoption.md`
- 로그: `harness/logs/herdr-adoption/`
- 3축 평가(코드 안전성/아키텍처 정합성/테스트 가능성).
