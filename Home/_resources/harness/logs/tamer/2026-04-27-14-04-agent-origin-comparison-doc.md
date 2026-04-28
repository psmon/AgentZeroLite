---
date: 2026-04-27T14:04:00+09:00
agent: tamer
type: creation
mode: log-eval
trigger: "AgentWin 조상 프로젝트와의 정밀 스펙 비교 문서 생성 (Docs/agent-origin/)"
---

# AgentWin ↔ AgentZeroLite 정밀 비교 테크문서 작성

## 실행 요약

사용자 요청: 조상 프로젝트인 `D:\Code\AI\AgentWin`과 현재 프로젝트(`D:\Code\AI\AgentZeroLite`)의 현재 시점 기술 스택을 정밀 비교한 테크문서를 `Docs/agent-origin/` 하위에 작성. 목적은 AgentZeroLite의 차후 기술 채택 방향 결정 (오리진이 더 우수하면 채택).

수행 단계:
1. 하네스 상태 확인 (`harness.config.json` 읽기) — v1.1.2, 5명 에이전트, 2개 엔진 확인
2. 매칭되는 단일 트리거 없음 — 정원지기(tamer) 페르소나로 1회성 분석 진행
3. 두 프로젝트의 정밀 조사를 병렬 Explore 에이전트 2명에게 위임:
   - **AgentWin 조사**: 솔루션·NuGet·UI·터미널·액터·LLM·MCP·DB·CLI/IPC·빌드·테스트·문서·하네스·기타 12개 카테고리
   - **AgentZeroLite 조사**: 동일 12개 카테고리 + Mermaid/Pencil 통합
4. 조사 결과를 토대로 4개 문서 작성:
   - `Docs/agent-origin/README.md` — 인덱스 + Executive Summary + 1페이지 결론
   - `Docs/agent-origin/01-stack-comparison.md` — 항목별 정밀 스펙 비교 (15섹션)
   - `Docs/agent-origin/02-architecture-comparison.md` — 다이어그램 + 분기 사유 (10섹션)
   - `Docs/agent-origin/03-adoption-recommendations.md` — P0/P1/P2/P3 채택 로드맵

## 결과

### 산출 파일

| 파일 | 라인수 추정 | 핵심 내용 |
|---|---|---|
| `Docs/agent-origin/README.md` | ~110 | 비교 대상 스냅샷 표 + 4개 문서 구성 + Executive Summary (P0~P3 등급) |
| `Docs/agent-origin/01-stack-comparison.md` | ~310 | 솔루션·csproj·NuGet·DLL·DB·CLI/IPC·빌드 등 15개 영역 1:1 비교 표 |
| `Docs/agent-origin/02-architecture-comparison.md` | ~310 | 액터·LLM·터미널·CLI·UI·하네스·Speech 다이어그램 + 분기 분석 |
| `Docs/agent-origin/03-adoption-recommendations.md` | ~290 | 13개 채택 항목(P0×2, P1×3, P2×4, P3×4) + 3개 적극 거부 항목 |

### 핵심 발견

**Lite가 명백히 우수한 영역 (유지)**:
1. LLamaSharp 우회 패턴 (self-built llama.dll + custom RID 폴더) — Origin이 ggml.dll 충돌로 포기한 것을 살림
2. `LlmGateway` Local+External hybrid 추상
3. EF Core 마이그레이션 정리 (Origin 31개 vs Lite 1개)

**Origin이 명백히 우수한 영역 (채택 후보)**:
1. **P0-1**: `ReActActor` 5상태 머신 + 다층 가드 (`MaxSameCallRepeats=3`, `MaxConsecutiveBlocks=3`, `MaxAiWaitSeconds=25` 등) — Lite의 단순 루프 대비 안정성 우위
2. **P0-2**: `AppLogger` IDE 디버거 분기 (콘솔/디버거/파일 동시 채널)
3. **P1-1**: Speech 파이프라인 (`Whisper.net` + `NAudio` + `System.Speech`)
4. **P1-2**: CLI 데스크톱 자동화 풀세트 (UIA + 마우스/키보드 시뮬 + 캡처)
5. **P1-3**: `IVirtualDesktopService` (Windows 가상 데스크톱 COM 래핑)
6. **P2-1**: 하네스 도메인 전문가 5명 (`wpf-engineer`, `conpty-engineer`, `ipc-engineer`, `llm-engineer`, `native-efficiency-auditor`)
7. **P2-4**: `LlmSettings` 엔티티로 자격증명 영속화

