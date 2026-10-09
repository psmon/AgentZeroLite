#include <cstdio>
#include <cstring>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:Settings"
#include "esp_lib_utils.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "device_wifi.hpp"
#include "device_voice.hpp"
#include "device_power.hpp"
#include "bsp/esp-bsp.h"
#include "settings_app.hpp"
#include "boot_button.hpp"
#include "chat_core.hpp"

#define APP_NAME "Settings"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using voice_chat::AnswerMode;
using voice_chat::Core;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_settings_112_112);
LV_FONT_DECLARE(font_nanum_18);          // shared with the other apps

namespace esp_brookesia::apps {

static const uint32_t C_BG     = 0x000000;
static const uint32_t C_WHITE  = 0xFFFFFF;
static const uint32_t C_GRAY   = 0x8A8F98;
static const uint32_t C_DIM    = 0x232733;
static const uint32_t C_BLUE   = 0x4C8DFF;
static const uint32_t C_PURPLE = 0xA78BFA;
static const uint32_t C_PURPLE_BG = 0x2A2440;

// The round 466x466 panel: usable half-width at a given y is sqrt(233^2 - (y-233)^2). A 300 px column
// between y=72 and y=400 stays inside it at both ends.
static constexpr int W_COL = 300, Y_COL = 72, H_COL = 328;
static constexpr int W_SLIDER = 176, D_STEP = 40;

static constexpr int BRIGHT_MIN = 10, BRIGHT_MAX = 100, BRIGHT_DEFAULT = 80;
static constexpr const char *NVS_NS = "settings";

static int s_brightness = BRIGHT_DEFAULT;

static void loadBrightness()
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READONLY, &nvs) == ESP_OK) {
        uint8_t v = 0;
        if (nvs_get_u8(nvs, "bright", &v) == ESP_OK && v >= BRIGHT_MIN && v <= BRIGHT_MAX) s_brightness = v;
        nvs_close(nvs);
    }
}

static void applyBrightness(int v)
{
    if (v < BRIGHT_MIN) v = BRIGHT_MIN;
    if (v > BRIGHT_MAX) v = BRIGHT_MAX;
    if (v == s_brightness) return;
    s_brightness = v;
    device_power::setBrightness(v);   // the screen service owns the live value, so an
                                     // off/on cycle comes back to exactly this number
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_u8(nvs, "bright", (uint8_t)v);
        nvs_commit(nvs);
        nvs_close(nvs);
    }
}

Settings *Settings::_instance = nullptr;

Settings *Settings::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) {
        _instance = new (std::nothrow) Settings(use_status_bar, use_navigation_bar);
        ESP_UTILS_CHECK_NULL_RETURN(_instance, nullptr, "Create instance failed");
    }
    return _instance;
}

Settings::Settings(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_settings_112_112, true, use_status_bar, use_navigation_bar)
{
}

Settings::~Settings() {}

bool Settings::init(void)
{
    // Runs at install time, i.e. at boot: restore the saved brightness even if nobody opens this app.
    loadBrightness();
    device_power::setBrightness(s_brightness);
    settings_app::startBootButton();
    // WiFi is a device-wide service, not AskBot's: bring it up at boot so any app
    // finds a live address, and so the BLE apps are unaffected either way.
    device_wifi::start();
    ESP_UTILS_LOGI("Init: brightness restored to %d%%, BOOT button armed (power key), wifi %s", s_brightness,
                   device_wifi::configured() ? "starting" : "off (no SSID)");
    return true;
}

bool Settings::run(void)
{
    buildUi(lv_scr_act());
    _seen = 0;
    refresh();
    _timer = lv_timer_create(timerCb, 200, this);
    return true;
}

bool Settings::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

bool Settings::close(void)
{
    _timer = nullptr;
    return true;
}

void Settings::timerCb(lv_timer_t *t)
{
    auto *self = (Settings *)lv_timer_get_user_data(t);
    if (self) self->refresh();
}

// ---------------------------------------------------------------- events
// While a slider is being dragged, refresh() must not fight the finger, so every handler marks the app
// dirty and refresh() skips the control the user is touching.
void Settings::volSlider(lv_event_t *e)
{
    auto *sl = (lv_obj_t *)lv_event_get_target(e);
    Core::instance().setVolume((int)lv_slider_get_value(sl));
    if (lv_event_get_code(e) == LV_EVENT_RELEASED) Core::instance().playTestTone();
}

