---
date: 2026-09-18T00:24:00+09:00
agent: code-coach
type: review
mode: execution
trigger: "지금오류 점검해 수정 — 최초연결해 잘사용.. 클라이언트 기기 리부트후 재접속후 발생 - 로그확인 ..스마트 와치 호스트사용"
target: Project/ZeroWearable (M0031 wearable host) — BLE 재접속 경로
engine: (none — 단일 에이전트 레인, creator-rule Rule 2 해당 없음)
---

# 와치 재부팅 후 재접속 시 동작 이상 — 원인 수정 및 로그 확보

## 실행 요약

증상은 "최초 연결은 정상, 클라이언트(와치) 리부트 후 재접속하면 발생". 로그 확인을
먼저 시도했으나 **와치 호스트는 로그 파일을 남긴 적이 없다** — `AgentZeroWearable.exe`의
stdout은 `WearableHostProcess.LineReceived` → `WearablePagePanel.AppendLog` 로 GUI 패널의
인메모리 TextBox 에만 들어간다. 사후 진단이 구조적으로 불가능한 상태였다.

따라서 (1) 관측 가능성을 먼저 확보하고, (2) 코드 정독으로 확증되는 재접속 경로 결함을
수정했다.

## 결과

### 1순위 원인 — 노티피케이션 경계에서 UTF-8 문자 파괴

`BleLink.OnValueChanged` 가 BLE 노티피케이션 **한 개씩** `Encoding.UTF8.GetString(data)` 로
디코딩해 `StringBuilder` 에 붙였다. 한 글자가 두 노티피케이션에 걸치면 양쪽 조각이 각각
U+FFFD 로 디코딩되어 **문자가 영구 소실**된다. 한글은 3바이트라 상시 노출된다.

재부팅 후에만 두드러지는 이유: 잘림 위치를 정하는 것이 협상된 MTU이고, MTU는 링크 수명
간 고정이 아니다. 재부팅 후 재접속에서 247 대신 기본값 23으로 내려오면 청크가 20바이트가
되어 분할 빈도가 "가끔"에서 "한 줄에 여러 번"으로 바뀐다. 깨진 줄은
`JsonDocument.Parse` 에서 탈락한다.

수정: 라인 조립을 **바이트 단위**로 바꾸고, 개행 바이트로 자른 뒤 완성된 줄만 디코딩.
로직을 `Agent.Common.Wearable.LineAssembler`(ZeroCommon)로 분리 — WinRT 의존이 없는
부분이므로 CLAUDE.md의 계층 규칙대로 헤드리스 테스트가 가능한 곳에 둔다.

| 항목 | 위치 |
|---|---|
| 조립기 | `Project/ZeroCommon/Wearable/LineAssembler.cs` (신규) |
| 회귀 테스트 | `Project/ZeroCommon.Tests/LineAssemblerTests.cs` (신규, 9개) |
| 호출부 | `Project/ZeroWearable/Ble/BleLink.cs` |

### 함께 고친 재접속 경로 결함

| # | 파일 | 결함 | 수정 |
|---|---|---|---|
| 2 | `Ble/BleLink.cs` | 세션 Dispose 전에 `MaintainConnection` 을 내리지 않음. 남아 있으면 OS가 링크를 계속 재수립하고, 와치는 미연결 상태에서만 광고하므로 이후 스캔이 계속 빈다 — 실패 경로 주석(190행)이 이미 지적하던 실패 모드인데 정상 disconnect 경로에는 적용돼 있지 않았다 | `ReleaseSession()` 도입, 양쪽 경로에 적용 |
| 3 | `Program.cs` | 재접속이 **스캔 전용**. 스캔에 안 잡히는 것과 범위 밖인 것은 다르다 (OS가 구 링크를 잡고 있으면 광고가 없다) | 스캔이 비면 마지막 성공 주소로 직접 다이얼 (`link.Address` / 신규 `AddressKind`) |
| 4 | `Ble/BleTunnel.cs` | 종료 중인 펌프의 `finally { StopAsync() }` 가 세대 구분 없이 동작 — 그 사이 새 청크가 연 소켓을 방금 올라온 터널째로 닫아버릴 수 있다 | 소켓마다 generation 부여, 자기 세대만 닫음. `_cts` 누수도 정리 |
| 5 | `Program.cs` | keep-alive 루프가 `ConnectAsync` 에 CancellationToken 미전달 — 종료 시 최대 20초 매달림 | `ct` 전달 |
| 6 | `Actors/ChatActor.cs`, `Actors/BleChatProxy.cs` | 링크가 끊겨도 per-device 상태가 남는다. 재부팅한 보드는 request id를 1부터 다시 시작하고 진행 중이던 캡처를 기억하지 못한다 | `DeviceGone` 메시지로 링크 드롭 시 엔트리 폐기. `BeginCapture` 의 MemoryStream 누수도 함께 수정 |

