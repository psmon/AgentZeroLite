// Single transport: BLE Nordic UART Service (NUS). Feeds State::handleLine() for the HUD's "S"/"E" lines and
// hands every other tag ("A"/"H"/"C"…) to the registered line hook (the voice-chat app). Never touches LVGL.
// If BLE fails to start, a warning is logged and the HUD simply stays empty (no fallback transports).
#pragma once
#include <cstddef>
#include <cstdint>

namespace claude_hud {

void startBle();                                   // advertise as "claude-hud"; idempotent (apps call it from init())

bool bleConnected();                               // a central is connected
bool bleSubscribed();                              // ...and it has enabled TX notifications
int  bleMaxPayload();                              // bytes per notification = negotiated MTU - 3 (>= 20)

// bleConnected() alone is not enough to send on. A central is connected as soon as the link
// is up, but it only subscribes to TX after it has discovered the service and written the
// CCCD - on Windows that is well over a second later. Anything that notifies during that
// window fails, so a sender that waits on bleConnected() will burn its first attempt.

// Device -> host. One notification of up to bleMaxPayload() bytes. False when not connected, not subscribed,
// or the controller has no free buffers right now (caller may retry after a short delay).
bool bleNotify(const uint8_t *data, size_t len);

// Device -> host text line ("R {json}"); '\n' is appended and the line is split over several notifications.
bool bleSendLine(const char *line, size_t len);

// Inbound lines whose tag is not 'S'/'E'. Called from the NimBLE host task — keep it short, no LVGL.
typedef bool (*LineHook)(const char *line, size_t len);
void setLineHook(LineHook hook);

// Inbound binary writes (first byte 0xA6 = host speech frame, 0xAB = Akka tunnel
// chunk). Same task, same rules. Several apps may register: hooks are tried in order
// until one returns true, so each must claim only its own magic.
typedef bool (*FrameHook)(const uint8_t *data, size_t len);
void setFrameHook(FrameHook hook);

} // namespace claude_hud
