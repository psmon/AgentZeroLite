---
date: 2026-08-10T16:40:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W1 에이전트 훅 상태 수신"
engine: orca-adoption
phase: W1
---

# W1 — 에이전트 훅 상태 수신 완료

## 실행 요약

터미널 출력 스크래핑(`ApprovalParser`/`AgentEventStream`) 대신, 호스팅된 에이전트
CLI(Claude Code)의 훅이 `-cli agent-hook`로 실제 상태를 역보고하도록 구현. 사용자
`~/.claude` 수정은 **동의 필요한 외부 변경**이므로 자동 설치 대신 명시적 CLI 명령으로 노출.

## 결과 (편집 지점)

- `Project/ZeroCommon/Actors/Messages.cs` — `AgentHookEvent(HookEvent, StateOverride, Session, Detail)` record.
- `Project/ZeroCommon/Actors/AgentHookMapper.cs` **(신규, 순수)** — Claude 훅 이벤트→`AgentLoopPhase`
  매핑 + 표시 텍스트. StateOverride 우선, 미지정 시 이벤트명 매핑.
- `Project/ZeroCommon/Actors/AgentBotActor.cs` — `Receive<AgentHookEvent>` → `AgentHookMapper.Resolve`
  → `_agentLoopOnProgress` 직접 호출(봇=단일 UI 게이트웨이; 호스팅 CLI는 온디바이스 루프와 독립).
- `Project/ZeroCommon/Agents/AgentHookSettingsMerger.cs` **(신규, 순수)** — settings.json `hooks`
  서브트리 병합/제거. 마커(`-cli agent-hook`)로 자사 항목 식별 → 멱등 재설치, 외부 훅 보존.
- `Project/AgentZeroWpf/Services/AgentHookInstaller.cs` **(신규)** — `~/.claude*` 발견 + 백업 +
  원자적 쓰기(.tmp+Move), StatusLineWrapperInstaller 기계 모델링. InstallAll/UninstallAll.
- `Project/AgentZeroWpf/CliHandler.cs` — `agent-hook`(fire-and-forget), `agent-hook-install`/
  `agent-hook-uninstall`(in-process, GUI 불필요) + usage.
- `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs` — `agent-hook` IPC 분기 → `HandleAgentHook`
  → 봇 액터에 `AgentHookEvent` tell.

## 검증

- `dotnet test ...ZeroCommon.Tests --filter AgentHookTests` → **17/17 통과** (49ms).
  (이벤트→phase 매핑 9종, StateOverride 우선/폴백, detail 텍스트, merger 생성/멱등/외부보존/제거·프룬/무변경.)
- 관련 카테고리 회귀: AgentHook+FileTools+AgentLoop → **41 통과, 13 스킵(모델 의존), 0 실패**.
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | `~/.claude` 변경은 명시적 CLI 명령으로만(동의). 백업 + 원자적 쓰기 + 마커 기반 안전 제거. 외부 훅 보존. |
| 아키텍처 정합성 | Pass | 순수 로직(Mapper/Merger) ZeroCommon(헤드리스). 액터 WPF 비의존. 봇=단일 UI 게이트웨이 원칙. |
| 테스트 가능성 | A | 매핑·병합 전부 헤드리스 유닛. |
| 이식 충실도 | Pass | orca agent-hooks 개념 + settings.json hooks 스키마만 이식, 구현 신규. |
| 스코프 규율 | Pass | 자동 설치 배제(동의), UI 토글은 후속. |

## 다음 단계 제안

- **UI 토글**(Settings에 "에이전트 훅 설치/제거" 버튼) — 현재 CLI 전용. operator 편의 후속.
- 훅 스크립트가 `$CLAUDE_SESSION_ID`/stdin JSON에서 session·detail을 채우도록 정교화 여지
  (현재 event만으로도 phase 구동).
- 스크래핑(`AgentEventStream`)은 폴백으로 유지 — 훅 미설치 환경 호환.
- Sprint 2(W3 diff 리뷰)로 진행 가능.
