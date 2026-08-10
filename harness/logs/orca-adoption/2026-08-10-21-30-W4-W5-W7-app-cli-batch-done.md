---
date: 2026-08-10T21:30:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W4 App-CLI / W5 스킬스텁 / W7 worktree"
engine: orca-adoption
phase: W4,W5,W7
---

# W4·W5·W7 — App-CLI 확장 / 안티드리프트 스킬 스텁 / worktree 워크스페이스

## 실행 요약

에이전트가 앱을 스크립팅하는 표면(App-CLI)을 확장하고, 드리프트 없는 스킬 스텁을
주입하며, git worktree를 관리·신뢰 등록. 세 미션이 `-cli` + `GitWorktreeBuilder`를
공유하므로 함께 처리.

## 결과 (편집 지점)

### W4 — App-CLI 확장
- `Project/ZeroCommon/Module/GitWorktreeBuilder.cs` **(신규)** — porcelain 파서(순수) + add/list/
  remove exec. 6 테스트.
- `Project/ZeroCommon/Agents/AgentSkillGuides.cs` **(신규)** — `-cli help <topic>`가 서빙하는
  버전-매칭 가이드(agentzero/orchestrate).
- `Project/AgentZeroWpf/CliHandler.cs` — `worktree`, `terminal-wait`(TUI-idle 폴링),
  `help <topic>`(가이드 서빙), usage.

### W5 — 안티드리프트 스킬 스텁
- `AgentSkillGuides.BuildStub()` — 스텁은 커맨드 미포함, `-cli help agentzero`만 지시(드리프트 방지).
  marker로 식별.
- `Project/AgentZeroWpf/Services/SkillStubInjector.cs` **(신규)** — `~/.claude*/skills/agentzero-control/
  SKILL.md`에 스텁 주입, marker 기반 안전 제거(외부 스킬 보존).
- `-cli skill-stub-install` / `-uninstall`(명시적·동의).

### W7 — worktree 1급 워크스페이스 (코어 + trust 연동)
- `GitWorktreeBuilder`(W4 공유)로 worktree 관리.
- `-cli worktree add --trust` → 새 worktree에 `TrustPresetWriter.MarkAllTrusted`(W2 연동) 적용.

## 검증

- 헤드리스: GitWorktree 6 + SkillGuides 5 = **11 통과**. 전체 회귀 **403 통과 / 0 실패**.
- `dotnet build AgentZeroWpf` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 스킬/신뢰 파일 수정은 명시적 CLI(동의). marker 기반 안전 제거. worktree exec는 git 위임. |
| 아키텍처 정합성 | Pass | 파서·가이드·스텁 콘텐츠 ZeroCommon(헤드리스). IO만 WPF. |
| 테스트 가능성 | A− | 파서·가이드·스텁 헤드리스. terminal-wait/주입 IO는 런타임(폴백 안전). |
| 이식 충실도 | Pass | orca app-CLI + 스킬 스텁 안티드리프트 패턴 이식. |

## follow-up

- **W7 WorkspaceActor 확장** — worktree를 status badge/hosted tabs 가진 1급 UI 워크스페이스로
  자동 등록(현재는 폴더로 열기 + CLI 관리). WPF 통합.
- terminal-wait는 CLI측 폴링 — GUI TUI-idle 신호(W1 훅)와 통합 여지.
