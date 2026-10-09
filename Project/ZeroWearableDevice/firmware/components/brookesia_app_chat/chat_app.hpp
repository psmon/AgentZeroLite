// "Chat" ESP-Brookesia phone app: hold-to-talk voice chat with the PC host (amoled_chat_host).
#pragma once

#include "lvgl.h"
#include "systems/phone/esp_brookesia_phone_app.hpp"
#include "chat_core.hpp"

namespace esp_brookesia::apps {

class VoiceChat : public systems::phone::App {
public:
    static VoiceChat *requestInstance(bool use_status_bar = false, bool use_navigation_bar = false);
    ~VoiceChat();

protected:
    VoiceChat(bool use_status_bar, bool use_navigation_bar);

    bool init(void) override;    // install time: start BLE + chat core (works even while the app is closed)
    bool run(void) override;
    bool back(void) override;
    bool close(void) override;

private:
    void buildUi(lv_obj_t *scr);
    void refresh();
    static void timerCb(lv_timer_t *t);
    static void micEvent(lv_event_t *e);
    static void textEvent(lv_event_t *e);
    static void stopEvent(lv_event_t *e);
    static void modeEvent(lv_event_t *e);
    static void newChatEvent(lv_event_t *e);

    lv_timer_t *_timer = nullptr;
    uint32_t    _seen = 0;
    size_t      _lastReplyLen = 0;

    lv_obj_t *_lblHost = nullptr;
    lv_obj_t *_btnMode = nullptr;
    lv_obj_t *_lblMode = nullptr;
    lv_obj_t *_btnNew = nullptr;
    lv_obj_t *_box = nullptr;
    lv_obj_t *_lblUser = nullptr;
    lv_obj_t *_lblAi = nullptr;
    lv_obj_t *_lblStage = nullptr;
    lv_obj_t *_btnMic = nullptr;
    lv_obj_t *_imgMic = nullptr;
    lv_obj_t *_bar = nullptr;
    lv_obj_t *_btnText = nullptr;
    lv_obj_t *_btnStop = nullptr;

    static VoiceChat *_instance;
};

} // namespace esp_brookesia::apps