**적극 거부 (Origin 회귀 금지)**:
- LLamaSharp 제거 결정 → Lite는 self-built로 살림
- 31개 마이그레이션 누적 → Lite의 깨끗한 InitialCreate 유지
- NoteWindow/HarnessMonitorWindow/ScrapPanel 통째 채택 → Lite의 경량 정체성과 충돌

### 권장 작업 시퀀스

```
Sprint N    [P0-1, P0-2]                    안정성·진단 즉시 강화
Sprint N+1  [P1-3, P2-1, P2-4]              경량 가치 우선
Sprint N+2  [P1-1]                          Speech 입력 (옵션 빌드)
Sprint N+3  [P1-2, P2-2, P2-3]              자동화 + UX
Backlog     [P3-*]                          선택적
```

## 평가 (정원지기 3축)

| 축 | 평가 | 근거 |
|---|---|---|
| **워크플로우 개선도** | **A** | 조상 프로젝트와의 차이를 항목별·작업단위로 분해하여 즉시 실행 가능한 로드맵 산출. 단순 비교가 아닌 *우선순위 + 비용 + Trade-off* 명시 |
| **Claude 스킬 활용도** | **5/5** | 병렬 Explore 에이전트 2명으로 두 프로젝트 동시 정밀 조사 → 메인 컨텍스트 보호. Read·Bash·Write 직접 사용 최소화 |
| **하네스 성숙도** | **L4** | 본 분석 자체로 Lite 하네스에 신규 채택 후보 도출 (Origin의 5명 도메인 전문가 + Speech/Automation 모듈) — 정원이 풍요해질 후속 작업 명확 |

## 다음 단계 제안

### 즉시 실행 가능
- **P0-1 채택 검토** — Lite의 `AgentToolLoop` 가드 미흡이 실전에서 무한 호출 유발 가능. 다음 sprint에서 Origin `ReActActor` 가드 상수와 `(functionName, argumentsHash)` 카운터 이식.
- **P2-1 채택** — md 파일 5개 복사만으로 가능. 하네스 v1.2.0 마이너 bump.

### 후속 분석 필요
- Origin `Tech/`, `Prompt/` 디렉토리의 미공개 인사이트 수집 (현재 본 분석에서 표면만 다룸)
- Origin의 `chakra-auditor`가 정확히 무엇을 하는지 — 이름만 식별, 내용 미확인. Origin md 파일 추가 조사 필요.
- Lite의 `MainWindow.xaml.cs` 내 `BotDockHost` 존재 여부 — 본 조사에서 "확인 필요"로 남겨둠.

### 하네스 자체 개선 (메타)
- `agent-origin-comparison`을 정기 워크플로우로 등록할지 검토. 6개월 단위 재조사 권장 (코드베이스가 변하므로).
- 본 문서 내 "확인 안됨"/"확인 필요" 항목 7개 → `code-coach` 또는 신규 `comparison-auditor` 에이전트로 후속 검증.

---

## 트리거 매칭 로그

| 항목 | 값 |
|---|---|
| 매칭된 트리거 | 없음 (1회성 분석) |
| 진입 모드 | 수행부 변종 — tamer 페르소나로 1회성 분석 + 문서 산출 |
| 사용한 도구 | Bash (확인) ×2, Agent/Explore ×2 (병렬), Write ×4 |
| 외부 호출 | 없음 (Origin/Lite 모두 로컬 파일시스템) |
| 안전 게이트 | secret/ 디렉토리는 이름만 식별, 내용 읽지 않음 |

## 관련 산출물

- [Docs/agent-origin/README.md](../../../Docs/agent-origin/README.md)
- [Docs/agent-origin/01-stack-comparison.md](../../../Docs/agent-origin/01-stack-comparison.md)
- [Docs/agent-origin/02-architecture-comparison.md](../../../Docs/agent-origin/02-architecture-comparison.md)
- [Docs/agent-origin/03-adoption-recommendations.md](../../../Docs/agent-origin/03-adoption-recommendations.md)
