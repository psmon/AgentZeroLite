---
date: 2026-05-10T07:51+09:00
agent: code-coach
type: research
mode: log-eval
trigger: "CLI 블락현상이 또 발생했습니다. 원인 조사"
---

# CLI 블락 현상 — 재발 RCA + 수정 제안

## 한 줄 요약

CLI → GUI IPC가 `SendMessageW`(타임아웃 없음) 위에 올라가 있어, **GUI UI 스레드가
잠깐만 바빠도 CLI는 무한 블록**된다. 이것이 "또" 재발하는 진짜 이유다 — 회귀가
아니라 영구적 설계 결함이다.

## 증상

`AgentZeroLite.exe -cli <command>` 또는 `AgentZeroLite.ps1 <command>` 호출 시
프로세스가 종료되지 않고 멈춤. PowerShell wrapper는 `Start-Process … -Wait`
이므로 PS 셸까지 같이 멈춤. 사용자가 보는 "CLI 블락 현상".

## 코드 경로 추적

CLI 한 호출의 IPC 단계:

```
CLI 프로세스                                   GUI 프로세스 (WPF UI 스레드)
─────────────────────────────────────────────────────────────────────
LocateAgentZeroWindow()    ──hwnd──▶
SendMessageCopyData()  ════ BLOCK ════▶ WndProc(WM_COPYDATA)
       │                                  └─ HandleCliCommand(json)
       │                                       └─ HandleTerminalSend
       │                                            └─ session.WriteAndSubmit  ← UI 스레드 위에서 동기 실행
       │                                            └─ IpcMemoryMappedResponseWriter.WriteJson
       │ ◀══ SendMessage 반환 ════════════ WndProc returns
TryReadMmf (300ms 폴링, 5s 타임아웃) ◀──MMF──
Console.WriteLine(...)
```

핵심:

1. `Project/AgentZeroWpf/CliHandler.cs:99` — `NativeMethods.SendMessageCopyData`
   는 `NativeMethods.cs:384-385`에서 `SendMessageW`로 P/Invoke. **타임아웃 없음.**
   `SendMessage`는 수신 스레드의 메시지 펌프가 메시지를 디스패치하고 `WndProc`이
   반환할 때까지 호출 스레드를 블록한다. WPF UI 스레드가 응답하지 않으면 영원히.

2. `CliHandler._timeoutMs = 5000` 은 `TryReadMmf` 폴링에만 적용
   (`CliHandler.cs:117-138`). WM_COPYDATA 송신 단계에는 영향이 없다.
   사용자가 보는 블록은 **MMF 폴링이 시작되기도 전에** 발생.

3. `MainWindow.WndProc → HandleCliCommand → HandleTerminalSend` 는 UI 스레드
   위에서 `session.WriteAndSubmit(text)` 를 동기 실행
   (`MainWindow.xaml.cs:546`). `WriteAndSubmit` → `Write` → `_pty.WriteToTerm`
   는 ConPTY 입력 파이프에 동기 `WriteFile` 을 건다. 자식 프로세스가 paused/
   stuck이고 파이프 버퍼가 꽉 차 있으면 **WriteFile이 블록 → WndProc이 반환
   못 함 → CLI의 SendMessage가 무한 블록**.

4. UI 스레드를 다른 작업이 점유 중일 때도 같은 결과:
   - LLamaSharp 모델 첫 토큰 / GBNF 컴파일
   - 모달 다이얼로그 (DispatcherUnhandledException → MessageBox.Show)
   - WebView2 navigation, AvalonDock layout pass
   - COM 마샬링 (UI Automation 콜이 UI 스레드 회신을 기다리는 경우)

## 왜 "또" 재발하는가

회귀가 아니다. 동기 `SendMessage` 디자인 자체가 결함이다:
- GUI가 잠깐 바쁘다 → CLI 블록.
- GUI가 항상 바쁘지 않다 → 사용자는 "오늘은 잘 되네" → 다음에 또 블록 → "또".

`harness/logs/code-coach/` 의 과거 로그와 git log 모두 이 IPC 핫패스에 대한
타임아웃 도입 이력이 없다. 따라서 v0.x 전 구간에서 잠재적으로 재현 가능.

## 후순위 후보 (이번 주증상 외 알아둘 것)

- `OsControlService.TextCapture` (`OsControlService.cs:158-181`) 가
  `ElementTreeScanner.Scan` 을 **STA 마샬링 없이 직접** 호출. 동일 코드
  영역의 `ElementTreeAsync`(`:109`) 는 명시적으로 STA 스레드로 마샬링하면서
  주석으로 "System.Windows.Automation requires an STA thread"라고 못박아 둠.
  CLI 메인 스레드는 WinExe STA라 단독 호출 시엔 통과하지만, GUI 안에서
  LLM 툴벨트 경로로 호출되면 MTA 스레드풀에서 부르게 되어 COM 데드락 가능.
  지금 CLI 블록과는 별개의 후속 이슈.

- `OsCliCommands.ElementTree` 는 `.GetAwaiter().GetResult()` 사용. 자체로는
  CLI 프로세스의 메인 STA에서 SyncContext 없으니 데드락 위험은 낮지만,
  대상 hwnd 가 응답하지 않는 창이면 **UIA 자체가 분 단위로 블록 가능**
  (UIA에는 디폴트 타임아웃 없음). `os element-tree` 호출자가 무한 대기로
  보일 수 있으므로 `--timeout` 도입 검토.

