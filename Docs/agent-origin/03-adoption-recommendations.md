# 03 — 채택 로드맵 (우선순위 + 마이그레이션 비용 + Trade-off)

> 본 문서는 [`01-stack-comparison.md`](./01-stack-comparison.md)와 [`02-architecture-comparison.md`](./02-architecture-comparison.md)에서 도출한 *Origin 우위 항목*을 **실행 가능한 작업 단위**로 분해하고, 각각의 채택 비용과 트레이드오프를 정량화한다.

---

## 우선순위 등급 정의

| 등급 | 정의 | 액션 시점 |
|:---:|---|---|
| **P0** | 안정성·보안에 직결, 즉시 채택 권장 | 다음 sprint |
| **P1** | 사용자 가치 큼, Lite 정체성과 충돌 없음 | 1~2 sprint 내 |
| **P2** | 가치 있으나 우선순위 낮음 (UX 개선 위주) | 분기 단위 |
| **P3** | 선택적 (옵션 패키지) | 백로그 |

비용 등급:

| 등급 | 추정 작업량 |
|:---:|---|
| **S** | 1일 미만 (md/설정/소량 코드) |
| **M** | 2~5일 (한 모듈 이식) |
| **L** | 1주 이상 (구조 변경, 테스트 동반) |

---

## P0 — 즉시 채택 (안정성·보안 직결)

### P0-1: ReActActor 가드 패턴 이식 [비용: M]

**대상 파일**:
- 출처: `D:\Code\AI\AgentWin\Project\ZeroCommon\Actors\ReActActor.cs`
- 적용: `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs`, `ExternalAgentToolLoop.cs`, `Project/ZeroCommon/Actors/AgentReactorActor.cs`

**이식 항목**:
```csharp
// 다음 상수와 검사 로직만 발췌하여 Lite의 tool loop에 추가
public const int DefaultMaxRounds       = 10;
public const int MaxSameCallRepeats     = 3;   // 동일 함수+인자 반복 차단
public const int MaxConsecutiveBlocks   = 3;   // 같은 도구 연속 차단
public const int MaxLlmRetries          = 1;   // transient 에러 재시도
public const int MaxCallsPerRound       = 30;
public const int MaxAiWaitSeconds       = 25;  // 비동기 도구 적응형 cap
public const int MaxAsyncTimeoutRetries = 1;
```

**수행 절차**:
1. `AgentToolLoop`에 위 상수와 `(functionName, argumentsHash)` 쌍 카운터 도입
2. 같은 호출이 `MaxSameCallRepeats`회 이상 → 강제 종료, "loop detected" 반환
3. 라운드 cap 초과 시 명시적 에러 메시지로 LLM에 반환 (조용한 실패 금지)
4. `ZeroCommon.Tests`에 가드 단위 테스트 추가 (mock LLM이 같은 호출 반복)

**Trade-off**: 없음. 안정성 향상.

**채택 사유**: Lite의 tool loop은 LLM의 stop 토큰에 안정성을 위임. 잘못 학습된 모델/프롬프트가 무한 호출 시 보호 없음. Origin 가드는 실전 이슈에서 도출된 값.

---

### P0-2: AppLogger IDE 디버거 분기 패턴 [비용: S]

**대상 파일**: `Project/AgentZeroWpf/Logging/AppLogger.cs` (또는 신규)

**이식 항목**:
- `EnableConsoleOutput()` — `--debug` 플래그 시 콘솔 stdout
- `EnableDebuggerOutput()` — `Debugger.IsAttached` 시 OutputDebugString
- `EnableFileOutput(BaseDirectory)` — 파일 로깅
- 세 채널 동시 활성화 가능

**수행 절차**:
1. Origin `AppLogger` 클래스 통째로 복사
2. `App.OnStartup`에서 분기 호출 추가
3. 기존 `Console.WriteLine`/`Debug.WriteLine` 호출을 `AppLogger`로 대체

**Trade-off**: 코드 라인 수 ~200 증가. 진단성 대비 무시 가능.

