---
date: 2026-09-18T00:57:00+09:00
agent: device-smith
type: review
mode: execution
trigger: "새로 영입한 기기빌드및 디버거수행후속진행"
target: claude_hud_amoled (ESP-IDF) on COM7 · AgentZeroWearable.exe
---

# device-smith 첫 실전 — 빌드 통과, 그리고 AskBot 첫 ASSOCIATE 가 매번 실패한다

## 실행 요약

영입 직후 실기 검증. 정의된 Step 1~4 를 순서대로 수행했고, **플래시는 하지 않았다**
(기기 상태를 바꾸는 작업이라 사용자 확인 전까지 보류). Step 4 에서 호스트와 기기 로그를
나란히 놓은 결과, 기존에 보이지 않던 결함이 드러났다.

## 결과

### Step 1 — 보드 판별 (VID 기준)

| 포트 | PNPDeviceID | 판정 |
|---|---|---|
| **COM7** | `USB\VID_303A&PID_1001` | ✅ ESP32-S3 네이티브 USB — 우리 보드 |
| COM4 / COM5 | `BTHENUM\…` | 블루투스 가상 포트 — 제외 |

호스트 프로세스 없음, COM 점유 프로세스 없음 → 진행 가능.
**knowledge §1 의 "이름이 아니라 VID" 규칙이 실제로 작동했다** — 이름만 보면 COM4/5/7 이
모두 "직렬 장치"로 보인다.

### Step 2 — 툴체인 판정

`CMakeLists.txt` + `sdkconfig` + `main/` 존재, `.ino` 없음 → **ESP-IDF**.
빌드 캐시(371 MB) / `managed_components`(379 MB) 생존, 마지막 빌드 2026-09-16.

### Step 3 — 빌드

```
. .\idf-env.ps1        →  ESP-IDF v5.5.5 | WS_AMOLED_REPO=C:\esp\ws-amoled-175c
idf.py build           →  Project build complete
```

| 산출물 | 크기 | 여유 |
|---|---|---|
| `claude_hud_amoled.bin` | `0x38a390` (3.54 MB) | 앱 파티션 8 MB 중 **56% free** |
| `bootloader.bin` | `0x5710` | 32% free |

knowledge §3-A 의 명령이 **실측 검증됨**. 문서 수정 필요 없음.

### Step 4 — 호스트 vs 펌웨어 (양쪽 로그 동시 캡처)

호스트를 40초 띄우고 COM7 시리얼을 동시에 읽었다. 시리얼은 **DTR/RTS 를 끄고** 열었다 —
네이티브 USB CDC 에서 켜면 보드가 리셋된다.

```
기기 (COM7)                                        호스트 (wearable-host.log)
────────────────────────────────────────────────   ──────────────────────────────────
                                                   00:57:09.477 connecting to 288485905F92
I ( 8596) hud_ble: MTU 512
I ( 8656) hud_ble: central connected (handle 1)
I ( 8810) askbot_ble: tunnel open, 508 B/notify
I ( 8810) askbot: tcp connected to 127.0.0.1:2552
W ( 9010) askbot_ble: notify blocked for 200 ms,
                      dropping the tunnel          ← ❗
E ( 9010) askbot: sending ASSOCIATE failed         ← ❗
I (10100) hud_ble: TX notify subscribed            ← 구독이 1.3 s 늦게 도착
I (10238) chat_core: host online: ASUS-AI          00:57:11.286 connected: mtu=512
                                                   00:57:11.288 [hud] watch connected;
                                                                no statusLine seen yet
                                                   00:57:11.291 said hello as chat-app
I (14010) askbot_ble: tunnel open (재시도)          00:57:15.246 tunnel open (generation 1)
I (14010) askbot: sent ASSOCIATE … uid=1816285507…
I (14230) askbot: associated with akka.tcp://
                  AskBot@127.0.0.1:2552 uid=403303857  00:57:15.457 said hello as askbot
```

**발견: AskBot 의 첫 ASSOCIATE 는 매번 실패하고, 5초 뒤 재시도에서만 성공한다.**

가장 정합적인 해석: 기기의 AskBot 은 **BLE 링크가 붙자마자**(8656) 터널을 열고
ASSOCIATE 를 보내려 하는데, 센트럴(호스트)의 TX notify 구독은 1.3초 뒤(10100)에야
도착한다. 구독자가 없으니 notify 가 막히고, 200 ms 대기 후 터널을 버린다.
그 1.3초는 호스트 쪽 GATT 서비스 탐색 + characteristic 조회 + CCCD write 에 드는 시간이다.

비용: **접속 1회당 약 5초 지연 + 실패한 ASSOCIATE 1건**. 매 접속마다 재현된다.

### 부수 확인 — HUD 재전송 수정이 의도대로 동작

```
00:57:11.288 [hud] watch connected; no statusLine seen yet, so nothing to show it
```

새로 넣은 `HudActor.LinkUp` 경로가 정확히 발화했고, **콜드 스타트에서는 재전송하지 않는다**는
설계대로 거동했다. 이 경우 시계의 "waiting for sessions..." 는 올바른 화면이다.

## 심각도

| 항목 | 등급 | 근거 |
|---|---|---|
| AskBot 첫 ASSOCIATE 실패 | **Moderate** | 기능은 복구되나 접속마다 5초 손실. 링크가 불안정하면 재시도가 반복될 수 있음 |
| 빌드/툴체인 | Info | 이상 없음 |

## 경계 판정 — 이건 누구 일인가

**펌웨어 쪽**이다. 기기가 구독 완료를 기다리지 않고 송신을 시도하는 순서 문제이므로,
이 정원이 아니라 형제 저장소(`psmon/Arduino`)의 하네스 —
`ble-contract-sentinel` 레인으로 넘긴다. device-smith 정의의 "하지 않는 것" 대로,
여기서 펌웨어를 고치지 않았다.

호스트 쪽에서 1.3초를 줄일 여지는 있으나(`DiscoverNusAsync` 의 전체 열거),
해당 코드의 주석이 `ForUuid` 가 이 시점에 `0x80070016` 으로 실패한다고 명시하고 있어
**증거 없이 순서를 바꾸지 않았다**.

## 평가

- **워크플로우 개선도: A-** — 영입한 절차가 첫 실전에서 신규 결함 1건을 잡았다.
  Step 4(양쪽 로그 동시)가 없었으면 호스트 로그만으로는 "터널이 4초 뒤 열림"까지만 보이고
  이유는 알 수 없었다.
- **Claude 스킬 활용도: 1/5** — 스킬 없이 직접 수행.
- **하네스 성숙도: L3+ 유지** — 기기 레인이 실제로 작동함을 확인.

## 다음 단계 제안

1. **형제 저장소 이슈로 이관** — AskBot 이 TX notify 구독 완료를 기다린 뒤 ASSOCIATE 를
   보내도록. 현재는 링크-업 즉시 시도한다
2. 플래시 사이클은 아직 미수행. 수행하면 호스트 재기동이 **와치 재부팅과 같은 재접속 경로**를
   타므로 2026-09-18 재접속 수정분의 회귀 검증이 같이 된다
3. knowledge §3-A 는 실측 검증 완료. §3-B(arduino-cli 경로)는 해당 보드가 미연결이라
   여전히 문서 기반 — 다음 기회에 검증