### 관측 가능성

`Project/ZeroWearable/HostLog.cs` (신규) — stdout/stderr 를
`%LOCALAPPDATA%\AgentZeroLite\logs\wearable-host.log` 로 **티(tee)** 한다. 4 MB 롤링, 1세대 보관.

- 파이프 바이트는 그대로 둔다. 패널이 `[host/ready]` 를 **줄 시작**에서, `/error]` 를
  줄 안에서 찾으므로 타임스탬프는 파일 쪽에만 붙인다.
- `Console.Out` 에 걸었기 때문에 **Akka 자체 로거도 파일에 들어간다** — 기기 재부팅
  사후분석에 필요한 remoting association / quarantine 라인이 여기 잡힌다.
- `--log <path>` 로 경로 지정 가능.

## 검증

| 게이트 | 명령 | 결과 |
|---|---|---|
| 와치 호스트 빌드 | `dotnet build Project/ZeroWearable -c Debug` | 오류 0 / 경고 0 |
| 신규 회귀 테스트 | `dotnet test ZeroCommon.Tests --filter ~LineAssemblerTests` | **9 pass / 0 fail** |
| 헤드리스 전체 | `dotnet test Project/ZeroCommon.Tests` | **606 pass / 0 fail / 24 skip** |
| WPF 컴파일 | `dotnet build Project/AgentZeroWpf -t:Compile` | 오류 0 (전체 빌드는 실행 중인 GUI가 DLL 잠금 → 복사 단계만 실패) |
| 로그 파일 실동작 | `AgentZeroWearable.exe --speak … --out …` | 타임스탬프 포함 3줄 기록 확인, stdout 은 변형 없음 |

## 평가

- **워크플로우 개선도: B+** — 증상 재현 없이 코드 정독만으로 1순위 원인을 특정하고 회귀
  테스트로 고정했다. 다만 실기(와치) 재현 검증이 아직 없어 A는 아니다.
- **Claude 스킬 활용도: 2/5** — 전용 스킬 없이 직접 정독·수정. `agentzero-cli` 는 GUI
  터미널용이라 이 프로세스에는 해당 없음.
- **하네스 성숙도: L3 유지** — 와치 호스트(M0031)에 대응하는 전문가가 없어 `code-coach`
  레인으로 처리했다. BLE/링크 계층 지식이 `harness/knowledge/` 에 없는 것이 이번 작업에서
  드러난 공백이다.

### 남은 불확실성 (로그로만 확정 가능)

AskBot(Akka) 앱 경로에서 기기 재부팅 시 **같은 주소·다른 UID** 로 재결합할 때
Akka.Remote 가 구 association 을 quarantine/gate 하는지 여부는 확증하지 못했다. 증거 없이
고치지 않았다. 이번에 추가한 파일 로그에 Akka 로거가 포함되므로, 재현 1회로 판정된다.

## 다음 단계 제안

1. 재현 후 `wearable-host.log` 확보 — `connected: … mtu=` 값이 최초 연결과 재접속에서
   다른지, `tunnel`/Akka association 라인이 무엇을 남기는지 확인
2. MTU가 실제로 떨어지는 것이 확인되면 펌웨어 측 MTU 재협상 요청을 별도 이슈로
3. `harness/knowledge/code-coach/` 에 wearable BLE 링크 계층 문서 신설 검토
   (또는 M0031 전담 전문가 영입 — creator-rule Rule 5-1 에 따라 중복 책임 먼저 확인)