**채택 사유**: 현재 Lite의 디버깅은 IDE 콘솔에 한정. CLI 모드 + 외부 콘솔 강제 시 로그가 사라짐.

---

## P1 — 사용자 가치 큼 (1~2 sprint 내)

### P1-1: Speech 입력 (Whisper.net + NAudio) [비용: M, 옵션 빌드: L]

**대상 모듈**: `Project/AgentZeroWpf/Services/Speech/`

**도입 패키지** (Lite 신규):
```
NAudio                    2.2.1   — 마이크 캡처
Whisper.net               1.9.0   — STT 본체
Whisper.net.Runtime       1.9.0   — CPU 런타임 (필수)
Whisper.net.Runtime.Cuda  1.9.0   — GPU 가속 (선택)
```

**수행 절차**:
1. `ISpeechCaptureService` 추상 (`ZeroCommon`에 정의)
2. `WhisperCaptureService` 구현 (`AgentZeroWpf/Services/Speech/`)
3. NAudio VAD (RMS 진폭 임계값) → 음성 시작/종료 감지
4. STT 결과를 `AgentBotActor.UserInput`으로 직접 주입
5. MainWindow에 마이크 버튼 추가 (또는 글로벌 단축키)
6. **인스톨러 분기**: "Voice Pack" 옵션 체크박스 — CUDA runtime + 모델은 옵션
7. 모델은 GitHub Actions에서 별도 ZIP으로 배포 (사이즈 ~50MB)

**Trade-off**:
- **publish 사이즈 +50~150MB** (Whisper 모델 포함 시)
- CUDA runtime 포함 시 +200MB
- **해결**: 인스톨러에서 옵션 분리, 본체는 그대로 유지

**채택 사유**: Origin이 검증한 패턴. CLI 자동화와 결합 시 손 놓고 작업하는 시나리오 가능.

---

### P1-2: CLI 데스크톱 자동화 명령군 [비용: L]

**대상 파일**: `Project/AgentZeroWpf/Cli/Commands/`

**이식 명령**:
| 명령 | 의존 |
|---|---|
| `text-capture` | UI Automation (`UIAutomationClient`) |
| `scroll-capture` | UIA + 화면 합성 |
| `element-tree` | UIA |
| `mouseclick` / `mousemove` / `mousewheel` | `SendInput` Win32 |
| `keypress` | `SendInput` |
| `screenshot` / `capture` | `BitBlt` GDI |
| `get-window-info` / `wininfo-layout` | `GetWindowRect`, `EnumWindows` |
| `dpi` | `GetDpiForWindow` |
| `activate <hwnd>` | `SetForegroundWindow` |
| `copy <text>` | Clipboard (UI 스레드 marshal 필요) |

**수행 절차**:
1. **별도 csproj 모듈로 분리** — `Project/AgentZeroWpf.Automation/`
2. `Project/AgentZeroWpf`에서 `ProjectReference`로 link (옵션)
3. CLI 진입점(`CliHandler.Run`)에서 명령 dispatcher 확장
4. **보안 리뷰** (security-guard 에이전트 통과 필수): Win32 API 호출 표면 확장 → prompt injection 시 마우스 탈취 시나리오 검토
5. ReAct 도구로 노출하기 전에 화이트리스트 정책 정의

**Trade-off**:
- 보안 표면 확대 — Lite의 prompt-injection-aware 정책에 직접 영향
- LLM이 사용자 화면을 "볼 수" 있게 됨 → 데이터 유출 경로
- **완화**: 자동화 명령은 사용자 명시 승인(approval prompt)으로 게이팅, ReAct 도구 등록은 별도 설정 키로 옵트인

**채택 사유**: ReAct 능력의 본질적 확장. Origin은 이를 통해 "에이전트가 화면을 조작하는" 시나리오를 검증.

---

### P1-3: VirtualDesktop API 통합 [비용: S]

**대상 파일**: `Project/ZeroCommon/Services/VirtualDesktop/IVirtualDesktopService.cs`

