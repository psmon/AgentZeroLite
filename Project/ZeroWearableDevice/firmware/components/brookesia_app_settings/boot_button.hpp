// BOOT (GPIO0) as the power key: short press toggles the screen, a 3 s hold restarts. See boot_button.cpp.
#pragma once

namespace settings_app {

void startBootButton();   // idempotent; called from the Settings app's install-time init()

} // namespace settings_app
