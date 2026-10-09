#include "device_mic.hpp"

#include <mutex>

#include "bsp/esp-bsp.h"
#include "esp_codec_dev.h"
#include "esp_log.h"
#include "nvs.h"

static const char *TAG = "device_mic";

namespace device_mic {
namespace {

constexpr const char *NVS_NS = "chat";   // the key the Chat app has always used; keep it
constexpr int DEFAULT_GAIN_DB = 30;

std::mutex s_mutex;
esp_codec_dev_handle_t s_handle = nullptr;
int s_holders = 0;
int s_gain = -1;        // -1 = not loaded yet
bool s_everOpened = false;

int loadGainLocked()
{
    if (s_gain >= 0) return s_gain;
    s_gain = DEFAULT_GAIN_DB;

    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READONLY, &nvs) == ESP_OK) {
        uint8_t v = 0;
        if (nvs_get_u8(nvs, "gain", &v) == ESP_OK && v <= 60) s_gain = v;
        nvs_close(nvs);
    }
    return s_gain;
}

}  // namespace

bool acquire()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    if (s_handle) {
        s_holders++;
        return true;
    }

    esp_codec_dev_handle_t h = bsp_audio_codec_microphone_init();
    if (!h) {
        ESP_LOGW(TAG, "bsp_audio_codec_microphone_init failed");
        return false;
    }
    esp_codec_dev_set_in_gain(h, (float)loadGainLocked());

    esp_codec_dev_sample_info_t fs = {};
    fs.sample_rate = SAMPLE_RATE;
    fs.channel = 1;
    fs.bits_per_sample = 16;
    const int rc = esp_codec_dev_open(h, &fs);
    if (rc != ESP_CODEC_DEV_OK) {
        ESP_LOGW(TAG, "codec open failed rc=%d", rc);
        return false;
    }

    s_handle = h;
    s_holders = 1;
    s_everOpened = true;
    ESP_LOGI(TAG, "microphone ready: %d Hz mono 16-bit, gain %d dB", SAMPLE_RATE, s_gain);
    return true;
}

void release()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    if (s_holders <= 0 || !s_handle) return;
    if (--s_holders > 0) return;

    // Close rather than keep it: holding the handle is exactly what stopped a second app
    // from ever recording.
    esp_codec_dev_close(s_handle);
    s_handle = nullptr;
    ESP_LOGI(TAG, "microphone released");
}

bool ok()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    return s_everOpened;
}

bool read(void *buffer, size_t bytes)
{
    esp_codec_dev_handle_t h;
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        h = s_handle;
    }
    if (!h || buffer == nullptr || bytes == 0) return false;
    // esp_codec_dev_read blocks until the I2S DMA has the samples, which is what paces a
    // capture loop.
    return esp_codec_dev_read(h, buffer, (int)bytes) == ESP_CODEC_DEV_OK;
}

int gain()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    return loadGainLocked();
}

void setGain(int db)
{
    db = db < 0 ? 0 : (db > 60 ? 60 : db);
    esp_codec_dev_handle_t h;
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (loadGainLocked() == db) return;
        s_gain = db;
        h = s_handle;
    }
    if (h) esp_codec_dev_set_in_gain(h, (float)db);

    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_u8(nvs, "gain", (uint8_t)db);
        nvs_commit(nvs);
        nvs_close(nvs);
    }
    ESP_LOGI(TAG, "mic gain -> %d dB", db);
}

} // namespace device_mic
