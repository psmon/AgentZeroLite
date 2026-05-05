# Agent Origin — AgentWin ↔ AgentZeroLite 비교 문서 세트

> **목적**: AgentZeroLite는 `D:\Code\AI\AgentWin`(이하 **Origin**)에서 분기된 후손 프로젝트다.
> 본 문서 세트는 두 프로젝트의 **현재 시점(2026-04-27) 기술 스택과 아키텍처**를 정밀 대조하여,
> AgentZeroLite의 **차후 기술 채택 방향**을 결정하기 위한 근거 자료로 사용된다.
>
> 비교 원칙: **"오리진이 더 낫다면 채택"** — 단, 채택 시 Lite의 경량성 철학과 충돌하는 부분은
> 각 권고 항목의 *Trade-off* 섹션에서 명시한다.

---

## 비교 대상 스냅샷

| 항목 | Origin (AgentWin) | AgentZeroLite |
|---|---|---|
| 경로 | `D:\Code\AI\AgentWin` | `D:\Code\AI\AgentZeroLite` |
| 버전 (version.txt) | **4.5.5** | **0.1.4** |
| 솔루션 파일 | `AgentWin.slnx` | 없음 (csproj 직접) |
| 프로젝트 수 | 4 | 5 (`LlmProbe` 추가) |
| 어셈블리명 | `AgentZeroWpf.exe` (기본) | `AgentZeroLite.exe` |
| 단일 인스턴스 뮤텍스 | `Local\AgentZeroWpf.SingleInstance` | `Local\AgentZeroLite.SingleInstance` |
| WM_COPYDATA 마커 | `0x4147` ("GA") | `0x414C` ("AL") |
| DB 경로 | `%LOCALAPPDATA%\AgentZero\agentZero.db` | `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` |
| EF Core 마이그레이션 | **31개** (누적) | **1개** (InitialCreate, 2026-04-21) |
| 하네스 에이전트 | **9명** (도메인 전문가 위주) | **5명** (QA 위주) |
| 하네스 엔진 | **4개** | **2개** |
| LLamaSharp | **제거됨** (ggml.dll 충돌) | **0.26.0** (self-built llama.dll) |
| Speech (Whisper/NAudio) | **있음** | 없음 |
| LLM 백엔드 | OpenAI 호환 외부 only | Local + External hybrid |
| 액터 추론 루프 | `ReActActor` (5상태 머신) | `AgentLoopActor` (단순화) |
| 추가 윈도우 | NoteWindow / HarnessMonitorWindow / Scrap / VirtualDesktop API | 없음 |
| CLI 명령 표면 | 데스크톱 자동화 풀세트 (capture, mouseclick, keypress 등) | 핵심 IPC만 (status, terminal-*, bot-chat) |
| 인스톨러 다국어 | English (Inno) + publish/에 16개 언어 잔재 | English + Korean (Inno) |

---

## 문서 구성

| # | 파일 | 다룰 내용 |
|---|---|---|
| 01 | [`01-stack-comparison.md`](./01-stack-comparison.md) | 솔루션·csproj·NuGet·런타임·DB·CLI/IPC·빌드 파이프라인까지 **항목별 스펙 표** |
| 02 | [`02-architecture-comparison.md`](./02-architecture-comparison.md) | 액터 토폴로지·LLM 게이트웨이·터미널 세션·하네스 디자인의 **구조 다이어그램과 분기 사유** |
| 03 | [`03-adoption-recommendations.md`](./03-adoption-recommendations.md) | "오리진이 낫다" 판정과 **채택 우선순위 + 마이그레이션 비용 + Trade-off** |

---

## Executive Summary — 한 페이지 결론

### Lite가 명백히 우수한 영역 (그대로 유지)

1. **로컬 LLM 유지 결정** — Origin은 `LLamaSharp + Whisper.net` ggml.dll 심볼 충돌로 LLamaSharp을 **포기**했다. Lite는 동일 충돌을 *self-built llama.dll + custom RID 폴더(`win-x64-cpu`, `win-x64-vulkan`)* 로 우회하여 온디바이스 + 외부 hybrid를 모두 가진다. **Origin 회귀하지 않을 것**.
2. **External LLM Gateway 단일 진입점** — Lite의 `LlmGateway`는 Local과 External을 단일 추상으로 노출. Origin은 외부만 있어 추상화 레이어가 얕다. Lite의 설계가 더 미래지향적이다.
3. **마이그레이션 누적 부채 제거** — Lite는 `InitialCreate` 한 장으로 출발한 깨끗한 스키마. Origin은 31개 마이그레이션 누적(31개 중 일부는 컬럼 1~2개 추가 수준).

