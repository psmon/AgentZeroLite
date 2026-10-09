// Claude HUD as an ESP-Brookesia phone app. Four swipeable tiles: CREW / SESSIONS / USAGE / INFO.
#pragma once

#include "lvgl.h"
#include "systems/phone/esp_brookesia_phone_app.hpp"
#include "hud_state.hpp"

namespace esp_brookesia::apps {

class ClaudeHud : public systems::phone::App {
public:
    static ClaudeHud *requestInstance(bool use_status_bar = false, bool use_navigation_bar = false);
    ~ClaudeHud();

protected:
    ClaudeHud(bool use_status_bar, bool use_navigation_bar);

    bool init(void) override;    // install time: start the BLE transport once
    bool run(void) override;     // build the UI on the default screen
    bool back(void) override;
    bool close(void) override;

private:
    void buildUi(lv_obj_t *scr);
    void buildCrew(lv_obj_t *tile);
    void tick();
    void refresh(bool force);
    void animateCrew(uint32_t now);
    static void timerCb(lv_timer_t *t);
    static void brightnessCb(lv_event_t *e);

    lv_timer_t *_timer = nullptr;
    uint32_t    _seenVersion = 0;
    uint32_t    _lastRefreshMs = 0;
    uint32_t    _lastTickMs = 0;

    // cached model (refreshed on new data or every 1.5 s)
    claude_hud::Session _ss[claude_hud::MAX_SESSIONS];
    claude_hud::Limits  _lim;

    // tile 0: crew — one fixed slot per session, ASCII character + name tag
    struct CrewSlot {
        lv_obj_t *box = nullptr;
        lv_obj_t *name = nullptr;
        lv_obj_t *art = nullptr;
        int       baseX = 0, baseY = 0;
        int       level = -1;          // 0 idle, 1 low, 2 mid, 3 high (-1 = unset)
        int       frame = 0;
        uint32_t  nextFrameMs = 0;
        float     phase = 0;
        bool      shown = false;
        char      lastName[24] = "";
    } _crew[claude_hud::MAX_SESSIONS];
    lv_obj_t *_lblCrewHint = nullptr;

    // tile 1: sessions
    lv_obj_t *_ring = nullptr;
    lv_obj_t *_lblActive = nullptr;
    lv_obj_t *_rows[4] = {};
    // tile 2: usage
    lv_obj_t *_lblCost = nullptr;
    lv_obj_t *_lblSess = nullptr;
    lv_obj_t *_barCtx = nullptr, *_barRl5 = nullptr, *_barRl7 = nullptr;
    lv_obj_t *_lblCtx = nullptr, *_lblRl5 = nullptr, *_lblRl7 = nullptr;
    // tile 3: info
    lv_obj_t *_lblNet = nullptr;

    static ClaudeHud *_instance;
};

} // namespace esp_brookesia::apps
