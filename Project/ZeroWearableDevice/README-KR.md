# ZeroWearableDevice — AgentZero 워치 펌웨어

*[English](README.md) · 한국어*

AgentZero 웨어러블의 펌웨어 쪽 절반입니다. PC 쪽 절반은 [`../ZeroWearable`](../ZeroWearable)
(`AgentZeroWearable.exe`, 두 GUI 어느 쪽이든 **Wearable** 페이지에서 시작)이고, 둘은 BLE 링크 하나로
이야기하며 AskBot은 그 위에서 진짜 Akka.NET remoting 피어로 동작합니다. 통신 규약은
[`PROTOCOL.md`](PROTOCOL.md)입니다.

저장소 안의 독립된 파트입니다. 어떤 .NET 프로젝트도 이것을 참조하지 않고, 이것도 아무것도 참조하지
않습니다. `dotnet`이 아니라 ESP-IDF로 빌드합니다.

## 기기

| | |
|---|---|
| 보드 | **Waveshare ESP32-S3-Touch-AMOLED-1.75C** (BSP `waveshare/esp32_s3_touch_amoled_1_75c` 3.x) |
| SoC | ESP32-S3R8 — 듀얼코어 Xtensa LX7, **PSRAM 8 MB**, 플래시 32 MB |
| 디스플레이 | 1.75" 원형 AMOLED, 466×466, CO5300 드라이버, CST9217 정전식 터치 |
| 오디오 | ES8311 코덱 + 스피커(답변), ES7210 ADC + 마이크(질문) |
| 무선 | BLE 전용, 여기서는 `claude-hud`라는 이름의 Nordic UART Service 주변기기 |
| 버튼 | 소프트웨어가 보는 것은 BOOT(GPIO0) 하나: 전원 키 — 화면 끄기/켜기, 3초 누르면 재시작 |
| UI 셸 | ESP-Brookesia "phone" 런처 (Waveshare 보드 저장소의 `brookesia_core`) |

제약은 메모리입니다. `init()`을 실행하는 앱마다 내부 DMA 가능 RAM에서 태스크와 버퍼를 가져가는데,
디스플레이·BLE·오디오도 같은 RAM이 필요합니다. 그래서 이 빌드는 **AskBot만** 싣습니다 — 보드에서
AskBot 시작 시점에 잰 값:

| 빌드 | 내부 DMA 힙 여유 | 가장 큰 블록 |
|---|---|---|
| 앱 4개 전부 | 98,223 B | 94,208 B |
| AskBot + Settings (이 빌드) | **163,427 B** | **126,976 B** |

## 앱

| 앱 | 컴포넌트 | 이 빌드 |
|---|---|---|
| **AskBot** — 음성이나 텍스트로 묻고, 답은 화면에, (선택) 워치나 PC에서 음성으로 | `brookesia_app_askbot` | **켬** |
| Settings — 마이크 게인, 밝기, 음성 언어와 화자, WiFi 상태 | `brookesia_app_settings` | 켬 |
| Chat — 예전 BLE 라인 프로토콜 음성 채팅 | `brookesia_app_chat` | 끔 |
| Claude HUD — Claude Code 세션 타일 | `brookesia_app_claude_hud` | 끔 |

**끔은 빌드 옵션이지 삭제가 아닙니다.** `menuconfig` → *AgentZero watch apps*에
`CONFIG_WATCH_APP_CHAT`과 `CONFIG_WATCH_APP_CLAUDE_HUD`가 있고, 이 저장소의 `sdkconfig.defaults`가
둘 다 끕니다. 꺼진 앱은 런처에 등록되지 않아 `init()`이 실행되지 않습니다 — Chat의 경우 태스크
3개(캡처·송신·재생)가 시작되지 않습니다. 두 컴포넌트는 일부러 계속 컴파일합니다: AskBot이 HUD
컴포넌트의 BLE 전송, 한글 폰트, 마이크·음성·전원 코드를 쓰고, Settings와 BOOT 버튼이 Chat 코어를
씁니다. Chat이 꺼지면 Settings는 Chat만 움직이던 행(스피커 볼륨, 답변 모드, 테스트 톤)을 숨깁니다.
AskBot의 스피커 볼륨은 빌드 설정 `CONFIG_ASKBOT_VOLUME`(100)입니다.

AskBot의 답변 모드는 화면의 버튼입니다: 누를 때마다 텍스트만 → 워치에서 음성 → PC에서 음성 순으로
바뀝니다. `PROTOCOL.md`의 *Where the answer is spoken* 참고.

## 구성

```
ZeroWearableDevice/
  firmware/            ESP-IDF 프로젝트 (CMakeLists.txt, main/, components/, sdkconfig.defaults)
    idf-env.ps1        현재 셸에서 설치된 툴체인을 활성화
    tools/             앱 에셋용 아이콘 / 폰트 생성기
  akka-client/         AskBot이 함께 컴파일하는 Akka.NET classic remoting 클라이언트 (src/, include/)
  PROTOCOL.md          기기 <-> 호스트 메시지
```

[psmon/Arduino](https://github.com/psmon/Arduino)의 `project/samples/claude_hud_amoled`와
`project/samples/akka/cpp`에서 이 기기에 필요한 만큼만 가져왔습니다: PC 쪽 파이썬 브리지, 참조용 .NET
호스트(`ZeroWearable`이 됨), 테스트 하네스는 가져오지 않았습니다. 다른 보드와 샘플은 Arduino
저장소에 그대로 있습니다.

## 빌드와 플래시 — 이미 설치된 것으로

이 프로젝트는 아무것도 내려받거나 설치하지 않습니다. `idf-env.ps1`은 Arduino 프로젝트가 설치해 둔
툴체인을 가리킵니다:

| 필요한 것 | 이미 있는 곳 |
|---|---|
| ESP-IDF **v5.5.5** + 도구 + Python 3.12 venv | `C:\esp\v5.5.5` (EIM이 설치) |
| Waveshare 보드 저장소 (`brookesia_core`, 44 MB) | `C:\esp\ws-amoled-175c` — `WS_AMOLED_REPO` |
| 관리형 컴포넌트 (LVGL 9.5, BSP, 코덱…) | `firmware/managed_components/` — 로컬에 복사, git 제외 |

```powershell
cd Project\ZeroWearableDevice\firmware
. .\idf-env.ps1                     # ESP-IDF v5.5.5 | WS_AMOLED_REPO=C:\esp\ws-amoled-175c
idf.py build
idf.py -p COM7 flash monitor        # 워치의 USB 시리얼 포트 (VID 303A)
```

먼저 PC 호스트를 멈추세요(Wearable 페이지 → Stop): 플래시는 보드를 리셋하고, BLE 링크를 잡고 있던
호스트는 보드와 상태가 어긋납니다. `managed_components/`, `dependencies.lock`, `sdkconfig`,
`build/`는 로컬 전용이며 git에서 제외됩니다. 이것들이 없는 PC에서는 첫 빌드 때 컴포넌트 매니저가
잠긴 버전을 받아 옵니다.
