#include "device_voice.hpp"

#include <cstring>
#include <mutex>

#include "esp_log.h"
#include "nvs.h"
#include "nvs_flash.h"

static const char *TAG = "device_voice";

namespace device_voice {
namespace {

constexpr const char *NVS_NS = "voicecfg";

// Input may be left to whisper; output has to name a language, since the synthesiser has
// to pick pronunciation before it has any audio to detect from.
const char *IN_LANGS[] = {"auto", "ko", "en"};
const char *OUT_LANGS[] = {"ko", "en"};
const char *VOICES[] = {"F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5"};

std::mutex s_mutex;
bool s_loaded = false;
char s_inLang[8] = "auto";
char s_outLang[8] = "ko";
char s_voice[8] = "F1";

void loadLocked()
{
    if (s_loaded) return;
    s_loaded = true;

    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READONLY, &nvs) != ESP_OK) return;

    size_t len = sizeof(s_inLang);
    nvs_get_str(nvs, "in", s_inLang, &len);
    len = sizeof(s_outLang);
    nvs_get_str(nvs, "out", s_outLang, &len);
    len = sizeof(s_voice);
    nvs_get_str(nvs, "voice", s_voice, &len);
    nvs_close(nvs);
    ESP_LOGI(TAG, "voice prefs: in=%s out=%s voice=%s", s_inLang, s_outLang, s_voice);
}

void store(const char *key, const char *value)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) != ESP_OK) return;
    nvs_set_str(nvs, key, value);
    nvs_commit(nvs);
    nvs_close(nvs);
}

// Returns the entry after `current`, wrapping; the first entry when it is unknown.
template <size_t N>
const char *next(const char *(&table)[N], const char *current)
{
    for (size_t i = 0; i < N; ++i) {
        if (strcmp(table[i], current) == 0) return table[(i + 1) % N];
    }
    return table[0];
}

}  // namespace

Prefs get()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    return Prefs{s_inLang, s_outLang, s_voice};
}

const char *cycleInLang()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_inLang, sizeof(s_inLang), "%s", next(IN_LANGS, s_inLang));
    store("in", s_inLang);
    return s_inLang;
}

const char *cycleOutLang()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_outLang, sizeof(s_outLang), "%s", next(OUT_LANGS, s_outLang));
    store("out", s_outLang);
    return s_outLang;
}

const char *cycleVoice()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_voice, sizeof(s_voice), "%s", next(VOICES, s_voice));
    store("voice", s_voice);
    return s_voice;
}

namespace {

template <size_t N>
bool known(const char *(&table)[N], const char *value)
{
    if (value == nullptr) return false;
    for (size_t i = 0; i < N; ++i) {
        if (strcmp(table[i], value) == 0) return true;
    }
    return false;
}

}  // namespace

bool setInLang(const char *code)
{
    if (!known(IN_LANGS, code)) return false;
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_inLang, sizeof(s_inLang), "%s", code);
    store("in", s_inLang);
    return true;
}

bool setOutLang(const char *code)
{
    if (!known(OUT_LANGS, code)) return false;
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_outLang, sizeof(s_outLang), "%s", code);
    store("out", s_outLang);
    return true;
}

bool setVoice(const char *id)
{
    if (!known(VOICES, id)) return false;
    std::lock_guard<std::mutex> lock(s_mutex);
    loadLocked();
    snprintf(s_voice, sizeof(s_voice), "%s", id);
    store("voice", s_voice);
    return true;
}

// The device UI is English throughout (the answers themselves are whatever language the
// host was asked for), so the language names are too.
const char *langLabel(const char *code)
{
    if (code == nullptr) return "?";
    if (strcmp(code, "ko") == 0) return "Korean";
    if (strcmp(code, "en") == 0) return "English";
    if (strcmp(code, "auto") == 0) return "auto";
    return code;
}

} // namespace device_voice
