# orca 영입 — 페이즈별 구현 계획 & 하네스 추적

> 대상: AgentZero Lite (`AgentZeroWpf → ZeroCommon` 단방향 의존, net10.0).
> 원칙: WPF/Win32 비의존 로직은 전부 `ZeroCommon`(헤드리스 테스트 가능). 액터 레이어는 WPF import 금지.
> 각 페이즈 착수/완료 시 `harness/logs/orca-adoption/`에 로그 + 3축 평가.

진행 표기: `[ ]` 미착수 · `[~]` 진행중 · `[x]` 완료

---

## Phase 0 — 기반 정비 (~1주)

목표: 지속 분석 인프라 + 후속 페이즈 전제 마련.

- [x] orca 참조본 `E:\git-other\orca` 확보 (shallow, 읽기 전용)
- [x] `Docs/agent-orca/` 스냅샷 문서화 (README / 01-catalog / 02-plan)
- [x] `harness/engine/orca-adoption.md` 엔진 등록 + `harness.config.json` 반영
- [x] **W8 완료** — `IAgentToolbelt` 파일 도구(`read_file/write_file/edit_file/grep`) 구현.
      `FileToolCore`(ZeroCommon, 샌드박스) + grammar lockstep + 양 loop + 호스트. 18 테스트 통과.

**산출물**: 문서 3종, 엔진 정의. **검증**: 문서 링크 정합, 의존성 규칙 위반 없음.

---

## Phase 1 — 상태 신뢰성 (2~3주, P0) 🥇   ✅ **W1 완료**

목표: 터미널 스크래핑 → **훅 기반 정확 상태**. 무프롬프트 에이전트 기동.

### 1a. 에이전트 훅 (orca 기능 E) — 완료
- [x] `agent-hook` 서브커맨드(fire-and-forget) + `agent-hook-install`/`-uninstall`(in-process).
- [x] `AgentHookMapper`(ZeroCommon, 순수) — Claude 훅 이벤트→`AgentLoopPhase`. 17 테스트.
- [x] `AgentHookSettingsMerger`(ZeroCommon) + `AgentHookInstaller`(WPF) — settings.json `hooks`
      서브트리, 백업+원자적 쓰기, 마커 기반 멱등/제거. **동의: 명시적 CLI 명령으로만 설치**.
- [x] `AgentBotActor.Receive<AgentHookEvent>` → `AgentLoopProgress`(봇=단일 UI 게이트웨이).
      `ApprovalParser` 스크래핑은 폴백 유지.
- [ ] (후속) Settings UI 토글, 훅 스크립트 session/detail 정교화.

### 1b. Trust preset (orca 기능 F) — 미착수 (W2, 이번 선택 밖)

### 1b. Trust preset (orca 기능 F)
- [ ] `TrustPresetWriter.cs` (`ZeroCommon/Agents/`) — 각 CLI 신뢰 파일 기록. orca
      `agent-trust-presets.ts`의 경로 명세 이식 (Codex TOML, Cursor `.workspace-trusted`, Copilot JSON).
- [ ] realpath 정규화 + 신뢰 과확장 방지 (worktree 대비).
- [ ] 신뢰 기록 전 사용자 옵트인 토글 (보안 민감).

**산출물**: `AgentHookInstaller.cs`, `TrustPresetWriter.cs`, `-cli agent-hook`, 훅 템플릿.
**테스트**: `ZeroCommon.Tests`에 훅 이벤트 파싱 + trust 경로/포맷 유닛 테스트(헤드리스).
**평가축**: 코드 안전성(신뢰 파일 기록 안전) / 아키텍처 정합성(액터 WPF 비의존) / 테스트 가능성.

---

## Phase 2 — Diff 리뷰 & App-CLI (3~4주, P1) 🥈   ✅ **W3(2a) 완료**

### 2a. Diff 리뷰 + 인라인 주석 (orca 기능 H) — 완료
- [x] `GitDiffReader`(ZeroCommon, 순수 파서) + 7 테스트. `GitDiffService`(WPF, git shell-out).
- [x] `DiffReviewPanel`(WebView2 자체완결 HTML diff — Monaco 대신 경량) + 라인 주석 브리지.
- [x] 라인-앵커 주석 수집 → `BuildReviewPrompt` → `AgentBotWindow.PostAiRequest`(AI-loop 재투입).
- [x] 주석 영속 `DiffComment` 엔티티 + `AddDiffComments` 마이그레이션.
- [x] ActivityBar 버튼 + 오버레이 + 토글(형제 핸들러 연동).
- [ ] (후속) operator 데스크톱 스모크(WebView2/git 런타임), Staged 토글, 파일 접기.