**이식 항목**:
```csharp
public sealed record VirtualDesktopInfo(Guid Id, string Name, bool IsCurrent);

public interface IVirtualDesktopService
{
    IReadOnlyList<VirtualDesktopInfo> GetDesktops();
    // (Origin에는 Get만 있을 가능성, Lite에서는 SwitchTo 추가 검토)
}
```

**수행 절차**:
1. Origin 파일 그대로 복사 (COM interop)
2. 워크스페이스 ↔ 가상 데스크톱 매핑 옵션 추가 (UI 메뉴)
3. CLI 명령 `vdesktop list` / `vdesktop switch <id>` 노출

**Trade-off**: 거의 없음. Win32 COM이라 추가 NuGet 불필요.

**채택 사유**: 멀티 워크스페이스 ↔ 멀티 가상 데스크톱 자연스러운 매핑. 헤비 사용자 UX 향상.

---

## P2 — 분기 단위 채택 (UX 개선 위주)

### P2-1: 하네스 도메인 전문가 추가 [비용: S — md만 복사]

**채택 대상**: `harness/agents/`에 다음 5개 추가

| 에이전트 | 역할 | 출처 |
|---|---|---|
| `wpf-engineer.md` | XAML/WPF 리뷰 | Origin |
| `conpty-engineer.md` | ConPTY/터미널 통합 리뷰 | Origin |
| `ipc-engineer.md` | WM_COPYDATA/MMF 리뷰 | Origin |
| `llm-engineer.md` | tool-calling/GBNF 리뷰 | Origin |
| `native-efficiency-auditor.md` | DLL/네이티브 메모리 | Origin |

**수행 절차**:
1. Origin `harness/agents/*.md` 5개 파일을 그대로 복사
2. Lite 프로젝트 컨텍스트에 맞게 트리거/예시 조정
3. `harness.config.json`의 `agents` 배열 확장: `["tamer", "security-guard", ..., "wpf-engineer", "conpty-engineer", "ipc-engineer", "llm-engineer", "native-efficiency-auditor"]`
4. 새 엔진 `domain-review` 추가 (5명 동시 호출)

**Trade-off**: 없음. md 파일이라 코드 영향 0.

**채택 사유**: 카카시 하네스의 "정원 풍요화" — Lite 정체성과 100% 부합.

---

### P2-2: BotDockHost Embed/Floating 토글 [비용: M]

**대상 파일**: `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs`

**이식 항목**:
- `BotDockHost` Grid (높이 280, 또는 0)
- AgentBotWindow를 메인 윈도우 내부에 reparent
- `Ctrl+Shift+OemTilde` → embed/undock 토글
- `Ctrl+OemTilde` → 가시성 토글
- `IsBotDocked` DB 영속화 (Lite의 `AppWindowState`에 이미 존재 — 마이그레이션 불필요)

**Trade-off**: WPF reparenting은 까다로운 패턴 (HWND 소유권 처리). 충분한 테스트 필요.

**채택 사유**: 봇 윈도우를 분리/임베드 자유롭게 — 작업 흐름 전환 비용 감소.

---

### P2-3: LogRow 3탭 (Bot/Output/Log) + 자동 스크롤 + 트리밍 [비용: M]

**대상 파일**: `MainWindow.xaml`, `Project/AgentZeroWpf/UI/Logs/LogPanelController.cs`

**이식 항목**:
- 하단 LogRow에 TabControl 3탭
  - Bot — `AgentEventStream` 봇 이벤트
  - Output — 활성 터미널 stdout
  - Log — `AppLogger` 출력
- `Ctrl+Shift+1/2/3` 단축키 전환
- 자동 스크롤 + 100,000자 초과 시 앞 50,000자 제거 (메모리 cap)

**Trade-off**: Lite 정체성 유지. 코드량은 중간.

**채택 사유**: 진단성 대폭 향상. 현재 Lite는 봇 응답과 터미널 출력이 같은 영역.

---

### P2-4: LlmSettings 엔티티 + 자격 증명 영속화 [비용: M]

