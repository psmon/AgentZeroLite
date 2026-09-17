# 웨어러블 기기 툴체인 — USB로 빌드·플래시·관찰

> 이 문서는 **무엇이 올바른가**를 적는다. 발동 문구는 여기 없다 —
> 트리거는 `harness/agents/device-smith.md` frontmatter 가 소유한다 (creator-rule Rule 3).

AgentZero Lite 의 웨어러블 링크는 두 쪽이고, 이 저장소에는 **PC 쪽만** 있다.

| 쪽 | 저장소 | 산출물 |
|---|---|---|
| PC 호스트 | 이 저장소 `Project/ZeroWearable` | `AgentZeroWearable.exe` |
| 기기 펌웨어 | 형제 저장소 [`psmon/Arduino`](https://github.com/psmon/Arduino) — 로컬 `C:\code\psmon\Arduino` | `.bin` (보드에 플래시) |

형제 저장소의 1차 출처: `CLIBUILD.md`(arduino-cli), 각 앱의 `README.md`,
`project/samples/claude_hud_amoled/idf-env.ps1`(ESP-IDF). 이 문서는 그중
**AgentZero 쪽에서 반복적으로 필요한 부분만** 증류한 것이고, 충돌하면 형제 저장소가 우선이다.

---

## 1. 포트 판별 — 이름이 아니라 VID로

Windows 는 블루투스 가상 직렬 포트도 COM 으로 올린다. `board list` 나 포트 이름으로
고르면 엉뚱한 포트에 플래시를 시도하게 된다. **VID 로 판별한다.**

```powershell
Get-CimInstance Win32_PnPEntity |
  Where-Object { $_.Name -match 'COM\d+' } |
  Select-Object Name, PNPDeviceID
```

| VID | 무엇 | 어느 보드 |
|---|---|---|
| `VID_303A` (PID_1001) | Espressif 네이티브 USB (CDC/JTAG) | ESP32-S3 AMOLED 1.75" — **claude_hud_amoled** |
| `VID_1A86` | WCH CH343 USB-직렬 브리지 | ESP32-S3-LCD-1.28 — arduino 샘플 |
| `표준 Bluetooth에서 직렬 링크` | 블루투스 가상 포트 | **아님** — 절대 고르지 말 것 |

**COM 번호는 머신마다·재연결마다 바뀐다.** 문서에 박힌 번호는 전부 자리표시자다.
(실측: 최초 개발 머신 COM6, `SAM` COM3, 현재 AMOLED 보드 **COM7**)

기기가 목록에 없으면 거기서 멈춘다. 가장 흔한 원인은 **충전 전용 USB 케이블**이다.

---

## 2. 툴체인 분기 — 디렉토리가 말해준다

| 보이는 것 | 툴체인 | 진입점 |
|---|---|---|
| `CMakeLists.txt` + `sdkconfig` + `main/` | **ESP-IDF** | `idf.py` |
| `*.ino` (+ 선택적 `sketch.yaml`) | **arduino-cli** | `arduino-cli` |

두 툴체인은 코어·라이브러리 저장소가 다르다. 섞으면 컴파일은 통과하고 런타임에
죽는 식으로 실패하므로, **먼저 판정하고 시작한다.**

`project/samples/` 현재 구성:

| 앱 | 툴체인 | 비고 |
|---|---|---|
| `claude_hud_amoled` | ESP-IDF | Claude HUD + Chat + AskBot — **AgentZero 가 대화하는 그 펌웨어** |
| `hello_lcd`, `ble_lcd`, `ble_pc`, `selfcheck`, `claude_hud` | arduino-cli | 초기 샘플 |
| `akka`, `amoled_chat_host` | (PC측 코드) | `ZeroWearable` 의 원본 |

---

## 3-A. ESP-IDF 경로 (claude_hud_amoled)

검증 환경: **ESP-IDF v5.5.5** (`C:\esp\v5.5.5`), Python 3.12 venv.

```powershell
cd C:\code\psmon\Arduino\project\samples\claude_hud_amoled
. .\idf-env.ps1                       # IDF_TOOLS_PATH / venv 고정 + export.ps1
idf.py set-target esp32s3             # 최초 1회만
idf.py -p COM7 build flash monitor    # COM 번호는 §1 로 확인한 값
```

- `idf-env.ps1` 이 `WS_AMOLED_REPO=C:\esp\ws-amoled-175c` 를 잡는다. Waveshare 보드
  저장소의 `brookesia_core`(44 MB)를 **복사하지 않고** `EXTRA_COMPONENT_DIRS` 로 참조하기
  때문에, 이 경로가 없으면 빌드가 컴포넌트 미해결로 실패한다.
- BSP(`waveshare/esp32_s3_touch_amoled_1_75c`), LVGL 9.5 등은 첫 빌드 때 컴포넌트
  매니저가 받는다 (`managed_components/`, git-ignore). 최초 1회는 느리다.
- 모니터 종료: `Ctrl+]`

**공장 펌웨어 복구** — 벽돌화가 의심될 때:
```powershell
esptool --port COM7 write_flash 0x0 `
  C:\esp\ws-amoled-175c\Firmware\ESP32-S3-Touch-AMOLED-1.75C-FactoryOnly-260114.bin
```

---

## 3-B. arduino-cli 경로 (초기 샘플)

검증 환경: **arduino-cli 1.5.2** (`C:\Users\psmon\tools\arduino-cli.exe`),
esp32 코어 3.3.11. 데이터 폴더를 Arduino IDE 와 공유하므로 IDE로 설치한 코어·라이브러리를
그대로 쓴다.

보드 옵션은 **FQBN 으로** 넘긴다 (보드: Waveshare ESP32-S3-LCD-1.28):

| 스케치 | FQBN |
|---|---|
| `hello_lcd` | `esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M` |
| `ble_lcd` | `esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app,CDCOnBoot=cdc` |

```powershell
arduino-cli compile --fqbn "<FQBN>" project/samples/hello_lcd          # 빌드만
arduino-cli compile --upload -p COM6 --fqbn "<FQBN>" project/samples/hello_lcd
arduino-cli monitor -p COM6 -c baudrate=115200
```

`sketch.yaml` 프로파일(`--fqbn` 없이 빌드)도 있으나, 인덱스에서 라이브러리를 새로 받기
때문에 로컬 설치본과 버전이 어긋날 수 있다. **일상 개발은 `--fqbn` 방식**이 빠르고 확실하다.

---

## 4. 호스트와 기기를 동시에 다룰 때의 규칙

플래시는 기기를 리셋시킨다. PC 호스트가 BLE 링크를 잡고 있으면 양쪽 상태가 어긋난다.

1. **플래시 전에 호스트를 내린다** — GUI 의 Wearable 패널 Stop, 또는
   `AgentZeroWearable.exe` 종료. 호스트는 단일 인스턴스 뮤텍스
   (`Local\AgentZeroLite.WearableHost`) 를 쓰므로, 두 개가 뜨면 뒤엣것이 exit 5 로 죽는다.
2. **COM 포트는 한 프로세스만** — 모니터를 띄운 채 플래시하면 Access denied.
   `arduino-cli monitor` / IDE Serial Monitor / `idf.py monitor` 중 하나만.
3. 플래시 후 호스트를 다시 올리면 재연결 경로를 그대로 탄다 — 기기 재부팅과 같은 경로이므로,
   재접속 회귀를 검증하기 좋은 순간이다.

---

## 5. 양쪽 로그를 나란히

링크 이상을 한쪽 로그만 보고 단정하지 않는다.

| 쪽 | 어디 |
|---|---|
| PC 호스트 | `%LOCALAPPDATA%\AgentZeroLite\logs\wearable-host.log` (`HostLog`, 4 MB 롤링) |
| 기기 | `idf.py -p <COM> monitor` 또는 `arduino-cli monitor -p <COM> -c baudrate=115200` |

호스트 로그에서 먼저 볼 줄:

```
[ble/info] connected: claude-hud [288485905F92] mtu=512     ← MTU. 재접속 때 값이 바뀌는지
[tunnel/info] tunnel open to 127.0.0.1:2552 (generation N)  ← AskBot(Akka) 경로
... hud] S line, N bytes -> watch (S:.. E:.. dropped:..)     ← HUD 경로. dropped 가 핵심
```

`Console.Out` 에 tee 하므로 **Akka 자체 로그(remoting association/quarantine)도 이 파일에
들어간다.**

---

## 6. 실패 패턴

| 증상 | 원인 | 조치 |
|---|---|---|
| 업로드가 `Connecting...` 에서 멈춤 | 보드가 다운로드 모드 아님 | BOOT 누른 채 RESET → BOOT 떼고 재시도 |
| `Access denied` on COM | 다른 프로세스가 포트 점유 | 모니터/IDE/브리지 먼저 종료 |
| ESP-IDF 빌드가 컴포넌트 미해결 | `WS_AMOLED_REPO` 미설정 | `. .\idf-env.ps1` 를 **먼저** |
| 코어/라이브러리 안 보임 | 인덱스 미갱신 | `arduino-cli core update-index` / `lib update-index` |
| 기기가 COM 목록에 없음 | 충전 전용 케이블 / 드라이버 | 데이터 케이블로 교체 후 §1 재확인 |
| 호스트가 exit 5 | 다른 호스트가 이미 실행 중 | 패널에서 Stop 후 재시도 |
| HUD 가 계속 "waiting for sessions..." | 링크는 정상, 보여줄 status 가 없음 | 호스트 로그의 `dropped:` 확인 — 0 이면 정상 동작 |

---

## 7. 비-Arduino 기기로 넓힐 때

이 문서는 ESP32 두 보드로 시작했지만 구조는 보드 중립이다. 새 기기를 들일 때 채울 칸:

1. **포트/전송 판별** — VID/PID, 또는 USB가 아니면 그 기기의 식별 방법 (§1 표에 행 추가)
2. **툴체인 진입점** — 디렉토리로 판별되는 규칙 (§2 표에 행 추가)
3. **빌드/플래시/모니터 3종 명령** — 실제로 돌려서 검증된 것만 (§3 에 절 추가)
4. **복구 경로** — 벽돌화 시 되돌리는 방법
5. **실패 패턴** — 그 기기 고유의 것 (§6 에 행 추가)

기기 종류가 늘어나면 §3 을 기기별 파일로 분리한다. 그 전까지는 한 파일이 낫다 —
분기 기준(§2)이 한눈에 보이는 것이 개별 명령보다 중요하다.
