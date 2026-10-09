// The only physical button this board exposes to software - now the power key.
//
// BSP_CAPS_BUTTONS is 0 on the ESP32-S3-Touch-AMOLED-1.75C: the two keys on the case are BOOT (GPIO0)
// and RESET, and RESET is wired to the chip's reset line, so it can never be read from firmware. That
// leaves one button for everything physical, and the two things that have to work when the screen is
// unusable are screen power and a restart:
//
//   short press  -> screen off / on (device_power, which also stops the touch indev, so a dark screen
//                   cannot be pressed by accident - the button is the only way back)
//   hold 3 s     -> beep, then restart
//
// Volume used to live here (short up, long down). It moved to the touch sliders in the Settings app
// and the HUD's INFO tile, which is where it already was as well - nothing was lost by freeing the key.
#include "boot_button.hpp"

#include "driver/gpio.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "chat_core.hpp"
#include "device_power.hpp"

static const char *TAG = "boot_btn";

namespace settings_app {

static constexpr gpio_num_t PIN = GPIO_NUM_0;   // BOOT, active low, pulled up on the board
static constexpr int POLL_MS = 20;
static constexpr int DEBOUNCE_MS = 40;
static constexpr int REBOOT_MS = 3000;          // long enough that no pocket press reaches it
static constexpr int BEEP_MS = 500;             // the tone is 0.4 s; let it finish before the reset

static bool s_started = false;

static inline uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

static void task(void *)
{
    bool wasDown = false;
    uint32_t downAt = 0;
    bool fired = false;                          // the hold already did its work; ignore the release

    for (;;) {
        bool down = gpio_get_level(PIN) == 0;
        uint32_t t = nowMs();

        if (down && !wasDown) {
            wasDown = true;
            downAt = t;
            fired = false;
        } else if (down && wasDown) {
            if (!fired && t - downAt >= REBOOT_MS) {
                fired = true;
                ESP_LOGW(TAG, "held %d ms: restarting", REBOOT_MS);
                voice_chat::Core::instance().playTestTone();   // audible, because the screen may be off
                vTaskDelay(pdMS_TO_TICKS(BEEP_MS));
                device_power::reboot("BOOT button held 3 s");
            }
        } else if (!down && wasDown) {
            wasDown = false;
            if (!fired && t - downAt >= DEBOUNCE_MS) {
                bool on = device_power::toggleScreen();
                ESP_LOGI(TAG, "short press: screen %s", on ? "on" : "off");
            }
        }
        vTaskDelay(pdMS_TO_TICKS(POLL_MS));
    }
}

void startBootButton()
{
    if (s_started) return;
    s_started = true;

    gpio_config_t cfg = {};
    cfg.pin_bit_mask = 1ULL << PIN;
    cfg.mode = GPIO_MODE_INPUT;
    cfg.pull_up_en = GPIO_PULLUP_ENABLE;
    cfg.pull_down_en = GPIO_PULLDOWN_DISABLE;
    cfg.intr_type = GPIO_INTR_DISABLE;
    if (gpio_config(&cfg) != ESP_OK) { ESP_LOGW(TAG, "gpio_config failed"); return; }

    // Polled rather than interrupt driven: 20 ms is plenty for a power key, and this task must keep
    // running when the LVGL side is paused or stuck - which is exactly when the key is needed.
    xTaskCreatePinnedToCore(task, "boot_btn", 3072, nullptr, 3, nullptr, 1);
    ESP_LOGI(TAG, "BOOT button: short press = screen off/on, hold 3 s = restart");
}

} // namespace settings_app
