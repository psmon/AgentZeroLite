# Wearable device — build, flash, debug

> 🇰🇷 한국어: [wearable-device.kr.md](wearable-device.kr.md) · ⬅ [README.md](../README.md)

AgentZero Lite talks to a wearable over BLE. **Only the PC half lives in this repo.**
The firmware is a sibling project, and this page is the seam between the two: how to get a
device built, flashed and talking, and how to tell which half is at fault when it isn't.

| Half | Repo | Artifact |
| --- | --- | --- |
| PC host | this repo — `Project/ZeroWearable` | `AgentZeroWearable.exe` |
| Device firmware | [**psmon/Arduino**](https://github.com/psmon/Arduino) | a `.bin` flashed to the board |

> The firmware repo is mostly Arduino-family boards today, but nothing here assumes that —
> §5 is the checklist for adding a device on any toolchain.

---

## 1. Which device are we talking about

The reference device is a **Waveshare ESP32-S3 Touch AMOLED 1.75"** running
`project/samples/claude_hud_amoled`, a single firmware that serves three apps over one BLE
link:

| App on the watch | Protocol | Served by |
| --- | --- | --- |
| **Claude HUD** | `S` / `E` lines | `HudActor` ← `POST /status` · `/event` on :8765 |
| **Chat** | line protocol (`R`/`A` + `0xA5`/`0xA6` frames) | `BleChatProxy` → `ChatActor` |
| **AskBot** | real Akka remoting, PDUs tunnelled as `0xAB` | `BleTunnel` → this host's own `:2552` |

One central can hold the device, which is why one process owns the radio. See
`Project/ZeroWearable/Program.cs` for the full picture.

---

## 2. Find the board — by VID, not by name

Windows lists Bluetooth virtual serial ports as COM too. Picking by name eventually flashes
the wrong port.

```powershell
Get-CimInstance Win32_PnPEntity |
  Where-Object { $_.Name -match 'COM\d+' } |
  Select-Object Name, PNPDeviceID
```

| VID | Bridge | Board |
| --- | --- | --- |
| `VID_303A` | Espressif native USB (CDC/JTAG) | ESP32-S3 AMOLED 1.75" — `claude_hud_amoled` |
| `VID_1A86` | WCH CH343 | ESP32-S3-LCD-1.28 — the Arduino samples |

**COM numbers move** between machines and re-plugs; every number in these docs is a
placeholder. If nothing shows up, suspect a charge-only USB cable first.

---

## 3. Build and flash

Pick the toolchain from the directory, not from memory: `CMakeLists.txt` + `sdkconfig`
means ESP-IDF; a `.ino` means arduino-cli.

**ESP-IDF** — the HUD/Chat/AskBot firmware:

```powershell
cd C:\code\psmon\Arduino\project\samples\claude_hud_amoled
. .\idf-env.ps1                       # pins IDF_TOOLS_PATH, the py3.12 venv, WS_AMOLED_REPO
idf.py set-target esp32s3             # first time only
idf.py -p COM7 build flash monitor
```

**arduino-cli** — the earlier samples:

```powershell
arduino-cli compile --upload -p COM6 `
  --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" `
  project/samples/hello_lcd
arduino-cli monitor -p COM6 -c baudrate=115200
```

Verified toolchain versions, per-sketch FQBNs, the factory-firmware recovery command and the
failure table live in
[`harness/knowledge/device-smith/wearable-device-toolchain.md`](../harness/knowledge/device-smith/wearable-device-toolchain.md).
The authoritative source is the sibling repo's own `CLIBUILD.md` and per-app READMEs.

### Two rules that bite

1. **Stop the PC host before flashing.** Flashing resets the board; if
   `AgentZeroWearable.exe` is holding the BLE link the two halves end up disagreeing. Use
   the GUI's Wearable panel → Stop. (The host is single-instance on
   `Local\AgentZeroLite.WearableHost`; a second one exits with code 5.)
2. **One process per COM port.** A serial monitor left open turns the next flash into
   `Access denied`.

---

## 4. Debugging across the seam

Link problems are never diagnosed from one side. Put both logs next to each other:

| Half | Where |
| --- | --- |
| PC host | `%LOCALAPPDATA%\AgentZeroLite\logs\wearable-host.log` — 4 MB rolling |
| Device | `idf.py -p <COM> monitor` · `arduino-cli monitor -p <COM> -c baudrate=115200` |

The host log is a tee of the process's stdout, installed before anything can fail, so
**Akka's own remoting lines are in it too** — which is what a "the watch rebooted and now
AskBot won't associate" post-mortem needs. Lines worth reading first:

```
[ble/info] connected: claude-hud [288485905F92] mtu=512        ← does the MTU change on reconnect?
[tunnel/info] tunnel open to 127.0.0.1:2552 (generation N)     ← the AskBot path
[.../user/hud] S line, 201 bytes -> watch (S:1 E:1 dropped:0)  ← the HUD path; dropped is the signal
```

Tail it live while you reproduce:

```powershell
Get-Content "$env:LOCALAPPDATA\AgentZeroLite\logs\wearable-host.log" -Wait -Tail 30
```

A useful property of the flash cycle: bringing the host back up after a flash exercises the
**same reconnect path as a watch reboot**, so it doubles as the regression check for it.

---

## 5. Adding a different device

Nothing above is Arduino-specific. To bring in a board on another toolchain, fill in five
slots — and only with commands you actually ran:

1. **How it is identified** — VID/PID, or the non-USB equivalent
2. **Toolchain entry point** — and the directory signature that selects it
3. **build / flash / monitor** — the three commands
4. **Recovery** — how to un-brick it
5. **Failure patterns** — the ones specific to that board

Then add the rows to
[`harness/knowledge/device-smith/wearable-device-toolchain.md`](../harness/knowledge/device-smith/wearable-device-toolchain.md),
which is what the `device-smith` harness agent reads.

---

## 6. Who owns what

| Concern | Owner |
| --- | --- |
| Port discovery, build, flash, monitor, recovery | `device-smith` (this repo's harness) |
| .NET build, installer, version bump | `build-doctor` — **not** device work |
| Firmware code review, BLE wire contract, device resource limits | the sibling repo's own harness (`device-resource-warden`, `ble-contract-sentinel`) |

This repo builds and observes the device. It does not edit the firmware — that source
belongs to the sibling project.
