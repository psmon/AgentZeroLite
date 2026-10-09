// Screen power and restart as a device service.
//
// This board has no power switch a program can reach: no PMIC on the I2C bus, no soft-power latch,
// so "off" in the sense of "the chip stops" only happens when the battery is disconnected. What the
// firmware can do is put the panel out - brightness 0 plus a paused LVGL worker - and restart the
// chip. That pair is what the single physical button drives (see brookesia_app_settings/boot_button.cpp).
//
// It lives in this component rather than in Settings for the same reason device_mic does: Settings
// depends on chat depends on this one, so anything every app may need has to sit at the bottom.
#pragma once

namespace device_power {

// The brightness the screen returns to when it comes back on, 0..100. Every app that moves a
// brightness slider should go through here instead of calling bsp_display_brightness_set(), so the
// value survives a screen-off cycle and the two sliders (Settings, HUD) agree. While the screen is
// off the value is only recorded - applying it would light the panel.
void setBrightness(int percent);
int  brightness();

bool screenOn();

// Off: brightness 0, then the LVGL worker is paused - which also stops the touch indev being polled,
// so nothing on the dark screen can be pressed by accident. On: worker resumed, panel content
// redrawn, brightness restored. Both are idempotent and safe to call from any task.
void setScreen(bool on);
bool toggleScreen();        // returns the new state

// Logs the reason, lets the log drain, then esp_restart(). Runs from whatever task calls it, so it
// still works when the UI is wedged - the physical RESET key remains the only cure for a scheduler
// that has stopped.
[[noreturn]] void reboot(const char *reason);

} // namespace device_power