### Origin이 명백히 우수한 영역 (채택 후보)

| 우선순위 | 채택 대상 | 사유 | 비용 |
|:---:|---|---|:---:|
| **P0** | `AppLogger` 파일 로깅 (디버그/콘솔/IDE 분기) | Lite 로깅은 단순. Origin은 IDE 디버거 감지·콘솔 강제 분기 보유 | S |
| **P0** | `ReActActor` 가드 *3종 세트* (`MaxSameCallRepeats=3` + `MaxConsecutiveBlocks=3` + `MaxLlmRetries=1`) | Lite tool-loop은 같은 도구 12회 반복 자유 — 2단 방어(LLM 피드백→차단) 패턴이 무한 호출 보호 | S~M |
| **P1** | `Whisper.net` STT + `NAudio` VAD (선택적 Speech 모듈) | 로컬 음성 입력은 Lite 스코프 결정 필요 | M |
| **P1** | `IVirtualDesktopService` (Windows Virtual Desktop API COM 래핑) | 멀티 데스크톱 워크스페이스에 직결 | S |
| **P1** | CLI 자동화 표면 (`capture`, `text-capture`, `scroll-capture`, `mouseclick`, `keypress`, `screenshot`, `element-tree`) | Lite는 IPC/터미널만, Origin은 UI Automation 풀세트 | L |
| **P2** | 하네스 도메인 전문가 (`wpf-engineer`, `conpty-engineer`, `ipc-engineer`, `llm-engineer`, `native-efficiency-auditor`) | Lite는 QA 5명만, 도메인 리뷰어 부재 | S (md만 복사) |
| **P2** | `BotDockHost` Embedded/Floating 토글 (Ctrl+Shift+\`) | UX 향상, AgentBotWindow 임베드 패턴 | M |
| **P2** | AvalonDock + 멀티탭 LogRow (Bot/Output/Log 탭) | 진단성 향상 | M |
| **P3** | TTS (`System.Speech`) | STT와 짝, 양방향 음성 인터랙션 | S |
| **P3** | 다국어 리소스 분리 패턴 | Lite는 한·영 하드코딩 | L |

### 적극 거부 (Origin → Lite 회귀 금지)

- **LLamaSharp 제거** — Origin이 포기한 결정. Lite의 self-built DLL 우회를 유지한다.
- **31개 마이그레이션 누적** — Origin 스키마를 그대로 import하지 말고, 필요한 엔티티(예: `LlmSettings`, `ClipboardEntry`)만 새 마이그레이션으로 추가.
- **WPF UI 거대화 (NoteWindow + HarnessMonitorWindow + ScrapPanel)** — Lite의 "경량 다중 셸" 정체성과 충돌. 채택 시 별도 빌드 구성 분리 검토.

---

## 다음 액션

본 문서를 읽은 후 두 가지 경로 중 선택:

1. **항목별 검증** → `01-stack-comparison.md` → `02-architecture-comparison.md` 순서로 차이를 확인
2. **즉시 채택 결정** → `03-adoption-recommendations.md`에서 P0/P1만 골라 작업 단위로 분할

> 📌 본 문서는 정원지기 카카시(tamer)가 1회성 분석으로 작성한 스냅샷이다.
> Origin과 Lite는 살아 있는 코드베이스이므로, 6개월 이상 경과 시 재조사 권장.
>
> **Snapshot timeline**:
> - **2026-04-27 (initial)** — 4-doc 비교 세트 초안 작성 (`harness/logs/tamer/2026-04-27-14-04-...md`)
> - **2026-04-27 (P0-1 deep-dive)** — `02-...#1.2` 가드 11개 + 적응형 일방향 도식 보강, `03-...#P0-1` 3종 세트 발췌 + 거부 항목 명시 (`harness/logs/tamer/2026-04-27-15-00-...md`)