void Settings::volStep(lv_event_t *e)
{
    int d = (int)(intptr_t)lv_event_get_user_data(e);
    Core &c = Core::instance();
    c.setVolume(c.volume() + d * 5);
    c.playTestTone();
}

void Settings::gainSlider(lv_event_t *e)
{
    auto *sl = (lv_obj_t *)lv_event_get_target(e);
    Core::instance().setMicGain((int)lv_slider_get_value(sl));
}

void Settings::gainStep(lv_event_t *e)
{
    int d = (int)(intptr_t)lv_event_get_user_data(e);
    Core &c = Core::instance();
    c.setMicGain(c.micGain() + d * 5);
}

void Settings::brightSlider(lv_event_t *e)
{
    auto *sl = (lv_obj_t *)lv_event_get_target(e);
    applyBrightness((int)lv_slider_get_value(sl));
}

void Settings::brightStep(lv_event_t *e)
{
    int d = (int)(intptr_t)lv_event_get_user_data(e);
    applyBrightness(s_brightness + d * 10);
}

void Settings::modeEvent(lv_event_t *)
{
    Core &c = Core::instance();
    c.setMode(c.mode() == AnswerMode::TextAndVoice ? AnswerMode::TextOnly : AnswerMode::TextAndVoice);
}

void Settings::testEvent(lv_event_t *) { Core::instance().playTestTone(); }

// Three separate settings, because they are three separate decisions: what language the
// host should expect to hear, what language it should answer in, and which of SuperTonic's
// ten voices says it. Tapping cycles; there is no keyboard on this panel.
void Settings::inLangEvent(lv_event_t *e)
{
    auto *self = (Settings *)lv_event_get_user_data(e);
    device_voice::cycleInLang();
    if (self) self->refresh();
}

void Settings::outLangEvent(lv_event_t *e)
{
    auto *self = (Settings *)lv_event_get_user_data(e);
    device_voice::cycleOutLang();
    if (self) self->refresh();
}

void Settings::voiceEvent(lv_event_t *e)
{
    auto *self = (Settings *)lv_event_get_user_data(e);
    device_voice::cycleVoice();
    if (self) self->refresh();
}

// ---------------------------------------------------------------- UI
static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    return l;
}

static lv_obj_t *mkStepBtn(lv_obj_t *parent, const char *sym, int delta, lv_event_cb_t cb)
{
    lv_obj_t *b = lv_button_create(parent);
    lv_obj_set_size(b, D_STEP, D_STEP);
    lv_obj_set_style_radius(b, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(C_BLUE), LV_STATE_PRESSED);
    lv_obj_set_style_shadow_width(b, 0, 0);
    lv_obj_set_style_pad_all(b, 0, 0);
    lv_obj_add_event_cb(b, cb, LV_EVENT_CLICKED, (void *)(intptr_t)delta);
    lv_obj_center(mkLabel(b, sym, &lv_font_montserrat_20, C_WHITE));
    return b;
}

