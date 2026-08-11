---
date: 2026-08-10T20:10:00+09:00
agent: security-guard
type: creation
mode: log-eval
trigger: "orca 페이즈 — W2 Trust preset"
engine: orca-adoption
phase: W2
---

# W2 — Trust preset writer 완료

## 실행 요약

각 에이전트 CLI(Cursor/Copilot/Codex)의 신뢰 저장소에 워크스페이스를 사전 등록해
"이 폴더 신뢰?" 프롬프트가 주입 키입력을 가로채지 않게 함. orca
`src/main/agent-trust-presets.ts`의 검증된 파일 위치/포맷 명세 이식.

## 결과

- `Project/ZeroCommon/Agents/TrustPresetWriter.cs` **(신규)** — 순수 헬퍼(CursorSlug,
  BuildCursorPayload, UpsertCodexTrust, AddCopilotFolder) + IO 메서드(home 파라미터화).
  - Cursor: `~/.cursor/projects/<slug>/.workspace-trusted` (JSON), slug=경로 정규화.
  - Copilot: `~/.copilot/config.json` `trustedFolders[]` 병합(외부 키 보존).
  - Codex: `~/.codex/config.toml` `[projects."<path>"] trust_level="trusted"` upsert(멱등).
- `Project/AgentZeroWpf/CliHandler.cs` — `trust-workspace [path]` (in-process, GUI 불필요) + usage.
- `Project/ZeroCommon.Tests/TrustPresetWriterTests.cs` **(신규)** — 10 테스트.

## 검증

- `dotnet test --filter Category=TrustPreset` → **10/10 통과**(slug 변환, TOML upsert 멱등/보존,
  JSON 병합 멱등/외부키 보존, 3-store IO 라운드트립, 2회 실행 멱등).
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 신뢰 파일 수정은 **명시적 CLI 명령**(동의). 원자적 쓰기. 멱등(중복 등록 방지). 외부 키/섹션 보존. |
| 아키텍처 정합성 | Pass | 순수 변환 ZeroCommon(헤드리스, home 파라미터화). |
| 테스트 가능성 | A | slug/TOML/JSON 변환 + IO 전부 헤드리스. |
| 이식 충실도 | Pass | orca 위치 명세 이식, C# 재구현. |

## 다음 단계 제안

- **worktree 백링크 검증**(orca resolveCodexProjectTrustRoot) 미이식 — 악의적 `.git`로 신뢰
  과확장 방지 로직. W7 worktree 작업 시 함께 정교화 권장.
- Phase 1(상태 신뢰성) = W1 + W2로 완결. Settings UI 토글은 후속.
