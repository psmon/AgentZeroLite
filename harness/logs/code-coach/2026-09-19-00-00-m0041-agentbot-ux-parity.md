---
date: 2026-09-19T00:00:00+09:00
agent: code-coach
type: review
mode: execution
trigger: "pre-commit-review (staged *.cs / *.axaml)"
---

# M0041 AgentBot UX 파리티 — 커밋 전 리뷰

## 실행 요약

`pre-commit-review` 엔진 규약에 따라 M0041 변경(ZeroCommon 시임 7종 + Avalonia 뷰모델·뷰·셸·CLI·테스트)을
커밋 직전에 리뷰했다. 렌즈: 관용성, **결합/시임 존중**(ZeroCommon은 WPF-free, 액터는 테스트 가능), 실패 모드, 명명.

## 결과

### Must-fix (수정 완료)

1. **`Project/AgentZeroAvalonia/Views/AgentBotWindow.axaml.cs:22` — 종료 시 플로팅 상태 유실.**
   `ShutdownMode.OnMainWindowClose`는 종료할 때 플로팅 봇 창을 대신 닫아 준다. 그런데 그 창의 `Closing` 핸들러가
   이를 **사용자의 재도킹으로 읽고** `e.Cancel = true` 후 `EmbedBot()`을 호출한다 → `BotDocked = true` →
   `SaveBotDocked(true)`. 결과적으로 **플로팅 상태로 종료하면 다음 실행에 도킹으로 돌아온다**. 종료가 막힐 여지도 있다.
   `MainWindow.Closing`에서 `_botWindow.ClosingProgrammatically = true`를 먼저 세우도록 수정
   (`Views/MainWindow.axaml.cs`). 이 값은 공유 컬럼이라 WPF 호스트의 복원에도 영향을 준다.

### Should-fix (수정 완료)

2. **`Project/AgentZeroAvalonia/Views/MainWindow.axaml.cs` — `PropertyChanged` 핸들러 누수.**
   `AttachBot`이 `BotFloatRequested`는 해제하면서 익명 `PropertyChanged` 핸들러는 해제하지 않았다.
   `AgentBotView`에서 같은 부류의 버그(`ItemAdded` 누적)를 이미 고쳐 놓고 여기서 반복한 셈이다.
   명명 메서드 `OnBotVmPropertyChanged`로 바꾸고 짝을 맞춰 해제.

### 확인했고 문제 없음

- **시임 경계**: `ZeroCommon/Agents/*` 7개 파일은 `ITerminalSession`과 BCL만 참조 — `System.Windows`/Avalonia 유입 없음.
  `ZeroCommon.Tests`에서 헤드리스로 51개 통과.
- **`SaveBotDocked`가 단일 컬럼만 쓴다** — 두 호스트가 공유하는 행에 `SaveWindowState`(9컬럼)를 쓰지 않는 것이 핵심.
- **`ApprovalToastViewModel.Delay`를 VM의 `Delay`와 묶지 않은 것은 의도적이다.** 묶으면 테스트의 즉시 시계에서
  카운트다운 10회가 `Show()` 반환 전에 동기적으로 끝나 토스트가 열리자마자 닫힌다. 토스트의 시계는 UI 페이싱,
  VM의 시계는 동작 지연 — 서로 다른 관심사다.
- **`AddSystem` / `AddNotice` 분리**는 WPF가 주석으로 우회한 함정(`AgentBotWindow.xaml.cs:1000`)을 구조로 푼 것.

## 평가

| 축 | 등급 |
|---|---|
| 관용성 (호스트 관례 준수) | A — `Style Selector` + 클래스, `x:DataType`, `DynamicResource`, `Post` 시임 유지 |
| 결합/시임 존중 | A — WPF 무변경 게이트 유지, 공유 로직은 ZeroCommon으로 하향 |
| 실패 모드 | B+ — Must-fix 1건이 리뷰에서 걸렸다. 창 수명주기는 이 호스트에 처음 생긴 개념이라 리뷰 없이는 놓쳤을 것 |
| 명명 | A — `AgentBotWindow`/`AgentBotView`/`ApprovalToast*`가 액터 어휘와 충돌하지 않음 |

## 다음 단계 제안

- 종료-플로팅 왕복은 단위 테스트가 어렵다(창 수명주기). **운영자 스모크 필수 항목**으로 완료로그와
  `macos-smoke.md`에 이미 올려 두었다.
- 280px 도크에서 대화 영역이 170px 남짓 — 미니키/리사이즈 그립 기본 접힘이 다음 후보.
