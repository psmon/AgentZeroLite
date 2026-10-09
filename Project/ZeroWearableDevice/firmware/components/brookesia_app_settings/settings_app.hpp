// "Settings" ESP-Brookesia phone app: touch sliders for speaker volume, microphone gain and display
// brightness, plus the chat answer mode. The audio values live in the chat app's Core (it owns the codec
// handles); this app is only the screen for them.
#pragma once

#include "lvgl.h"
#include "systems/phone/esp_brookesia_phone_app.hpp"

namespace esp_brookesia::apps {

class Settings : public systems::phone::App {
public:
    static Settings *requestInstance(bool use_status_bar = false, bool use_navigation_bar = false);
    ~Settings();

protected:
    Settings(bool use_status_bar, bool use_navigation_bar);

    bool init(void) override;
    bool run(void) override;
    bool back(void) override;
    bool close(void) override;

private:
    struct Row {
        lv_obj_t *slider = nullptr;
        lv_obj_t *value = nullptr;
        lv_obj_t *minus = nullptr;
        lv_obj_t *plus = nullptr;
    };

    void buildUi(lv_obj_t *scr);
    Row  addRow(lv_obj_t *parent, const char *title, const char *icon, int min, int max, int step,
                lv_event_cb_t onSlider, lv_event_cb_t onStep);
    void refresh();

    static void timerCb(lv_timer_t *t);
    static void volSlider(lv_event_t *e);
    static void volStep(lv_event_t *e);
    static void gainSlider(lv_event_t *e);
    static void gainStep(lv_event_t *e);
    static void brightSlider(lv_event_t *e);
    static void brightStep(lv_event_t *e);
    static void modeEvent(lv_event_t *e);
    static void testEvent(lv_event_t *e);
    static void inLangEvent(lv_event_t *e);
    static void outLangEvent(lv_event_t *e);
    static void voiceEvent(lv_event_t *e);

    lv_timer_t *_timer = nullptr;
    uint32_t    _seen = 0;
    bool        _dragging = false;

    Row _vol, _gain, _bright;
    lv_obj_t *_btnMode = nullptr;
    lv_obj_t *_lblMode = nullptr;
    lv_obj_t *_lblWifi = nullptr;
    lv_obj_t *_lblIn = nullptr;      // speech input language
    lv_obj_t *_lblOut = nullptr;     // spoken answer language
    lv_obj_t *_lblVoice = nullptr;   // spoken answer voice
    lv_obj_t *_lblHint = nullptr;

    static Settings *_instance;
};

} // namespace esp_brookesia::apps