**대상 파일**: `Project/ZeroCommon/Data/Entities/LlmSettings.cs`

**이식 항목**:
- 1행 싱글톤 엔티티
- 필드: `ActiveProvider`, `ActiveModel`, `MaxTokens`, `Temperature`, `Webnori_ApiKey`, `OpenAI_ApiKey`, `LMStudio_BaseUrl`, `Ollama_BaseUrl` 등
- DPAPI 또는 `ProtectedData.Protect`로 API 키 암호화 (Origin은 평문 추정 — Lite는 보안 강화)

**수행 절차**:
1. 엔티티 정의 (`Project/ZeroCommon/Data/Entities/LlmSettings.cs`)
2. `AppDbContext.LlmSettings` DbSet 추가
3. 새 마이그레이션: `AddLlmSettings.cs`
4. UI: Settings 다이얼로그에 LLM 탭 추가 (기존 코드 분기를 DB 읽기로 대체)

**Trade-off**: 마이그레이션 추가 (Lite의 1개 → 2개). 평문 키 vs DPAPI 정책 결정 필요.

**채택 사유**: 외부 백엔드 4개 + 4종 자격 증명을 코드 분기로 처리 중. UX/보안 양쪽 개선.

---

## P3 — 백로그 (선택적)

### P3-1: TTS (System.Speech) [비용: S]

P1-1과 짝. 봇 응답을 음성으로 재생.

```csharp
using System.Speech.Synthesis;

var synth = new SpeechSynthesizer();
synth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult, 0, CultureInfo.GetCultureInfo("ko-KR"));
synth.SpeakAsync("응답 텍스트");
```

**Trade-off**: Windows 내장이라 추가 NuGet `System.Speech` 1개. 사이즈 영향 미미.

---

### P3-2: 다국어 리소스 분리 (RESX) [비용: L]

현재 Lite는 한·영 하드코딩. RESX로 추출 시 i18n 가능.

**Trade-off**: XAML/code 전체 grep + 치환 작업. ROI 낮음 (현재 사용자가 한국어 중심).

---

### P3-3: ScrapPanel (윈도우 스파이) [비용: L]

P1-2 자동화 명령과 함께 채택. UI 패널 형태로 자동화 결과 시각화.

**Trade-off**: WPF 컴포넌트 추가. P1-2 없이는 의미 없음.

---

### P3-4: 코드 사이닝 적용 [비용: M]

Origin은 `.cer` 보유하지만 CI 미적용. Lite는 `.cer` 미발견.

**수행 절차**:
1. EV 코드 사이닝 인증서 확보 (또는 자체 서명)
2. GitHub Actions secret으로 PFX 등록
3. `signtool sign` 단계 추가 (publish 후, ZIP 생성 전)

**Trade-off**: SmartScreen 경고 감소 / 인증서 비용 ($300~$500/년).

**채택 사유**: 사용자 신뢰 향상. Lite 정식 배포 단계에서 결정.

---

## 적극 거부 (Origin → Lite 회귀 금지)

### REJECT-1: LLamaSharp 제거

**Origin 결정**: Whisper.net과 ggml.dll 심볼 충돌 → LLamaSharp 제거.

**Lite 결정**: self-built llama.dll + custom RID 폴더로 우회.

**유지 사유**:
- Lite의 우회는 동적 로드 시점에 명시적 `NativeLibraryConfig.WithLibrary()` 호출 → Whisper와 공존 가능
- Origin의 결정을 Lite에 적용하면 온디바이스 LLM 핵심 가치 상실
- Speech 채택(P1-1) 시에도 우회 패턴 유지 → 양쪽 보유

### REJECT-2: 31개 마이그레이션 누적

**Origin 상태**: 2026-04-04 ~ 2026-04-21 사이 31개 마이그레이션.

**Lite 결정**: `InitialCreate` 1개로 통합.

