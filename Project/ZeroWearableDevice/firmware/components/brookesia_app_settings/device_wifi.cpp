#include "device_wifi.hpp"

#include <cstring>
#include <mutex>

#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/task.h"
#include "nvs.h"
#include "nvs_flash.h"

static const char *TAG = "device_wifi";

namespace device_wifi {
namespace {

constexpr const char *NVS_NS = "netcfg";
constexpr int BIT_GOT_IP = BIT0;
constexpr int BIT_FAILED = BIT1;
constexpr int MAX_RETRY = 10;

std::mutex s_mutex;
// Separate from s_mutex and held for the whole of start(): app install order is not
// guaranteed, and AskBot's link task calls start() through waitForIp() at the same
// moment the Settings app calls it at boot. Two concurrent calls created the default
// station netif twice and esp_netif_create_default_wifi_sta asserted, which put the
// device in a boot loop.
std::mutex s_startMutex;
Status s_status;
EventGroupHandle_t s_events = nullptr;
esp_netif_t *s_netif = nullptr;
bool s_started = false;
int s_retries = 0;

struct Creds {
    std::string ssid;
    std::string password;
};

Creds loadCreds()
{
    Creds c{CONFIG_DEVICE_WIFI_SSID, CONFIG_DEVICE_WIFI_PASSWORD};

    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READONLY, &nvs) != ESP_OK) return c;

    char buf[80];
    size_t len = sizeof(buf);
    if (nvs_get_str(nvs, "ssid", buf, &len) == ESP_OK && buf[0]) {
        c.ssid.assign(buf);
        len = sizeof(buf);
        c.password = nvs_get_str(nvs, "pass", buf, &len) == ESP_OK ? std::string(buf) : std::string();
    }
    nvs_close(nvs);
    return c;
}

void setState(State state)
{
    std::lock_guard<std::mutex> lock(s_mutex);
    s_status.state = state;
    s_status.retries = s_retries;
}

void onEvent(void *, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
        return;
    }
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        {
            std::lock_guard<std::mutex> lock(s_mutex);
            s_status.ip.clear();
        }
        if (s_retries < MAX_RETRY) {
            s_retries++;
            setState(State::Connecting);
            ESP_LOGW(TAG, "disconnected, retry %d/%d", s_retries, MAX_RETRY);
            esp_wifi_connect();
        } else {
            setState(State::Failed);
            xEventGroupSetBits(s_events, BIT_FAILED);
        }
        return;
    }
    if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        auto *event = static_cast<ip_event_got_ip_t *>(data);
        char ip[16];
        snprintf(ip, sizeof(ip), IPSTR, IP2STR(&event->ip_info.ip));
        s_retries = 0;
        {
            std::lock_guard<std::mutex> lock(s_mutex);
            s_status.ip.assign(ip);
            s_status.state = State::Connected;
            s_status.retries = 0;
        }
        ESP_LOGI(TAG, "connected, ip %s", ip);
        xEventGroupSetBits(s_events, BIT_GOT_IP);
    }
}

void applyCreds(const Creds &c)
{
    wifi_config_t cfg = {};
    snprintf((char *)cfg.sta.ssid, sizeof(cfg.sta.ssid), "%s", c.ssid.c_str());
    snprintf((char *)cfg.sta.password, sizeof(cfg.sta.password), "%s", c.password.c_str());
    esp_wifi_set_config(WIFI_IF_STA, &cfg);

    std::lock_guard<std::mutex> lock(s_mutex);
    s_status.ssid = c.ssid;
}

}  // namespace

bool configured()
{
    return !loadCreds().ssid.empty();
}

void start()
{
    std::lock_guard<std::mutex> guard(s_startMutex);
    if (s_started) return;

    const Creds creds = loadCreds();
    if (creds.ssid.empty()) {
        ESP_LOGI(TAG, "no SSID configured - WiFi stays off");
        setState(State::Off);
        return;
    }

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        nvs_flash_init();
    }

    s_events = xEventGroupCreate();
    esp_netif_init();
    err = esp_event_loop_create_default();
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
        ESP_LOGW(TAG, "event loop: %s", esp_err_to_name(err));
    }

    s_started = true;   // from here on this function must never run twice
    s_netif = esp_netif_create_default_wifi_sta();
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    if (esp_wifi_init(&init) != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_init failed");
        setState(State::Failed);
        return;
    }

    esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &onEvent, nullptr, nullptr);
    esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &onEvent, nullptr, nullptr);

    esp_wifi_set_mode(WIFI_MODE_STA);
    applyCreds(creds);
    // WiFi shares the radio with NimBLE (software coexistence is enabled), and the
    // BLE apps must keep working, so nothing here touches the BT stack.
    setState(State::Connecting);
    if (esp_wifi_start() != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_start failed");
        setState(State::Failed);
        return;
    }
    ESP_LOGI(TAG, "station mode up, joining '%s'", creds.ssid.c_str());
}

Status status()
{
    Status copy;
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        copy = s_status;
    }
    if (copy.state == State::Connected) {
        wifi_ap_record_t ap;
        if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) copy.rssi = ap.rssi;
    }
    return copy;
}

std::string waitForIp(int timeout_ms)
{
    start();
    if (!s_events) return {};

    const int64_t deadline = esp_timer_get_time() / 1000 + timeout_ms;
    while (esp_timer_get_time() / 1000 < deadline) {
        {
            std::lock_guard<std::mutex> lock(s_mutex);
            if (!s_status.ip.empty()) return s_status.ip;
        }
        xEventGroupWaitBits(s_events, BIT_GOT_IP | BIT_FAILED, pdFALSE, pdFALSE, pdMS_TO_TICKS(500));
    }
    std::lock_guard<std::mutex> lock(s_mutex);
    return s_status.ip;
}

bool setCredentials(const char *ssid, const char *password)
{
    std::lock_guard<std::mutex> guard(s_startMutex);
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) != ESP_OK) return false;

    if (ssid == nullptr || !*ssid) {
        nvs_erase_key(nvs, "ssid");
        nvs_erase_key(nvs, "pass");
    } else {
        nvs_set_str(nvs, "ssid", ssid);
        nvs_set_str(nvs, "pass", password ? password : "");
    }
    nvs_commit(nvs);
    nvs_close(nvs);

    if (!s_started) {
        // start() takes the same guard, so release it first.
        s_startMutex.unlock();
        start();
        s_startMutex.lock();
        return true;
    }
    // Already up: re-apply and let the driver reconnect.
    const Creds creds = loadCreds();
    if (creds.ssid.empty()) {
        esp_wifi_disconnect();
        setState(State::Off);
        return true;
    }
    applyCreds(creds);
    s_retries = 0;
    setState(State::Connecting);
    esp_wifi_disconnect();
    esp_wifi_connect();
    return true;
}

} // namespace device_wifi
