---
date: 2026-08-10T15:30:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W8 파일 도구 구현"
engine: orca-adoption
phase: W8
---

# W8 — IAgentToolbelt 파일 도구 (read_file/write_file/edit_file/grep) 완료

## 실행 요약

에이전트 toolbelt에 파일 도구 4종을 추가. 순수 로직은 `ZeroCommon`에 배치해 헤드리스
테스트, 실제 파일 접근은 WPF 호스트가 **활성 워크스페이스 루트로 샌드박싱**.

## 결과 (편집 지점)

- `Project/ZeroCommon/Llm/Tools/FileToolCore.cs` **(신규)** — `ReadFile/WriteFile/Edit/Grep`
  순수 함수. 경로 정규화+루트 prefix 검증(탈출 거부), 바이너리 감지, edit 유일성/replace_all,
  grep 무시디렉토리(.git/bin/obj/node_modules) prune, JSON 엔벨로프(`JsonSerializer`).
- `Project/ZeroCommon/Llm/Tools/IAgentToolbelt.cs` — 4개 메서드 추가(default = "no workspace
  root bound" = **default-deny**).
- `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs` — 3곳 lockstep: SystemPrompt 카탈로그 +
  Gbnf toolname alternation + KnownTools.
- `Project/ZeroCommon/Llm/Tools/LocalAgentLoop.cs` + `ExternalAgentLoop.cs` — 미러 switch case 4종.
- `Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs` — `workspaceRootProvider` 추가 +
  4메서드 override → `FileToolCore` 위임 + AppLogger.
- `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` — 루트 provider(첫 실존 DirectoryPath 그룹)
  주입.
- `Project/ZeroCommon.Tests/FileToolCoreTests.cs` **(신규)** — 18 테스트.

## 검증

- `dotnet test ...ZeroCommon.Tests --filter FileToolCoreTests` → **18/18 통과** (177ms).
  (샌드박스 탈출 거부, 절대경로 외부 거부, write→read 왕복, 바이너리 거부, 트렁케이션,
   edit 유일/모호/replace_all/미존재, grep 매칭·무시디렉토리·정규식오류·maxResults,
   grammar lockstep guard 4종.)
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A− | 루트 샌드박싱 강제 + default-deny. 단, write/edit 승인 게이트는 미구현(아래 제안). |
| 아키텍처 정합성 | Pass | 순수 로직 ZeroCommon(헤드리스), WPF 의존 없음. OS 도구 default-method 패턴 준수. lockstep 3곳 동기. |
| 테스트 가능성 | A | 핵심 로직 전부 헤드리스 유닛(임시 dir). |
| 이식 충실도 | Pass | orca 개념(파일 도구)만 이식, 구현은 신규 C#. |
| 스코프 규율 | Pass | W8 범위 유지. |

## 다음 단계 제안

- **security-guard 리뷰**: write_file/edit_file에 대한 **옵션 승인 게이트**(OsApprovalGate 유사
  env/Settings 토글) 추가 검토. 현재는 워크스페이스 루트 샌드박스만으로 방어.
- 워크스페이스 "활성 그룹" 개념이 모호 → 현재 첫 실존 DirectoryPath 그룹 사용. 다중 워크스페이스
  시 활성 터미널 기준으로 정교화 여지.
- W3 diff 재투입/W6 오케스트레이션이 이 파일 도구를 전제로 진행 가능.
