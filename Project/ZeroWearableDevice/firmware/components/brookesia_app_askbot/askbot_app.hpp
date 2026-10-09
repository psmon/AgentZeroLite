// "AskBot" ESP-Brookesia phone app: same conversation flow as the Chat app, but the
// device is an Akka.NET remoting peer and every line is an actor message.
#pragma once

#include "lvgl.h"
#include "systems/phone/esp_brookesia_phone_app.hpp"
#include "askbot_core.hpp"

namespace esp_brookesia::apps {

class AskBot : public systems::phone::App {
public:
    static AskBot *requestInstance(bool use_status_bar = false, bool use_navigation_bar = false);
    ~AskBot();

protected:
    AskBot(bool use_status_bar, bool use_navigation_bar);

    bool init(void) override;
    bool run(void) override;
    bool back(void) override;
    bool close(void) override;

private:
    void buildUi(lv_obj_t *scr);
    void refresh();
    static void timerCb(lv_timer_t *t);
    static void askEvent(lv_event_t *e);
    static void micEvent(lv_event_t *e);
    static void presetEvent(lv_event_t *e);
    static void stopEvent(lv_event_t *e);
    static void newChatEvent(lv_event_t *e);
    static void modeEvent(lv_event_t *e);

    lv_timer_t *_timer = nullptr;
    uint32_t    _seen = 0;
    size_t      _lastReplyLen = 0;
    int         _preset = 0;

    lv_obj_t *_lblLink = nullptr;
    lv_obj_t *_btnPreset = nullptr;
    lv_obj_t *_lblPreset = nullptr;
    lv_obj_t *_btnNew = nullptr;
    lv_obj_t *_btnMode = nullptr;
    lv_obj_t *_lblMode = nullptr;
    lv_obj_t *_bar = nullptr;
    lv_obj_t *_box = nullptr;
    lv_obj_t *_lblUser = nullptr;
    lv_obj_t *_lblAi = nullptr;
    lv_obj_t *_lblStage = nullptr;
    lv_obj_t *_btnMic = nullptr;
    lv_obj_t *_lblMic = nullptr;
    lv_obj_t *_btnAsk = nullptr;
    lv_obj_t *_lblAsk = nullptr;
    lv_obj_t *_btnStop = nullptr;

    static AskBot *_instance;
};

} // namespace esp_brookesia::apps
