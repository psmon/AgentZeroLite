---
date: 2026-09-22T12:28:52+09:00
agent: tamer
type: creation
mode: execution
command: "/harness-creator"
---

# AgentOne v0 — 독립 CLI 에이전트 워킹 스켈레톤 심기

## 실행 요약

operator 가 `/harness-creator` 로 정원을 연 직후, 정원 작업이 아니라
**새 프로젝트 골격 생성**을 요청했다: "AgentZero 가 이용할 수도 있는 독립 CLI
Agent **AgentOne**", `Project/` 하위 독립 추가, 멀티 OS, 기본 골격은
`C:\code\psmon\CodeScan` 참고, `agent-one` 이름으로 동작, 로컬 구현·테스트 후
npm 배포.

매칭되는 전문가/엔진 트리거가 없어 Mode B(제안) 로 폴백하지 않고, **되돌리기
비싼 갈림길 4개만 확정**한 뒤 tamer 가 직접 수행했다. 네 항목 모두 operator 가
권장안을 선택:

| 갈림길 | 확정 | 근거 |
|---|---|---|
| 런타임/배포 | C# .NET 10 + Native AOT + npm 래퍼 | CodeScan 과 동일 골격, 레포 C# 일관성 유지 |
| v0 범위 | 워킹 스켈레톤 우선 | 능력보다 **배포 경로를 먼저 증명** |
| 프로바이더 | OpenAI-compat REST + echo | 한 wire 포맷으로 OpenAI·Ollama·LM Studio·vLLM 커버 |
| 레포 배치 | 같은 레포, 독립 프로젝트 | 분리 가능하도록 경계만 유지 |

선행 조사에서 CodeScan 의 `packaging/npm/codescan-cli/` 가 이미 **GitHub
Releases 에셋 다운로드 + SHA256 검증 + vendor 해제** 패턴을 갖고 있음을 확인
했고, 그것을 그대로 옮겨 심었다 — "멀티 OS + npm" 은 이 조직에서 이미 검증된
경로다.

## 결과

### 심은 것

```
Project/AgentOne/              agent-one (net10.0, PublishAot)
  Program.cs                   argv switch 라우팅 (CodeScan 패턴)
  Commands/                    Run · Chat · Config · Tools + 공용 플래그 파서
  Agent/                       AgentLoop · ToolCall(엔벨로프) · Guards · SystemPrompt
  Llm/                         IChatProvider · Echo · OpenAI-compat · Factory
  Tools/                       ToolCatalog(단일 진실) · LocalFileToolbelt(샌드박스)
  Services/                    ~/.agent-one/ · config · 세션 JSONL · JSON 소스젠
  packaging/npm/agent-one/     래퍼 (bin · install · uninstall · README)
Project/AgentOne.Tests/        xUnit 63개
.github/workflows/agent-one-release.yml   태그 agent-one-v* → 4 RID + npm
```

커밋 `8c1271c`, 브랜치 `feat/agent-one` 푸시 완료 (42 파일).

### 검증한 것 (실행 결과, 추정 아님)

- `dotnet build` 경고 0 / 오류 0 · `dotnet test` **63 passed**
- **win-x64 AOT publish 성공 — 5.77 MB 단일 exe**, 런타임 설치 불필요.
  퍼블리시된 바이너리로 `run` / `tools` / 세션 기록 재확인
- 샌드박스: `../../../../Windows/win.ini` → `path escapes the workspace root`
- npm 래퍼: `node --check` 3종 · `AGENT_ONE_SKIP_DOWNLOAD=1` 경로 · vendor
  바이너리 spawn · uninstall 정리까지 실제 실행

### 도중에 잡은 결함 2건

| 결함 | 증상 | 조치 |
|---|---|---|
| AOT JSON 반사 경로 혼입 | IL2026/IL3050 경고 6건. published 바이너리에서만 터지는 종류 | 컨텍스트를 `AgentOneJson`(config) / `AgentOneWireJson`(wire·JSONL·`--json`) 로 분리, `JsonSerializerIsReflectionEnabledByDefault=false` 로 **빌드 시점 실패**로 고정 |
| 파이프 입력 한글 깨짐 | `echo "한글" \| agent-one run` 이 콘솔 코드페이지(949)로 디코딩 | stdin 을 UTF-8 `StreamReader` 로 명시 (`RunCommand.cs:36`). argv 경로는 원래 정상 |

### 환경 함정 (다음 세션용)