## 수정 옵션

### Option A — `SendMessageTimeout` 으로 교체 (추천, 최소 변경)

`NativeMethods.cs` 에 추가:

```csharp
[LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
public static partial IntPtr SendMessageTimeoutCopyData(
    IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
    uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

public const uint SMTO_NORMAL       = 0x0000;
public const uint SMTO_ABORTIFHUNG  = 0x0002;
public const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;
```

`CliHandler.SendWpfCommand` 교체:

```csharp
private static bool SendWpfCommand(IntPtr agentWnd, string jsonCommand)
{
    byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonCommand);
    var gch = GCHandle.Alloc(jsonBytes, GCHandleType.Pinned);
    try
    {
        var cds = new NativeMethods.COPYDATASTRUCT
        {
            dwData = (IntPtr)0x414C,
            cbData = jsonBytes.Length,
            lpData = gch.AddrOfPinnedObject(),
        };
        var rc = NativeMethods.SendMessageTimeoutCopyData(
            agentWnd, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref cds,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
            uTimeout: 3000,
            out _);
        if (rc == IntPtr.Zero)
        {
            Console.Error.WriteLine(
                "Error: AgentZero GUI unresponsive (WM_COPYDATA timed out after 3000ms). " +
                "Try again, or check the log panel for a stuck operation.");
            return false;
        }
        return true;
    }
    finally
    {
        gch.Free();
    }
}
```

호출자는 `SendWpfCommand` 가 false면 일찍 반환하도록 한 줄씩 수정 (현재는 항상
true).

장점:
- 핫픽스 규모 (10줄대)
- 기존 happy-path 동작/지연/응답 형식 변화 없음
- 무한 블록 클래스 자체를 제거

단점:
- "GUI는 살아있고 단지 느릴 뿐" 시나리오에서 3초 컷 → 사용자에게 재시도 요구
  (그러나 현재의 무한 블록보다 객관적으로 낫다)

### Option B — 핸들러 본체를 UI 스레드 밖으로 (구조 개선)

`MainWindow.HandleTerminalSend` / `HandleTerminalKey` / `HandleTerminalRead`
를 `Task.Run` 으로 backgrounding하고 MMF 응답 쓰기를 그 안에서. WndProc은
즉시 반환. CLI는 지금처럼 MMF 폴링.

장점:
- IPC 라운드트립이 PTY 헬스에서 디커플
- 향후 `bot-chat` 등 다른 핸들러로 확산 쉬움

단점:
- 더 큰 diff (4-5 핸들러)
- A 만큼 즉효 아님 — A 적용 후 점진 적용 권장

### Option C — Named pipe 풀 IPC 교체 (장기)

`\\.\pipe\AgentZeroLite_Cli` duplex pipe로 WM_COPYDATA + MMF 둘 다 대체.

장점: 폴링·블록 양쪽 동시 해결, 정렬·타임아웃·취소 모두 자연스러움.
단점: 큰 리팩터 — `os` CLI는 in-process라 영향 없지만 `terminal-*`,
`bot-chat`, `status`, `copy` 모두 마이그레이션 필요. 별도 미션 가치.

### 진단 보강 (옵션과 무관, 같이 들어가도 좋음)

`CliHandler.Run` 시작에 stopwatch를 두고, `--verbose` 또는 `--timing` 플래그
지정 시 각 단계 (Locate → SendMsg → ReadMmf → 출력) 의 elapsed ms를
stderr로 찍기. 다음 사용자 리포트가 들어오면 "어디에서 멈췄는지"가 즉시 보인다.

## 추천 시퀀스

1. **즉시**: Option A 패치 + 진단 stderr 라인. 핫픽스로 한 커밋.
2. **다음 mission**: Option B 를 `terminal-send`/`terminal-key` 부터 시작 —
   ConPTY write가 이번 RCA의 secondary 의심 경로이기 때문.
3. **장기**: M0014 OS-control이 안정화되면 Option C 검토. Named pipe로 가면
   `--no-wait`/`--timeout` 플래그도 더 자연스러운 형태로 재정의 가능.

## 평가 (code-coach Mode 3 rubric)

| 축 | 측정 | 결과 |
|---|---|---|
| Cross-stack judgment | Win32 IPC + Akka/UI thread + ConPTY 3 영역 | A |
| Actionability | file:line + 구체 패치 스니펫 | A |
| Research depth | A/B/C tradeoff 동시 제시 | A |
| Knowledge capture | 본 로그 + (Option A 머지 시) PR description 인용 | Pass (예상) |
| Issue handoff | Suggestion ≥ 1 → 별도 GitHub 이슈 필요 | **Pending** — operator 승인 후 `gh issue create` |

## 다음 단계 제안

- [ ] operator 승인 시 Option A 패치 작성 (10줄대) → 빌드 확인 → commit
- [ ] `gh issue create` — `code-coach review: CLI WM_COPYDATA infinite-block —
      1 Should-fix`, label `bug`
- [ ] 후속 미션 티켓 — Option B (`terminal-*` 핸들러 backgrounding)
- [ ] 후속 미션 티켓 — `os element-tree` UIA 타임아웃 추가
