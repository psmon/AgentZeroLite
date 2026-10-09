# ZeroWearableDevice — AgentZero's watch firmware

*English · [한국어](README-KR.md)*

The firmware half of AgentZero's wearable. The PC half is [`../ZeroWearable`](../ZeroWearable)
(`AgentZeroWearable.exe`, started from the **Wearable** page of either GUI); the two talk over one
BLE link, with AskBot riding it as a real Akka.NET remoting peer. The wire contract is
[`PROTOCOL.md`](PROTOCOL.md).

This is an independent part of the repo: no .NET project references it and it references none.
It builds with ESP-IDF, not `dotnet`.

## The device

| | |
|---|---|
| Board | **Waveshare ESP32-S3-Touch-AMOLED-1.75C** (BSP `waveshare/esp32_s3_touch_amoled_1_75c` 3.x) |
| SoC | ESP32-S3R8 — dual-core Xtensa LX7, **8 MB PSRAM**, 32 MB flash |
| Display | 1.75" round AMOLED, 466×466, CO5300 driver, CST9217 capacitive touch |
| Audio | ES8311 codec + speaker (answers), ES7210 ADC + microphone (questions) |
| Radio | BLE only, used here as a Nordic UART Service peripheral named `claude-hud` |
| Buttons | BOOT (GPIO0) is the only one software sees: power key — screen off/on, hold 3 s to restart |
| UI shell | ESP-Brookesia "phone" launcher (`brookesia_core` from Waveshare's board repo) |

Memory is the constraint. Every app that runs `init()` takes tasks and buffers from internal
DMA-capable RAM, which is what the display, BLE and audio also need. That is why this build
carries **AskBot only** — measured at AskBot's start on the board:

| Build | Free internal DMA heap | Largest block |
|---|---|---|
| all four apps | 98,223 B | 94,208 B |
| AskBot + Settings (this build) | **163,427 B** | **126,976 B** |

## Apps

| App | Component | In this build |
|---|---|---|
| **AskBot** — ask by voice or text, answer on screen and (optionally) spoken on the watch or the PC | `brookesia_app_askbot` | **on** |
| Settings — mic gain, brightness, voice language and speaker, WiFi status | `brookesia_app_settings` | on |
| Chat — the older BLE line-protocol voice chat | `brookesia_app_chat` | off |
| Claude HUD — Claude Code session tiles | `brookesia_app_claude_hud` | off |

**Off is a build option, not a deletion.** `menuconfig` → *AgentZero watch apps* has
`CONFIG_WATCH_APP_CHAT` and `CONFIG_WATCH_APP_CLAUDE_HUD`; this repo's `sdkconfig.defaults`
turns both off. An app that is off is not registered with the launcher, so its `init()` never
runs — for Chat that is three tasks (capture, transmit, playback) never started. Both components
are still compiled, on purpose: AskBot uses the HUD component's BLE transport, Korean font and
mic / voice / power code, and Settings and the BOOT button use the Chat core. With Chat off,
Settings hides the rows that only drive Chat (speaker volume, answer mode, test tone); AskBot's
speaker volume is the build setting `CONFIG_ASKBOT_VOLUME` (100).

AskBot's answer mode is the pill on its screen: tap to cycle text only → spoken on the watch →
spoken on the PC. See *Where the answer is spoken* in `PROTOCOL.md`.

## Layout

```
ZeroWearableDevice/
  firmware/            ESP-IDF project (CMakeLists.txt, main/, components/, sdkconfig.defaults)
    idf-env.ps1        activates the installed toolchain for this shell
    tools/             icon / font generators for the app assets
  akka-client/         the Akka.NET classic-remoting client AskBot compiles in (src/, include/)
  PROTOCOL.md          device <-> host messages
```

Imported from [psmon/Arduino](https://github.com/psmon/Arduino) — `project/samples/claude_hud_amoled`
and `project/samples/akka/cpp` — taking only what this device needs: not the PC-side Python
bridge, not the reference .NET host (that became `ZeroWearable`), not the test harness. The
Arduino repo keeps the other boards and samples.

## Build and flash — with what is already installed

Nothing is downloaded or installed by this project. `idf-env.ps1` points at the toolchain the
Arduino project set up:

| Needed | Where it already is |
|---|---|
| ESP-IDF **v5.5.5** + tools + Python 3.12 venv | `C:\esp\v5.5.5` (installed by EIM) |
| Waveshare board repo (`brookesia_core`, 44 MB) | `C:\esp\ws-amoled-175c` — `WS_AMOLED_REPO` |
| Managed components (LVGL 9.5, BSP, codecs…) | `firmware/managed_components/` — copied locally, git-ignored |

```powershell
cd Project\ZeroWearableDevice\firmware
. .\idf-env.ps1                     # ESP-IDF v5.5.5 | WS_AMOLED_REPO=C:\esp\ws-amoled-175c
idf.py build
idf.py -p COM7 flash monitor        # the watch's USB serial port (VID 303A)
```

Stop the PC host first (Wearable page → Stop): flashing resets the board, and a host holding the
BLE link ends up disagreeing with it. `managed_components/`, `dependencies.lock`, `sdkconfig` and
`build/` are local and git-ignored; on a machine without them the component manager fetches the
locked versions on the first build.