- Windows AOT 링크는 `vswhere.exe` 가 PATH 에 있어야 한다
  (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`). 없으면
  `MSB3073 exit 123` 으로 죽는다 — 실제로 한 번 실패 후 PATH 추가로 통과.
  Linux 는 `clang` + `zlib1g-dev`. 워크플로에는 둘 다 반영됨.
- **Bash heredoc 은 이 환경에서 백슬래시를 먹는다.** `'EOF'` 인용 heredoc 인데도
  `'\\'` → `'\'` 로 바뀌어 C# 파서 오류가 났다. 코드 파일은 Write/Edit 도구로
  쓸 것.
- `.gitignore` 의 전역 `[Bb]in/` 규칙이 npm 런처
  (`packaging/npm/agent-one/bin/agent-one.js`) 를 삼켰다. 예외 규칙 추가함.

### 정원 관점에서 한 판단

ZeroCommon 의 `IAgentLoop` 재사용을 **의도적으로 포기**했다. Akka + EF Core +
LLamaSharp + ONNX + `runtimes/win-x64-*` 네이티브가 전이 의존으로 붙어 있어,
참조하는 순간 Native AOT 도 비-Windows 타깃도 성립하지 않는다. 대신 필요한
작은 부분만 재구현해 **별도 레포로 분리 가능한 상태**를 유지했다. 연동은
프로세스 레벨(`agent-one --json` → stdout 한 줄)로 설계.

`CLAUDE.md` 에 해당 절을 추가해 다음 세션이 이 경계를 다시 물어보지 않게 했다.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 워크플로우 개선도 | **A** | 조사 → 4갈림길 확정 → 구현 → 빌드/테스트/AOT/npm 실검증까지 한 세션에 관통. 검증된 CodeScan 패키징 패턴을 재조사 없이 이식해 왕복 비용을 없앰 |
| Claude 스킬 활용도 | **2 / 5** | 이번 작업은 스킬을 거의 쓰지 않았다. `agent-zero-build`(릴리스 파이프라인), `skill-creator`, `playwright-e2e` 어느 것도 관여하지 않았고 정원의 전문가도 투입되지 않음 — 순수 tamer 단독 수행 |
| 하네스 성숙도 | **L3 (유지)** | 이 작업으로 정원 구조는 변하지 않았다. agents/engine/knowledge 어디에도 추가가 없고 로그 1건만 늘었다. 새 프로젝트를 담당할 전문가/지식은 아직 없음 |

정원 규칙 준수 점검 (`creator-rule.md`):

- Rule 1/2 — 에이전트·엔진을 추가하지 않았으므로 lane 침범 없음 ✔
- Rule 3 — 트리거 문구를 어떤 knowledge 파일에도 넣지 않음 ✔
- Rule 6 — 엔진 미실행, 단일 actor(tamer) 로그 1건만 기록 ✔

**드러난 공백**: `Project/AgentOne` 은 지금 **어떤 전문가도 담당하지 않는다**.
AOT 링크 툴체인·멀티 RID·npm 배포 파이프라인은 성격상 `build-doctor` 의 lane
(build pipeline + native + version)에 가장 가깝지만, 그 에이전트 정의는 현재
WPF/설치본 릴리스만 알고 있다.

## 다음 단계 제안

1. **(정원)** `build-doctor` 에 agent-one 의 AOT/npm 릴리스 경로를 알려줄지
   결정 — 기존 lane 확장 vs 신규 전문가 영입. Rule 1 상 확장이 우선 검토 대상.
   operator 승인 전까지 실행하지 않는다.
2. **(정원)** agent-one 이 별도 레포로 나갈 가능성이 있으므로, 지식 파일을
   심는다면 `_shared/` 가 아니라 프로젝트 README 에 두는 현 방식 유지.
3. **(제품)** 도구 확장 — `write_file` / `run_shell` + 승인 프롬프트. 샌드박스가
   읽기 전용인 지금이 경계를 넓히기 가장 싼 시점.
4. **(제품)** Anthropic 네이티브 프로바이더 (tool_use 블록 + prompt caching).
5. **(제품)** AgentZero ↔ agent-one 프로세스 연동 커맨드.
6. **(배포)** `agent-one-v0.1.0` 태그를 실제로 밀어 릴리스 워크플로 첫 가동 —
   4 RID 빌드와 npm 스탬핑은 아직 CI 에서 한 번도 돌지 않았다.
