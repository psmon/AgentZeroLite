# orca 기능 카탈로그 — 파일 경로 + WPF 이식성

> 참조본: `E:\git-other\orca` (읽기 전용). 아래 경로는 모두 그 하위.
> 구현은 재사용 불가(TS/Electron) — **개념·명세만** 이식.

## 0. 아키텍처 개요

- **프로세스 모델**: `src/main/`(Electron main, ~90개 도메인 슬라이스) / `src/renderer/`(React, Zustand
  슬라이스 ~150) / `src/preload/` / `src/cli/`(자체 `orca` CLI) / `src/relay/`(모바일·SSH 릴레이) /
  `src/shared/`(agent-* 카탈로그).
- **IPC**: `src/main/ipc/`(433 파일). `register-core-handlers.ts`가 ~80개 도메인 핸들러를 조립.
  민감 채널은 신뢰된 renderer webContents id로 잠금(`setTrusted*RendererWebContentsId`).
- **상태/영속**: renderer=Zustand 슬라이스, main=`Store`+SQLite(`src/main/sqlite/`). 터미널
  스크롤백은 데몬+`@xterm/addon-serialize`로 재시작 후 콜드 리플레이.
- **에이전트 기동**: `src/main/daemon/`(별도 장수 프로세스가 모든 PTY 소유) →
  `src/main/providers/local-pty-provider.ts`(+ SSH/WSL/ephemeral-VM provider). 모두
  `*-provider-contract.ts`로 추상화되어 local/SSH/WSL 교체 가능.

---

## A. 감독형 멀티에이전트 오케스트레이션 ★최대 차별점

- **위치**: `skill-guides/orchestration.md`(406줄 정본), `src/cli/` 오케스트레이션 핸들러, SQLite 오케스트레이션 DB.
- **동작**: 코디네이터 에이전트가 **Run**(네임스페이스/인박스) → **Task**(의존성→DAG) →
  **Dispatch**(태스크 시도를 터미널에 배정). 워커는 `worker_done`/`heartbeat`/`escalation`/`ask`를
  코디네이터 인박스로 라우팅. blocking ask/reply, decision gate, `check --wait`(폴링 대신), 원격 워커 지원.
  "full handoff"(소유권 이전, 관찰 중단) vs "supervised dispatch"(완료 추적) 구분.
- **WPF 이식성**: ★★★. 메시징/DAG 모델은 언어 무관 → C# `Run/Task/Dispatch` 스키마 + 인박스로 직역.
  **AgentZero는 이미 Akka 액터가 있어 orca보다 유리** — `StageActor`=코디네이터 인박스, 워크스페이스
  에이전트=워커.

## B. 지속형 out-of-process PTY 데몬

- **위치**: `src/main/daemon/`, `daemon/AGENTS.md`, `daemon/cold-restore-replay-writer.ts`.
- **동작**: 별도 장수 데몬이 유닉스도메인/네임드파이프 소켓으로 모든 PTY 소유 → 터미널(및 그 안 에이전트)이
  **앱 재시작·크래시·업데이트에도 생존**. `daemon/AGENTS.md`는 endpoint-ownership 프로토콜(private name
  bind → 배타적 link → 접속으로 기존 데몬 사망 증명 → 원자적 rename)로 떠나는 데몬이 살아있는 대체본
  소켓을 지우는 사고를 방지.
- **WPF 이식성**: ★★(고비용). 렌더링 엔진은 이식 불가하나 **아이디어 2개가 금**: (1) 재시작 생존하는
  out-of-process PTY 호스트(ConPTY 기반), (2) **TUI-idle 감지**(에이전트 렌더 완료 시점 파악 → 프로그램적
  wait/send). ⚠️ `net.Server.close()`가 소켓을 무조건 unlink하는 함정 — 네임드파이프 데몬 구현 시 필독.

## C. 안티드리프트 스킬 시스템 (스텁/가이드/바이너리-서브)

- **위치**: `skills/<name>/SKILL.md`, `skill-stubs/<name>.md`(66줄 발견용 스텁),
  `skill-guides/<name>.md`(400줄 버전-매칭 정본), `src/main/skills/discovery.ts`, `skill-discovery-sources.ts`.