### 2b. App-CLI 확장 + 안티드리프트 스킬 스텁 (orca 기능 D, C)
- [ ] `-cli` 확장: `terminal-wait`(TUI-idle 대기), `worktree`(2b 후 Phase 3와 연계), `orchestrate`(Phase 3).
- [ ] `SkillStubInjector.cs` — 호스트 에이전트 스킬 폴더(`~/.claude/skills/agentzero/`)에 **스텁만** 주입.
      전체 가이드는 앱이 `-cli help <topic>`로 런타임 서빙 (드리프트 방지).
- [ ] `OrcaCliLauncher.cs` 참조 — Windows 런처 개행/PATH 함정 대응 확인 (AgentZero는 단일 exe라 유리).

**산출물**: `DiffReviewPanel`, `DiffComment` 엔티티+마이그레이션, `SkillStubInjector.cs`, `-cli` 신규 커맨드.
**테스트**: diff 파싱 + 주석→프롬프트 조립 유닛 테스트. 스텁 주입 멱등성 테스트.

---

## Phase 3 — 감독형 오케스트레이션 (4~6주, P1) 🥇 최대 차별점   ✅ **W6 코어 완료**

목표: 단일 에이전트 → **감독형 워커 풀**. AgentZero의 Akka 액터에 자연 매핑.

### 3a. Run/Task/Dispatch 모델 (orca 기능 A) — 코어 완료
- [x] SQLite 엔티티 3종 `OrchestrationRun/Task/Dispatch` + `AddOrchestration` 마이그레이션.
- [x] `OrchestrationDag`(ZeroCommon, 순수) — readiness/cycle/complete. 7 테스트.
- [x] `Messages.cs` 섹션 9 — `StartOrchestrationRun/DispatchTaskToWorker/WorkerDone/Heartbeat/
      AskCoordinator+Reply/Escalation/QueryRunStatus+Reply/RunCompleted`.
- [x] `CoordinatorActor`(transport-agnostic, workerRouter 주입) — dispatch/전진/사이클 abort/
      실패 처리/ask 게이트. Akka.TestKit 6 테스트.
- [ ] (follow-up) 워커 라우터→실제 에이전트 배선, `-cli orchestrate`, 인메모리↔영속 연동.

### 3b. git worktree = 1급 워크스페이스 — 미착수 (follow-up)

### 3b. git worktree = 1급 워크스페이스 (orca 기능 A 보조)
- [ ] `WorkspaceActor` 모델 확장: `(repo, worktreePath, branch, hostedTabs, statusBadge)`.
- [ ] `git worktree add/remove/list` 셸아웃 래퍼 (`ZeroCommon/Module/GitWorktreeBuilder.cs`).
- [ ] worktree 생성 시 hosted 에이전트 탭 자동 배치 + Phase 1 trust preset 연동.

**산출물**: 오케스트레이션 엔티티/마이그레이션, 액터 메시지, `GitWorktreeBuilder.cs`, `-cli orchestrate`.
**테스트**: DAG 의존성 해석, 인박스 라우팅, worker_done 완료 추적 유닛 테스트(헤드리스, mock toolbelt).
**평가축**: 아키텍처 정합성(액터 supervision 전략, 메시지 불변성) 특히 중요.

---

## Phase 4 — 차별화 심화 (선택, P2~P3)

우선순위 낮음/비용 높음. 개별 승인 후 착수.

- [ ] **지속형 out-of-process PTY 데몬** (orca 기능 B) — ConPTY 호스트 분리, 네임드파이프. ⚠️ endpoint-ownership
      함정(`daemon/AGENTS.md`). 고비용·고차별.
- [ ] **$ 비용 레이어 + 커맨드 팔레트** (orca 기능 K) — 기존 `TokenUsageRecord`에 가격표 + 예산 UI,
      워크스페이스/에이전트 퍼지 팔레트. 저비용.
- [ ] **Design Mode** (orca 기능 G) — WebDev WebView2에 DOM 클릭→HTML/CSS/스크린샷→프롬프트.
- [ ] **스케줄 자동화** (orca 기능 J) — Quartz.NET, 프롬프트 스케줄 실행.
- [ ] **캡슐형 플러그인 샌드박스** (orca 기능 I) — capability manifest + out-of-process + WebView2 패널. 고비용.

---

## 하네스 추적 규약

- **엔진**: `harness/engine/orca-adoption.md`가 이 계획의 진행을 오케스트레이션.
- **로그**: 페이즈 착수/완료마다 `harness/logs/orca-adoption/{yyyy-MM-dd-HH-mm-title}.md`.
- **평가**: 매 로그에 3축(코드 안전성 / 아키텍처 정합성 / 테스트 가능성) 등급.
- **체크리스트**: 본 문서의 `[ ]/[~]/[x]`를 진행에 맞춰 갱신.
- **버전**: 구조 변경 시 `harness.config.json` + `harness/docs/vX.Y.Z.md` 갱신.
- **차크라 평가**: 페이즈 엔진 실행 종료 후 `harness-chakra-kakashi`로 토큰 효율 감사(옵션).
