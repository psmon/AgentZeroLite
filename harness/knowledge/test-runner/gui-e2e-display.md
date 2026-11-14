# GUI e2e 화면 배치: Debug GUI는 보조 모니터에서 최대화로

> 운영자 지침 (2026-11-03): AgentZero Debug GUI를 띄워 e2e 검증을 할 때, 보조 모니터가
> 있으면 그 모니터에서 **최대화**로 시작해 진행한다. 운영자가 작업하는 주 화면과 겹치지
> 않게 테스트 화면을 보조 모니터로 떼어 두려는 것이다. 이 지침은 그 다음 e2e부터 적용한다.

## 규칙

1. Debug GUI(`Project/AgentZeroAvalonia/bin/Debug/net10.0/AgentZeroLite.exe`, WPF Debug도 같다)를
   `Start-Process … -PassThru`로 띄우면 **곧바로** 배치 스크립트를 실행한다.

   ```powershell
   $p = Start-Process $exe -PassThru
   & Docs/scripts/place-on-secondary-monitor.ps1 -ProcessId $p.Id
   ```

   - 보조 모니터는 주 모니터가 아닌 것 중 왼쪽부터 첫 번째다. 창을 복원(restore)한 뒤 작업 영역
     안으로 옮기고 최대화한다.
   - 모니터가 하나면 창을 그대로 두고 "single monitor"를 출력한다. 오류가 아니므로 테스트를 계속한다.
2. 키 입력(SendKeys, `os_key_press`)을 보내려면 창을 활성화해야 한다. 활성화는 위치와 무관하게
   동작하지만, 운영자가 주 화면에서 타이핑하던 입력과 섞일 수 있으니 키 입력 단계는 짧게 끝낸다.
3. 스크린샷은 창 핸들 기준(PrintWindow)으로 찍는다. 화면 좌표 캡처는 보조 모니터 좌표(음수 X 등)를
   따로 계산해야 해서 틀리기 쉽다. 캡처하는 프로세스는 `SetProcessDPIAware()`를 먼저 호출해야 한다.
   그러지 않으면 배율이 걸린 모니터에서 창 오른쪽이 잘린 이미지가 나온다(2026-11-03 실측).
4. 검증이 끝나면 띄운 Debug GUI는 직접 종료한다(`Stop-Process -Id $p.Id`). 운영자가 쓰는 Store/릴리스판은
   건드리지 않는다. Debug 빌드는 뮤텍스, CLI 파이프, 데이터 프로필이 따로라서 나란히 떠 있어도 된다.

## 실측 기록

- 2026-11-03: Windows 11, 주 모니터 + `\\.\DISPLAY2`(주 모니터 왼쪽, 3840×2464 작업 영역, X = -3840).
  스크립트가 Debug GUI(pid 40320)를 옮겨 최대화했다. 출력: `maximized on \\.\DISPLAY2 at -3840,0 3840x2464`.