- **핵심 통찰**: **스텁만 각 에이전트 스킬 폴더에 기록**되어 트리거용으로 씀. 스텁 자체가 명시:
  *"이 파일은 발견용 스텁이지 사용 가이드가 아니다. 전체 참조는 `orca` 바이너리가 런타임에 서빙 —
  실제 커맨드를 돌릴 바이너리와 절대 드리프트되지 않도록 일부러 뺐다."* Claude plugin 캐시/WSL/원격에서
  발견, freshness/convergence로 호스트 간 동기 유지.
- **WPF 이식성**: ★★★. AgentZero는 **작은 스킬 스텁을 호스트 에이전트 스킬 디렉토리(`~/.claude/skills/...`)에
  주입**하고, 전체 버전-매칭 지침은 앱이 런타임에 서빙 → "문서-툴 드리프트" 문제 해결.

## D. App-as-tool CLI/RPC (에이전트가 앱을 스크립팅)

- **위치**: `src/cli/`(bin `orca`), `skill-guides/orca-cli.md`, **`native/windows-cli-launcher/OrcaCliLauncher.cs`(C#)**.
- **동작**: 실행 중 앱이 RPC 기반 CLI를 노출 → 터미널 속 에이전트가 앱을 스크립팅: `orca worktree create`,
  `terminal send/read/wait`, `snapshot/click/fill`(브라우저), `automations`, `artifacts share`. 앱이 진실 원천.
  Windows 런처는 `Orca.exe`를 `ELECTRON_RUN_AS_NODE=1`로 찾아 `out/cli/index.js`로 포워드 — 임베디드
  개행 보존, PATH 중복 env 버그(#12046) 회피(cmd.exe 경유 안 함).
- **WPF 이식성**: ★★★(직결). AgentZero는 **이미 `WM_COPYDATA` IPC + `-cli` 12종** 보유 → 그것을 "에이전트가
  부르는 도구"로 확장. `OrcaCliLauncher.cs`가 정확히 우리 Windows 문제의 레퍼런스 구현.

## E. 에이전트 훅 (CLI 훅으로 실제 상태/트랜스크립트 수신)

- **위치**: `src/main/agent-hooks/`, `src/shared/agent-hook-*`.
- **동작**: 에이전트 CLI(예: Claude Code)에 훅을 설치 → 구조화된 상태/트랜스크립트 이벤트를 앱으로 역보고.
  터미널 출력 스크래핑이 아니라 훅으로 에이전트의 **실제 상태**를 알고 트랜스크립트를 읽음.
- **WPF 이식성**: ★★★(고레버리지). AgentZero의 `ApprovalParser` 휴리스틱 스크래핑을 근본 대체. Claude Code
  hook이 `AgentZeroLite.exe -cli`를 호출해 상태 역보고하도록.

## F. Trust preset (각 CLI 신뢰 파일 사전 기록)

- **위치**: `src/main/agent-trust-presets.ts`, `src/main/ipc/agent-trust.ts`.
- **동작**: 각 에이전트 CLI의 신뢰 마커를 **미리 기록**해 "이 폴더 신뢰?" 프롬프트가 주입 키입력을 가로채지
  않게 함. 예: Cursor `~/.cursor/projects/<slug>/.workspace-trusted`, Copilot `~/.copilot/config.json`의
  `trustedFolders`, Codex `~/.codex/config.toml`의 `[projects."<path>"] trust_level="trusted"`. realpath
  정규화 + worktree `.git` 백링크 검증으로 신뢰 과확장 방지.
- **WPF 이식성**: ★★★. 이 파일이 곧 **각 CLI 신뢰 상태 저장 위치 명세** → C#로 그대로 기록. ⚠️ 자동 신뢰는
  편하지만 보안 민감 결정.

## G. Design Mode + 임베디드 브라우저

- **위치**: `src/main/browser/`(agent-browser-bridge, grab/screenshot, cookie import), `src/renderer/src/components/browser-pane/`.
- **동작**: 앱 내 실제 Chromium 탭. **Design Mode**: 아무 요소나 클릭 → 그 HTML+CSS+크롭 스크린샷을 에이전트
  프롬프트로 직송. 브라우저는 CLI로 완전 스크립팅(`snapshot/click/fill/eval`).
- **WPF 이식성**: ★★(WebView2 부분 이식). "요소 클릭→HTML/CSS/스크린샷 캡처→프롬프트 삽입"은 WebView2 +
  주입 JS로 가능. AgentZero는 이미 WebDev 브라우저 툴링 보유.

## H. Diff 리뷰 + 인라인 주석

- **위치**: `src/renderer/src/components/diff-comments/`(`DiffCommentCard.tsx`, `useDiffCommentDecorator.tsx`),
  `source-control/`, `diffComments` 슬라이스.
- **동작**: Monaco 기반 diff 뷰에서 **아무 diff 라인에나 주석을 달아 후속 지시로 에이전트에 전송**. 앱을 떠나지
  않고 리뷰/편집/커밋. GitHub/GitLab/Linear/Jira PR·이슈 브라우징 네이티브.
- **WPF 이식성**: ★★(구현 무거움). 개념은 이식되나 diff/거터-데코 에디터 필요(AvalonEdit 또는 WebView2+Monaco).
  **값진 이식 포인트**: 라인-앵커 주석을 모아 구조화 프롬프트로 에이전트에 재투입하는 흐름.

## I. 캡슐형 플러그인 (샌드박스 패널 + capability 모델)

- **위치**: `src/main/plugins/`(~50 파일: discovery, content-integrity/hash, host-call-adapter),
  `examples/plugins/hello-orca/`, `examples/plugins/hostile-panel/`(보안 테스트 픽스처).
- **동작**: 플러그인이 manifest 선언(`contributes: {panels,commands,events}` + 명시적 `capabilities` 배열:
  `workspace:read`, `terminal:send`, `notifications:show`, `storage`...). 워커는 **out-of-process(순수 Node,
  최초 트리거 시 lazy fork)**, 호스트 접근은 **capability-게이트된 `orca.host.call()`**로만. 패널은 샌드박스 HTML.
- **WPF 이식성**: ★★. capability-스코프 out-of-process 플러그인 모델은 이식성 좋음(.NET 자식 프로세스 또는
  `AssemblyLoadContext` + capability manifest + WebView2 샌드박스 패널).

## J. 스케줄 자동화

- **위치**: `src/main/automations/`, `orca automations` CLI.
- **동작**: 프롬프트를 `hourly/daily/cron/RRULE`로 스케줄 → fresh worktree 또는 기존 워크스페이스에 실행,
  `--reuse-session`/`--fresh-session`.
- **WPF 이식성**: ★★★(저비용). Quartz.NET 또는 타이머로 워크스페이스에 프롬프트 에이전트 기동.

## K. 기타 저비용·고효용 이식 후보

- **커맨드 팔레트(Cmd-J)** — `renderer/src/components/cmd-j/`(cmdk). 워크스페이스/파일/에이전트/커맨드 퍼지 검색.
- **계정 스위처 & 사용량 추적** — `src/main/claude-usage/ codex-usage/ rate-limits/ claude-accounts/`. 각 에이전트
  토큰 사용량 + rate-limit 리셋 표시, 재로그인 없이 계정 hot-swap. **AgentZero는 이미 토큰 텔레메트리 보유** →
  $ 가격 레이어만 추가.
- **에이전트 탐지 카탈로그** — `src/shared/agent-detection.ts`, `agent-session-option-catalog*.ts`. agent →
  detect/launch/resume/model-options 매핑. C# 모델로 직역 가능한 설정 데이터.
- **provider 추상화** — `src/main/providers/*-provider-contract.ts`. local/SSH/WSL/VM 교체 가능 인터페이스.
  AgentZero는 이미 SSH 원격 `CliDefinition` 보유.

---

## 차별화 랭킹 (이식성 × 차별성, AgentZero 기준)

1. 감독형 오케스트레이션 (Run/Task/Dispatch DAG) — `skill-guides/orchestration.md`
2. 지속형 out-of-process PTY 데몬 — `src/main/daemon/`
3. 안티드리프트 스킬 스텁 — `skill-stubs/` vs `skill-guides/`
4. App-as-tool CLI/RPC — `src/cli/` + `OrcaCliLauncher.cs`
5. 에이전트 훅 + trust preset — `src/main/agent-hooks/`, `agent-trust-presets.ts`
6. capability-스코프 플러그인 — `src/main/plugins/`
7. Design Mode — `src/main/browser/`
8. 인라인 diff 주석 → 에이전트 — `diff-comments/`
9. provider 추상화 — `providers/*-provider-contract.ts`

**엔지니어링 규율 takeaway** (`AGENTS.md`, `daemon/AGENTS.md`): 원격 wire 변경은 버전/capability 네고,
git-capability는 호스트별 스코프, Windows 런처 셸을 사용자 터미널 취향에서 유도 금지, 소켓/네임드파이프
데몬의 endpoint-ownership 함정 주의.