Settings::Row Settings::addRow(lv_obj_t *parent, const char *title, const char *icon,
                               int min, int max, int step, lv_event_cb_t onSlider, lv_event_cb_t onStep)
{
    Row r;
    lv_obj_t *card = lv_obj_create(parent);
    lv_obj_set_size(card, W_COL - 8, 92);
    lv_obj_set_style_bg_color(card, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_bg_opa(card, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(card, 0, 0);
    lv_obj_set_style_radius(card, 16, 0);
    lv_obj_set_style_pad_all(card, 10, 0);
    lv_obj_remove_flag(card, LV_OBJ_FLAG_SCROLLABLE);

    char head[64];
    snprintf(head, sizeof(head), "%s  %s", icon, title);
    lv_obj_t *t = mkLabel(card, head, &font_nanum_18, C_GRAY);
    lv_obj_align(t, LV_ALIGN_TOP_LEFT, 0, 0);

    r.value = mkLabel(card, "-", &font_nanum_18, C_WHITE);
    lv_obj_align(r.value, LV_ALIGN_TOP_RIGHT, 0, 0);

    r.minus = mkStepBtn(card, LV_SYMBOL_MINUS, -1, onStep);
    lv_obj_align(r.minus, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    r.plus = mkStepBtn(card, LV_SYMBOL_PLUS, +1, onStep);
    lv_obj_align(r.plus, LV_ALIGN_BOTTOM_RIGHT, 0, 0);

    r.slider = lv_slider_create(card);
    lv_obj_set_size(r.slider, W_SLIDER, 10);
    lv_obj_align(r.slider, LV_ALIGN_BOTTOM_MID, 0, -14);
    lv_slider_set_range(r.slider, min, max);
    lv_obj_set_style_bg_color(r.slider, lv_color_hex(0x11141B), LV_PART_MAIN);
    lv_obj_set_style_bg_color(r.slider, lv_color_hex(C_BLUE), LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(r.slider, lv_color_hex(C_WHITE), LV_PART_KNOB);
    lv_obj_set_ext_click_area(r.slider, 16);           // easier to grab with a fingertip
    lv_obj_add_event_cb(r.slider, onSlider, LV_EVENT_VALUE_CHANGED, nullptr);
    lv_obj_add_event_cb(r.slider, onSlider, LV_EVENT_RELEASED, nullptr);
    (void)step;
    return r;
}

// One tappable row that shows "label: value"; the caller keeps the label object and writes
// the text in refresh().
static lv_obj_t *mkVoiceRow(lv_obj_t *parent, lv_event_cb_t cb, void *user)
{
    lv_obj_t *b = lv_button_create(parent);
    lv_obj_set_size(b, W_COL - 8, 42);
    lv_obj_set_style_radius(b, 14, 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_shadow_width(b, 0, 0);
    lv_obj_add_event_cb(b, cb, LV_EVENT_CLICKED, user);
    lv_obj_t *l = mkLabel(b, "", &font_nanum_18, C_WHITE);
    lv_obj_center(l);
    return l;
}

void Settings::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    lv_obj_t *title = mkLabel(scr, "Settings", &font_nanum_18, C_WHITE);
    lv_obj_align(title, LV_ALIGN_TOP_MID, 0, 36);

    lv_obj_t *col = lv_obj_create(scr);
    lv_obj_set_size(col, W_COL, H_COL);
    lv_obj_align(col, LV_ALIGN_TOP_MID, 0, Y_COL);
    lv_obj_set_style_bg_opa(col, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(col, 0, 0);
    lv_obj_set_style_pad_all(col, 0, 0);
    lv_obj_set_style_pad_row(col, 10, 0);
    lv_obj_set_flex_flow(col, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(col, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_scroll_dir(col, LV_DIR_VER);
    lv_obj_set_scrollbar_mode(col, LV_SCROLLBAR_MODE_AUTO);

    _vol    = addRow(col, "Speaker volume", LV_SYMBOL_VOLUME_MAX, 0, 100, 5,  volSlider,    volStep);
    _gain   = addRow(col, "Mic gain",       LV_SYMBOL_AUDIO,      0, 60,  5,  gainSlider,   gainStep);
    _bright = addRow(col, "Brightness",     LV_SYMBOL_EYE_OPEN,   BRIGHT_MIN, BRIGHT_MAX, 10,
                     brightSlider, brightStep);

    // answer mode, the same setting the chat screen shows
    _btnMode = lv_button_create(col);
    lv_obj_set_size(_btnMode, W_COL - 8, 46);
    lv_obj_set_style_radius(_btnMode, 16, 0);
    lv_obj_set_style_bg_color(_btnMode, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_shadow_width(_btnMode, 0, 0);
    lv_obj_add_event_cb(_btnMode, modeEvent, LV_EVENT_CLICKED, nullptr);
    _lblMode = mkLabel(_btnMode, "Answer: text only", &font_nanum_18, C_GRAY);
    lv_obj_center(_lblMode);

    _lblIn = mkVoiceRow(col, inLangEvent, this);
    _lblOut = mkVoiceRow(col, outLangEvent, this);
    _lblVoice = mkVoiceRow(col, voiceEvent, this);

    lv_obj_t *btnTest = lv_button_create(col);
    lv_obj_set_size(btnTest, W_COL - 8, 46);
    lv_obj_set_style_radius(btnTest, 16, 0);
    lv_obj_set_style_bg_color(btnTest, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_shadow_width(btnTest, 0, 0);
    lv_obj_add_event_cb(btnTest, testEvent, LV_EVENT_CLICKED, nullptr);
    lv_obj_center(mkLabel(btnTest, LV_SYMBOL_PLAY "  Test tone", &font_nanum_18, C_WHITE));

#if !CONFIG_WATCH_APP_CHAT
    // Without the Chat app these drive nothing: its playback task (the test tone, the
    // volume slider's beep) never starts, and AskBot's volume is a build setting
    // (CONFIG_ASKBOT_VOLUME). Hidden, not removed, so refresh() keeps working.
    lv_obj_add_flag(lv_obj_get_parent(_vol.slider), LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(btnTest, LV_OBJ_FLAG_HIDDEN);
#endif

    // WiFi is device-wide (started at boot, used by AskBot); shown here because this
    // is where the device's own settings live. Read-only for now: there is no
    // on-screen keyboard, so credentials are provisioned over the BLE link.
    _lblWifi = mkLabel(col, "WiFi: ...", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblWifi, W_COL - 16);
    lv_label_set_long_mode(_lblWifi, LV_LABEL_LONG_WRAP);
    lv_obj_set_style_text_align(_lblWifi, LV_TEXT_ALIGN_CENTER, 0);

    _lblHint = mkLabel(col, "BOOT button: short press turns the screen\noff and on, hold 3 s to restart.",
                       &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblHint, W_COL - 16);
    lv_label_set_long_mode(_lblHint, LV_LABEL_LONG_WRAP);
    lv_obj_set_style_text_align(_lblHint, LV_TEXT_ALIGN_CENTER, 0);
}

void Settings::refresh()
{
    if (_lblWifi) {
        const device_wifi::Status wifi = device_wifi::status();
        char buf[128];
        switch (wifi.state) {
        case device_wifi::State::Off:
            snprintf(buf, sizeof(buf), "WiFi: off (no SSID configured)");
            break;
        case device_wifi::State::Connecting:
            snprintf(buf, sizeof(buf), "WiFi: connecting to %s%s", wifi.ssid.c_str(),
                     wifi.retries ? " ..." : "");
            break;
        case device_wifi::State::Connected:
            snprintf(buf, sizeof(buf), "WiFi: %s\n%s  (%d dBm)", wifi.ssid.c_str(), wifi.ip.c_str(),
                     (int)wifi.rssi);
            break;
        case device_wifi::State::Failed:
            snprintf(buf, sizeof(buf), "WiFi: could not join %s\n(2.4 GHz only)", wifi.ssid.c_str());
            break;
        }
        lv_label_set_text(_lblWifi, buf);
    }

    if (_lblIn && _lblOut && _lblVoice) {
        const device_voice::Prefs vp = device_voice::get();
        char buf[64];
        snprintf(buf, sizeof(buf), "Listen: %s", device_voice::langLabel(vp.inLang));
        lv_label_set_text(_lblIn, buf);
        snprintf(buf, sizeof(buf), "Speak: %s", device_voice::langLabel(vp.outLang));
        lv_label_set_text(_lblOut, buf);
        snprintf(buf, sizeof(buf), "Voice: %s", vp.voice);
        lv_label_set_text(_lblVoice, buf);
    }

    Core &core = Core::instance();
    uint32_t v = core.version();
    if (v == _seen) return;
    _seen = v;

    char buf[32];
    int vol = core.volume();
    int gain = core.micGain();
    snprintf(buf, sizeof(buf), "%d", vol);
    lv_label_set_text(_vol.value, buf);
    if (!lv_obj_has_state(_vol.slider, LV_STATE_PRESSED)) lv_slider_set_value(_vol.slider, vol, LV_ANIM_OFF);

    snprintf(buf, sizeof(buf), "%d dB", gain);
    lv_label_set_text(_gain.value, buf);
    if (!lv_obj_has_state(_gain.slider, LV_STATE_PRESSED)) lv_slider_set_value(_gain.slider, gain, LV_ANIM_OFF);

    snprintf(buf, sizeof(buf), "%d%%", s_brightness);
    lv_label_set_text(_bright.value, buf);
    if (!lv_obj_has_state(_bright.slider, LV_STATE_PRESSED))
        lv_slider_set_value(_bright.slider, s_brightness, LV_ANIM_OFF);

    bool voice = core.mode() == AnswerMode::TextAndVoice;
    lv_label_set_text(_lblMode, voice ? LV_SYMBOL_VOLUME_MAX "  Answer: text + voice" : "Answer: text only");
    lv_obj_set_style_text_color(_lblMode, lv_color_hex(voice ? C_PURPLE : C_GRAY), 0);
    lv_obj_set_style_bg_color(_btnMode, lv_color_hex(voice ? C_PURPLE_BG : C_DIM), 0);
}

ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, Settings, APP_NAME, []()
{
    return std::shared_ptr<Settings>(Settings::requestInstance(), [](Settings * p) {});
})

} // namespace esp_brookesia::apps
