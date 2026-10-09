// Device-wide WiFi station service.
//
// Lives in the Settings app because WiFi is a property of the device, not of one
// app: it is started at boot (Settings::init runs at install time), the
// credentials are stored in NVS so they survive a reflash, and any app can ask
// for the current address. AskBot is the first consumer; the BLE apps ignore it.
//
// Credentials come from NVS, falling back to the Kconfig defaults on a fresh
// device. There is no on-screen keyboard on this panel, so the intended way to
// change them is over the existing BLE link rather than by typing on the watch.
#pragma once

#include <cstdint>
#include <string>

namespace device_wifi {

enum class State : uint8_t { Off, Connecting, Connected, Failed };

struct Status {
    State state = State::Off;
    std::string ssid;
    std::string ip;       // empty until a lease arrives
    int8_t rssi = 0;
    int retries = 0;
};

// Idempotent. Brings up station mode with the stored credentials and keeps
// retrying in the background; returns immediately.
void start();

// True when an SSID is configured at all (NVS or Kconfig default).
bool configured();

Status status();

// Blocks until an address is assigned, or the timeout expires. Returns the
// address, or an empty string.
std::string waitForIp(int timeout_ms);

// Stores credentials in NVS and reconnects. Empty ssid clears them.
bool setCredentials(const char *ssid, const char *password);

} // namespace device_wifi