**유지 사유**:
- Lite는 분기점에서 깨끗한 출발 — git 히스토리에 마이그레이션 노이즈 없음
- 신규 엔티티(P2-4)는 새 마이그레이션 1개로 추가
- Origin 스키마 그대로 import 시 31개 .cs 파일 + Designer 31개 → 62개 파일 noise

### REJECT-3: NoteWindow / HarnessMonitorWindow / ScrapPanel 통째 채택

**Origin 보유**: 데스크톱 허브 정체성의 풍부한 윈도우 자산.

**Lite 결정**: "경량 다중 셸 + 봇" 단순 정체성.

**유지 사유**:
- Lite의 README.md가 명시한 정체성: *"Windows-native multi-CLI shell with on-device LLM experiments"*
- 채택 시 Origin 회귀 — 차별점 소실
- **부분 채택은 가능** (Markdown/Pencil 미리보기는 이미 Lite가 보유, NoteWindow의 파일 브라우저는 P3로 검토)

---

## 채택 작업 단위 요약

| 우선순위 | 항목 | 비용 | 의존 | 산출물 |
|:---:|---|:---:|---|---|
| **P0-1** | ReActActor 가드 이식 | M | — | `AgentToolLoop` 안정성 가드 + 단위 테스트 |
| **P0-2** | AppLogger IDE 분기 | S | — | `Project/AgentZeroWpf/Logging/AppLogger.cs` |
| **P1-1** | Speech 입력 (Whisper) | M (코어) + L (옵션 빌드) | — | `Speech/` 모듈, "Voice Pack" 인스톨러 옵션 |
| **P1-2** | CLI 데스크톱 자동화 | L | security-guard 검토 | `AgentZeroWpf.Automation` 별도 csproj |
| **P1-3** | VirtualDesktop API | S | — | `Services/VirtualDesktop/` |
| **P2-1** | 하네스 도메인 전문가 5명 | S | — | `harness/agents/*.md` 5개 + 새 엔진 |
| **P2-2** | BotDockHost embed/float | M | P0-2 (선) | MainWindow XAML + reparenting 로직 |
| **P2-3** | LogRow 3탭 | M | P0-2 (선) | XAML + LogPanelController |
| **P2-4** | LlmSettings 엔티티 | M | — | 새 EF 마이그레이션 + Settings UI |
| **P3-1** | TTS | S | P1-1 | `System.Speech` 통합 |
| **P3-2** | RESX 다국어 | L | — | `Resources.resx` |
| **P3-3** | ScrapPanel | L | P1-2 | UI 패널 |
| **P3-4** | 코드 사이닝 | M | EV 인증서 | GHA `signtool` 단계 |

---

## 권장 시퀀스

```
Sprint N    [P0-1, P0-2]                    안정성·진단 즉시 강화
                │
Sprint N+1  [P1-3, P2-1, P2-4]              경량 가치 우선
                │
Sprint N+2  [P1-1]                          Speech 입력 (옵션 빌드)
                │
Sprint N+3  [P1-2, P2-2, P2-3]              자동화 + UX
                │
Backlog     [P3-*]                          선택적
```

**전제**:
- 각 P0/P1 채택 후 `release-build-pipeline` 엔진(security-guard → build-doctor)을 통과
- 채택 결과는 `harness/logs/code-coach/`와 `harness/logs/security-guard/`에 자동 기록
- 마이너 버전 bump 기준은 P1-* 1건 채택 시점

---

## 마지막 한 마디

> **Origin은 데스크톱 허브로 진화했고, Lite는 경량 셸로 분기했다.**
> 두 정체성은 충돌하지 않는다. Origin의 *기능 풍부함*에서 *Lite 정체성과 일치하는 항목만*을 골라
> 옵션 모듈 형태로 흡수하는 것이 본 로드맵의 철학이다.
>
> 카카시 하네스의 정원지기 시각: **Origin은 큰 정원, Lite는 작은 정원**.
> 큰 정원의 좋은 꽃을 꺾어다 작은 정원에 옮겨 심을 수 있지만,
> 큰 정원이 되려고 하지는 말 것.

— 정원지기 카카시, 2026-04-27
