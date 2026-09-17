# 웨어러블 기기 — 빌드 · 플래시 · 디버그

> 🇺🇸 English: [wearable-device.md](wearable-device.md) · ⬅ [README-KR.md](../README-KR.md)

AgentZero Lite는 웨어러블과 BLE로 대화합니다. **이 저장소에는 PC 쪽 절반만** 있습니다.
펌웨어는 별도 프로젝트이고, 이 문서는 두 절반의 이음매입니다 — 기기를 빌드해서 올리고
말이 통하게 만드는 방법, 그리고 말이 안 통할 때 **어느 쪽 잘못인지 가르는 방법**.

| 절반 | 저장소 | 산출물 |
| --- | --- | --- |
| PC 호스트 | 이 저장소 — `Project/ZeroWearable` | `AgentZeroWearable.exe` |
| 기기 펌웨어 | [**psmon/Arduino**](https://github.com/psmon/Arduino) | 보드에 플래시하는 `.bin` |

> 펌웨어 저장소는 현재 아두이노 계열 보드 위주지만, 이 문서의 구조는 그것을 전제하지
> 않습니다 — §5가 **계열 무관**하게 새 기기를 들이는 체크리스트입니다.

---

## 1. 어떤 기기인가

기준 기기는 **Waveshare ESP32-S3 Touch AMOLED 1.75"** 이고,
`project/samples/claude_hud_amoled` 펌웨어 하나가 **단일 BLE 링크로 세 앱을 동시에**
서비스합니다.

| 시계 쪽 앱 | 프로토콜 | 호스트 쪽 담당 |
| --- | --- | --- |
| **Claude HUD** | `S` / `E` 라인 | `HudActor` ← :8765 의 `POST /status` · `/event` |
| **Chat** | 라인 프로토콜 (`R`/`A` + `0xA5`/`0xA6` 프레임) | `BleChatProxy` → `ChatActor` |
| **AskBot** | 진짜 Akka 리모팅, PDU를 `0xAB` 로 터널링 | `BleTunnel` → 호스트 자신의 `:2552` |

센트럴 하나가 기기를 점유하므로 **한 프로세스가 라디오를 소유**합니다. 전체 그림은
`Project/ZeroWearable/Program.cs` 에 있습니다.

---

## 2. 보드 찾기 — 이름이 아니라 VID로

Windows는 블루투스 가상 직렬 포트도 COM으로 올립니다. 이름으로 고르면 언젠가 엉뚱한
포트에 플래시하게 됩니다.

```powershell
Get-CimInstance Win32_PnPEntity |
  Where-Object { $_.Name -match 'COM\d+' } |
  Select-Object Name, PNPDeviceID
```

| VID | 브리지 | 보드 |
| --- | --- | --- |
| `VID_303A` | Espressif 네이티브 USB (CDC/JTAG) | ESP32-S3 AMOLED 1.75" — `claude_hud_amoled` |
| `VID_1A86` | WCH CH343 | ESP32-S3-LCD-1.28 — 아두이노 샘플 |

**COM 번호는 머신마다, 재연결마다 바뀝니다.** 문서에 박힌 번호는 전부 자리표시자입니다.
아무것도 안 보이면 **충전 전용 USB 케이블**을 가장 먼저 의심하세요.

---

## 3. 빌드와 플래시

툴체인은 기억이 아니라 **디렉토리로** 고릅니다. `CMakeLists.txt` + `sdkconfig` 면 ESP-IDF,
`.ino` 면 arduino-cli 입니다.

**ESP-IDF** — HUD/Chat/AskBot 펌웨어:

```powershell
cd C:\code\psmon\Arduino\project\samples\claude_hud_amoled
. .\idf-env.ps1                       # IDF_TOOLS_PATH, py3.12 venv, WS_AMOLED_REPO 고정
idf.py set-target esp32s3             # 최초 1회만
idf.py -p COM7 build flash monitor
```

**arduino-cli** — 초기 샘플:

```powershell
arduino-cli compile --upload -p COM6 `
  --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" `
  project/samples/hello_lcd
arduino-cli monitor -p COM6 -c baudrate=115200
```

검증된 툴체인 버전, 스케치별 FQBN, 공장 펌웨어 복구 명령, 실패 패턴 표는
[`harness/knowledge/device-smith/wearable-device-toolchain.md`](../harness/knowledge/device-smith/wearable-device-toolchain.md)
에 있습니다. 1차 출처는 언제나 형제 저장소의 `CLIBUILD.md` 와 각 앱 README 입니다.

### 물리는 규칙 두 개

1. **플래시 전에 PC 호스트를 내립니다.** 플래시는 보드를 리셋시키는데,
   `AgentZeroWearable.exe` 가 BLE 링크를 잡고 있으면 양쪽 상태가 어긋납니다.
   GUI의 Wearable 패널 → Stop. (호스트는 `Local\AgentZeroLite.WearableHost` 뮤텍스로
   단일 인스턴스라, 두 번째는 exit 5 로 죽습니다.)
2. **COM 포트는 한 프로세스만.** 시리얼 모니터를 띄워둔 채 플래시하면 `Access denied` 입니다.

---

## 4. 이음매를 사이에 둔 디버깅

링크 문제는 한쪽 로그만 보고 판정하지 않습니다. **양쪽을 나란히** 놓으세요.

| 절반 | 어디 |
| --- | --- |
| PC 호스트 | `%LOCALAPPDATA%\AgentZeroLite\logs\wearable-host.log` — 4 MB 롤링 |
| 기기 | `idf.py -p <COM> monitor` · `arduino-cli monitor -p <COM> -c baudrate=115200` |

호스트 로그는 프로세스 stdout을 tee한 것이고 **아무것도 실패하기 전에** 설치되므로,
**Akka 자체 리모팅 로그도 여기 들어옵니다** — "시계가 재부팅했더니 AskBot이 재결합을
못 한다" 류의 사후분석에 필요한 바로 그 줄입니다. 먼저 볼 줄:

```
[ble/info] connected: claude-hud [288485905F92] mtu=512        ← 재접속 때 MTU가 바뀌는가?
[tunnel/info] tunnel open to 127.0.0.1:2552 (generation N)     ← AskBot 경로
[.../user/hud] S line, 201 bytes -> watch (S:1 E:1 dropped:0)  ← HUD 경로. dropped 가 신호
```

재현하면서 실시간으로 보려면:

```powershell
Get-Content "$env:LOCALAPPDATA\AgentZeroLite\logs\wearable-host.log" -Wait -Tail 30
```

플래시 사이클의 유용한 성질 하나: 플래시 후 호스트를 다시 올리면 **시계 재부팅과 똑같은
재접속 경로**를 타므로, 그 자체가 재접속 회귀 검증이 됩니다.

---

## 5. 다른 기기를 들일 때

위 내용 중 아두이노 전용인 것은 없습니다. 다른 툴체인의 보드를 들이려면 다섯 칸을
채우면 되고, **실제로 돌려본 명령만** 적습니다:

1. **식별 방법** — VID/PID, USB가 아니면 그에 해당하는 것
2. **툴체인 진입점** — 그리고 그것을 고르게 해주는 디렉토리 표식
3. **build / flash / monitor** — 세 명령
4. **복구** — 벽돌화를 되돌리는 방법
5. **실패 패턴** — 그 보드 고유의 것

그다음 `device-smith` 하네스 에이전트가 읽는
[`harness/knowledge/device-smith/wearable-device-toolchain.md`](../harness/knowledge/device-smith/wearable-device-toolchain.md)
에 행을 추가합니다.

---

## 6. 누가 무엇을 맡는가

| 관심사 | 담당 |
| --- | --- |
| 포트 판별, 빌드, 플래시, 모니터, 복구 | `device-smith` (이 저장소 하네스) |
| .NET 빌드, 설치 프로그램, 버전 범프 | `build-doctor` — 기기 작업 **아님** |
| 펌웨어 코드 리뷰, BLE 와이어 계약, 기기 리소스 한계 | 형제 저장소의 자체 하네스 (`device-resource-warden`, `ble-contract-sentinel`) |

이 저장소는 기기를 **빌드하고 관찰**합니다. 펌웨어를 수정하지는 않습니다 — 그 소스는
형제 프로젝트의 것입니다.
