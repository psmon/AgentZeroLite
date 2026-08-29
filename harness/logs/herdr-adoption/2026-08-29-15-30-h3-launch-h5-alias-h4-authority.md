---
date: 2026-08-29T15:30:00+09:00
agent: code-coach
type: improvement
mode: log-eval
engine: herdr-adoption
phase: H-runtime
trigger: "B2 진행 (worktree feat/b2-adoption)"
follows: harness/logs/herdr-adoption/2026-08-12-00-30-runtime-and-improvements.md
---

# herdr 영입 — H3 자동 재기동 · H5 별칭 · H4 authority 반영

## 실행 요약

브리핑 B2 채택. 워크트리 `feat/b2-adoption` 에서 herdr H-lane 의 잔여 3건을 구현.

## 결과

### H5 — 안정적 에이전트 별칭 (alias→tab)
- 순수 코어 `Agent.Common.Agents.TerminalAliasRegistry` (ZeroCommon) — alias →
  **stable (GroupName, Title)** 매핑(휘발성 인덱스가 아니라). JSON 영속
  (`terminal-aliases.json`), case-insensitive, prune, 검증. **14 headless 테스트**.
- CLI: `terminal-alias <list|set <g> <t> <name>|rm <name>>` + `terminal-send/key/read`
  에 `--alias <name>` 지원 (공유 `TryParseTarget`).
- GUI: `HandleCliCommand` 이 `alias` 를 live group 목록의 (DisplayName,Title) 로
  해석 → 인덱스. 미정의/미live alias 는 명확한 에러.

### H3 — 자동 세션 재기동 (실 주입)
- 기존 `agent-resume`(커맨드 출력만) 위에 **`agent-resume-launch <g> <t>` /
  `--alias`** 추가 — cwd→`ClaudeSessionLocator.BuildResumeCommand` 로 resume
  커맨드를 만들어 **live 터미널에 WriteAndSubmit 로 실제 주입**. PTY dead / 세션
  없음 / 미발견은 각각 에러 처리.

### H4 — authority 를 live 모니터에 반영
- `AgentStateMonitor.Tick` 이 이제 `AgentIntegrationCatalog.UseScreenDetection(
  tab.Title, HookInstalled(tab.Title))` 로 게이트. lifecycle-authority CLI 의 훅이
  설치된 경우 화면 감지를 억제하고 훅-보고 상태를 유지(else 분기). 현재 shipping
  에이전트는 전부 screen-detection 으로 resolve(정상 기본값) — lifecycle-authority
  CLI 용 훅 인스톨러가 생기면 즉시 동작하는 forward-compatible plumbing.
- **copilot 훅은 미구현** — `~/.copilot/config.json` 은 `trustedFolders[]` trust
  설정이지 lifecycle 훅 스키마가 아니며, 외부 herdr ref(`E:\git-other\herdr`)가
  이 머신에 부재해 포맷을 확증할 수 없다. 추측 구현 금지 원칙에 따라 제외.
  (codex/cursor 인스톨러는 이전 배치에서 이미 완료.)

## 검증

- 헤드리스: 전체 **538 통과 / 0 실패**(+14 TerminalAlias). WPF 빌드 **오류 0**.
- H3/H5 GUI 경로 + H4 모니터 게이트는 빌드 검증(런타임 스모크는 operator 데스크톱).

## herdr 영입 현황 (갱신)

| 항목 | 코어 | 런타임 |
|---|---|---|
| H3 세션 복원 | ✅ 발견+커맨드 | ✅ **자동 주입(agent-resume-launch)** |
| H4 권위 모델 | ✅ | ✅ **모니터 게이트 반영** (copilot 제외) |
| H5 별칭 | ✅ **registry+CLI/GUI** | ✅ |

## 평가 (3축)

| 축 | 결과 |
|----|------|
| 코드 안전성 | A — alias 는 stable identity 로 매핑, 미live 시 명확 에러. 주입은 PTY 상태 확인 후. |
| 아키텍처 정합성 | Pass — registry 는 ZeroCommon 순수(테스트), WPF 는 해석/주입만. authority 는 기존 catalog 재사용. |
| 테스트 가능성 | A — alias 코어 14 테스트. WPF 경로는 빌드 검증. |

## 다음 단계 제안
- H3 **auto-trigger** (Working→Idle 엣지에서 opt-in 자동 재기동) — 엣지 감지+de-dupe
  필요, 이번엔 수동 verb 까지만.
- lifecycle-authority CLI(opencode 등) 훅 인스톨러가 생기면 H4 게이트 실동작 검증.
- copilot 훅 포맷 확인되면 `AgentHookFileBuilder` 에 CopilotEvents 추가.
