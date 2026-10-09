#include "device_power.hpp"

#include "bsp/esp-bsp.h"
#include "esp_lv_adapter.h"
#include "esp_log.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lvgl.h"

static const char *TAG = "device_power";

namespace device_power {

static int  s_percent = 100;     // what the screen goes back to; bsp_display_brightness_get() rounds
                                 // through 0..255 and would lose a point per off/on cycle
static bool s_on = true;

// Serialises the off/on pair: the button task and an app's slider can both arrive here.
static SemaphoreHandle_t lock()
{
    static SemaphoreHandle_t m = xSemaphoreCreateMutex();
    return m;
}

struct Guard {
    Guard()  { SemaphoreHandle_t m = lock(); if (m) xSemaphoreTake(m, portMAX_DELAY); }
    ~Guard() { SemaphoreHandle_t m = lock(); if (m) xSemaphoreGive(m); }
};

void setBrightness(int percent)
{
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;

    Guard g;
    s_percent = percent;
    if (s_on) bsp_display_brightness_set(percent);
}

int brightness() { return s_percent; }

bool screenOn() { return s_on; }

void setScreen(bool on)
{
    Guard g;
    if (on == s_on) return;
    s_on = on;

    if (!on) {
        // Blank first: if the pause below times out the screen is at least dark, whereas pausing
        // first and failing to blank would leave it lit.
        bsp_display_brightness_set(0);
        esp_err_t err = esp_lv_adapter_pause(1000);
        if (err != ESP_OK) {
            ESP_LOGW(TAG, "LVGL pause failed (%s): screen is dark but still rendering",
                     esp_err_to_name(err));
        }
        ESP_LOGI(TAG, "screen off (returns to %d%%)", s_percent);
        return;
    }

    esp_lv_adapter_resume();
    // The panel kept the last frame, so this is only insurance against widgets that other tasks
    // changed under the lock while the worker was stopped.
    if (bsp_display_lock(200) == ESP_OK) {
        lv_obj_invalidate(lv_scr_act());
        bsp_display_unlock();
    }
    bsp_display_brightness_set(s_percent);
    ESP_LOGI(TAG, "screen on at %d%%", s_percent);
}

bool toggleScreen()
{
    bool next = !s_on;
    setScreen(next);
    return next;
}

void reboot(const char *reason)
{
    ESP_LOGW(TAG, "restarting: %s", reason ? reason : "no reason given");
    vTaskDelay(pdMS_TO_TICKS(150));      // let the UART drain so the reason survives the reset
    esp_restart();
}

} // namespace device_power
