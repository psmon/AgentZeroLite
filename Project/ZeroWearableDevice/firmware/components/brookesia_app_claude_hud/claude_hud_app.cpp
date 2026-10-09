#include <cstdio>
#include <cstring>
#include <cmath>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:ClaudeHud"
#include "esp_lib_utils.h"
#include "bsp/esp-bsp.h"
#include "claude_hud_app.hpp"
#include "device_power.hpp"
#include "hud_transport.hpp"

#define APP_NAME "Claude HUD"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using claude_hud::State;
using claude_hud::Session;
using claude_hud::Limits;
using claude_hud::MAX_SESSIONS;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_claude_hud_112_112);
LV_FONT_DECLARE(font_nanum_18);          // NanumGothic: Latin + 2350 Hangul syllables (tools/gen_font.py)

namespace esp_brookesia::apps {

// ---------------------------------------------------------------- palette
static const uint32_t C_BG     = 0x000000;
static const uint32_t C_WHITE  = 0xFFFFFF;
static const uint32_t C_GRAY   = 0x8A8F98;
static const uint32_t C_DIM    = 0x3A4052;
static const uint32_t C_GREEN  = 0x3DD68C;   // active
static const uint32_t C_BLUE   = 0x4C8DFF;   // done
static const uint32_t C_AMBER  = 0xF5A524;   // idle
static const uint32_t C_YELLOW = 0xFFE066;
static const uint32_t C_ORANGE = 0xFF8C42;
static const uint32_t C_RED    = 0xFF5C5C;
static const uint32_t C_CYAN   = 0x5CE1E6;

static uint32_t stateColor(const char *st)
{
    if (!strcmp(st, "tool") || !strcmp(st, "thinking") || !strcmp(st, "prompt_start") || !strcmp(st, "subagent"))
        return C_GREEN;
    if (!strcmp(st, "done") || !strcmp(st, "tool_end")) return C_BLUE;
    return C_AMBER;
}

// ---------------------------------------------------------------- crew characters
// Five distinct ASCII characters (<= 9 columns x 4 lines, monospace unscii_16 = 8x16 px).
// active[0]/active[1] alternate while working; idle[0]/idle[1] toggle the "z" while resting.
struct CrewArt { const char *active[2]; const char *idle[2]; };
static const CrewArt CREW_ART[MAX_SESSIONS] = {
    // 0: robot
    { { "  [o_o]\n /|===|\\\n   | |\n  _/ \\_",
        "  [o_o]\n \\|===|/\n   | |\n  _/ \\_" },
      { "  [-_-] z\n  |===|\n   | |\n  _/ \\_",
        "  [-_-] Z\n  |===|\n   | |\n  _/ \\_" } },
    // 1: cat
    { { "  /\\_/\\\n ( o.o )\n  > ^ < ~\n /|   |\\",
        "  /\\_/\\\n ( o.o )\n ~> ^ <\n /|   |\\" },
      { "  /\\_/\\\n ( -.- ) z\n  > ^ <\n /|   |\\",
        "  /\\_/\\\n ( -.- ) Z\n  > ^ <\n /|   |\\" } },
    // 2: alien
    { { "  (o o)\n -(_._)-\n  /   \\\n /     \\",
        "  (o o)\n /(_._)\\\n  /   \\\n /     \\" },
      { "  (- -) z\n  (_._)\n  /   \\\n /     \\",
        "  (- -) Z\n  (_._)\n  /   \\\n /     \\" } },
    // 3: blob
    { { " .-\"\"-.\n( o  o )\n(  ..  )\n `-..-'",
        " .-\"\"-.\n(  o  o )\n(  ..  )\n `-..-'" },
      { " .-\"\"-.\n( -  - ) z\n(  __  )\n `-..-'",
        " .-\"\"-.\n( -  - ) Z\n(  __  )\n `-..-'" } },
    // 4: bird
    { { "   (o>\n  //\\ \\\n   V_/\n   ^ ^",
        "   (o>\n  /\\\\ \\\n   V_/\n   ^ ^" },
      { "   (-> z\n  /\\ \\\n   V_/\n   ^ ^",
        "   (-> Z\n  /\\ \\\n   V_/\n   ^ ^" } },
};
// Fixed slot centres on the 466x466 round panel (all boxes stay inside r~220).
static const int SLOT_CX[MAX_SESSIONS] = { 233, 118, 348, 165, 301 };
static const int SLOT_CY[MAX_SESSIONS] = { 118, 222, 222, 338, 338 };
static constexpr int SLOT_W = 104, SLOT_H = 100;
// State machine tuning per level: 0 idle, 1 low, 2 mid, 3 high
static const uint32_t LVL_FRAME_MS[4] = { 1600, 700, 380, 170 };
static const float    LVL_AMP[4]      = { 0.0f, 2.0f, 4.0f, 7.0f };   // px
static const float    LVL_SPEED[4]    = { 0.8f, 2.0f, 4.0f, 7.5f };   // rad/s
static const uint32_t LVL_COLOR[4]    = { C_GRAY, C_GREEN, C_YELLOW, C_ORANGE };

// ---------------------------------------------------------------- helpers
static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color,
                         lv_align_t align, int x, int y)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    lv_obj_align(l, align, x, y);
    return l;
}

static lv_obj_t *mkBar(lv_obj_t *parent, int y, uint32_t color)
{
    lv_obj_t *b = lv_bar_create(parent);
    lv_obj_set_size(b, 280, 14);
    lv_obj_align(b, LV_ALIGN_TOP_MID, 0, y);
    lv_bar_set_range(b, 0, 100);
    lv_bar_set_value(b, 0, LV_ANIM_OFF);
    lv_obj_set_style_bg_color(b, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), LV_PART_INDICATOR);
    lv_obj_set_style_radius(b, 7, LV_PART_MAIN);
    lv_obj_set_style_radius(b, 7, LV_PART_INDICATOR);
    return b;
}

static lv_obj_t *mkTile(lv_obj_t *tv, int col, lv_dir_t dir)
{
    lv_obj_t *t = lv_tileview_add_tile(tv, col, 0, dir);
    lv_obj_set_style_bg_color(t, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(t, LV_OPA_COVER, 0);
    lv_obj_remove_flag(t, LV_OBJ_FLAG_SCROLLABLE);
    return t;
}

// ---------------------------------------------------------------- app lifecycle
ClaudeHud *ClaudeHud::_instance = nullptr;

ClaudeHud *ClaudeHud::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) _instance = new ClaudeHud(use_status_bar, use_navigation_bar);
    return _instance;
}

ClaudeHud::ClaudeHud(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_claude_hud_112_112, true, use_status_bar, use_navigation_bar)
{
}

ClaudeHud::~ClaudeHud() {}

bool ClaudeHud::init(void)
{
    ESP_UTILS_LOGI("Init: starting BLE (the only transport)");
    claude_hud::startBle();
    return true;
}

bool ClaudeHud::run(void)
{
    ESP_UTILS_LOGD("Run");
    buildUi(lv_scr_act());
    _seenVersion = 0;
    _lastTickMs = claude_hud::nowMs();
    for (auto &c : _crew) { c.level = -1; c.frame = 0; c.nextFrameMs = 0; c.phase = 0; c.shown = false; c.lastName[0] = 0; }
    refresh(true);
    animateCrew(_lastTickMs);
    // Recorded by the core (enable_recycle_resource) -> deleted automatically on close.
    _timer = lv_timer_create(timerCb, 100, this);
    return true;
}

bool ClaudeHud::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

bool ClaudeHud::close(void)
{
    _timer = nullptr;   // freed by the core together with the screen
    return true;
}

void ClaudeHud::timerCb(lv_timer_t *t)
{
    auto *self = (ClaudeHud *)lv_timer_get_user_data(t);
    if (self) self->tick();
}

void ClaudeHud::tick()
{
    uint32_t now = claude_hud::nowMs();
    refresh(false);
    animateCrew(now);
    _lastTickMs = now;
}

void ClaudeHud::brightnessCb(lv_event_t *e)
{
    lv_obj_t *slider = (lv_obj_t *)lv_event_get_target(e);
    device_power::setBrightness((int)lv_slider_get_value(slider));
}

// ---------------------------------------------------------------- UI build
void ClaudeHud::buildCrew(lv_obj_t *tile)
{
    for (int i = 0; i < MAX_SESSIONS; i++) {
        CrewSlot &c = _crew[i];
        c.baseX = SLOT_CX[i] - SLOT_W / 2;
        c.baseY = SLOT_CY[i] - SLOT_H / 2;
        c.box = lv_obj_create(tile);
        lv_obj_set_size(c.box, SLOT_W, SLOT_H);
        lv_obj_set_pos(c.box, c.baseX, c.baseY);
        lv_obj_set_style_bg_opa(c.box, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(c.box, 0, 0);
        lv_obj_set_style_pad_all(c.box, 0, 0);
        lv_obj_remove_flag(c.box, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);

        c.name = lv_label_create(c.box);
        lv_obj_set_width(c.name, SLOT_W);
        lv_label_set_long_mode(c.name, LV_LABEL_LONG_DOT);
        lv_obj_set_style_text_align(c.name, LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_set_style_text_font(c.name, &font_nanum_18, 0);
        lv_obj_set_style_text_color(c.name, lv_color_hex(C_WHITE), 0);
        lv_label_set_text(c.name, "");
        lv_obj_align(c.name, LV_ALIGN_TOP_MID, 0, 0);

        c.art = lv_label_create(c.box);
        lv_obj_set_style_text_font(c.art, &lv_font_unscii_16, 0);
        lv_obj_set_style_text_color(c.art, lv_color_hex(C_GRAY), 0);
        lv_obj_set_style_text_line_space(c.art, 0, 0);
        lv_label_set_text(c.art, CREW_ART[i].idle[0]);
        lv_obj_align(c.art, LV_ALIGN_TOP_MID, 0, 26);

        lv_obj_add_flag(c.box, LV_OBJ_FLAG_HIDDEN);
    }
    _lblCrewHint = mkLabel(tile, "waiting for sessions...", &font_nanum_18, C_DIM, LV_ALIGN_CENTER, 0, 0);
}

void ClaudeHud::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    lv_obj_t *tv = lv_tileview_create(scr);
    lv_obj_set_size(tv, LV_PCT(100), LV_PCT(100));
    lv_obj_set_style_bg_color(tv, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(tv, LV_OPA_COVER, 0);
    lv_obj_set_scrollbar_mode(tv, LV_SCROLLBAR_MODE_OFF);

    lv_obj_t *t0 = mkTile(tv, 0, LV_DIR_RIGHT);   // CREW
    lv_obj_t *t1 = mkTile(tv, 1, LV_DIR_HOR);     // SESSIONS
    lv_obj_t *t2 = mkTile(tv, 2, LV_DIR_HOR);     // USAGE
    lv_obj_t *t3 = mkTile(tv, 3, LV_DIR_LEFT);    // INFO

    // ---- tile 0: CREW ----
    buildCrew(t0);

    // ---- tile 1: SESSIONS (round display: keep content inside ~x 60..406) ----
    _ring = lv_arc_create(t1);
    lv_obj_set_size(_ring, 456, 456);
    lv_obj_center(_ring);
    lv_arc_set_bg_angles(_ring, 0, 360);
    lv_arc_set_range(_ring, 0, 360);
    lv_arc_set_value(_ring, 360);
    lv_obj_remove_style(_ring, nullptr, LV_PART_KNOB);
    lv_obj_remove_flag(_ring, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_arc_width(_ring, 6, LV_PART_MAIN);
    lv_obj_set_style_arc_width(_ring, 6, LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(C_AMBER), LV_PART_INDICATOR);

    mkLabel(t1, "SESSIONS", &lv_font_montserrat_28, C_WHITE, LV_ALIGN_TOP_MID, 0, 62);
    _lblActive = mkLabel(t1, "0 active", &lv_font_montserrat_20, C_GRAY, LV_ALIGN_TOP_MID, 0, 104);
    for (int i = 0; i < 4; i++) {
        _rows[i] = mkLabel(t1, "", &font_nanum_18, C_GRAY, LV_ALIGN_TOP_LEFT, 64, 160 + i * 46);
        lv_obj_set_width(_rows[i], 338);
        lv_label_set_long_mode(_rows[i], LV_LABEL_LONG_DOT);
    }
    mkLabel(t1, "< crew   usage >", &lv_font_montserrat_16, C_DIM, LV_ALIGN_BOTTOM_MID, 0, -70);

    // ---- tile 2: USAGE ----
    mkLabel(t2, "USAGE", &lv_font_montserrat_28, C_CYAN, LV_ALIGN_TOP_MID, 0, 62);
    _lblCost = mkLabel(t2, "$0.000", &lv_font_montserrat_44, C_YELLOW, LV_ALIGN_TOP_MID, 0, 108);
    _lblSess = mkLabel(t2, "0 sess", &lv_font_montserrat_20, C_GRAY, LV_ALIGN_TOP_MID, 0, 164);
    _lblCtx = mkLabel(t2, "ctx 0%", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 206);
    _barCtx = mkBar(t2, 234, C_GREEN);
    _lblRl5 = mkLabel(t2, "5h  n/a", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 262);
    _barRl5 = mkBar(t2, 290, C_ORANGE);
    _lblRl7 = mkLabel(t2, "7d  n/a", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 318);
    _barRl7 = mkBar(t2, 346, C_RED);

    // ---- tile 3: INFO + brightness ----
    mkLabel(t3, "INFO", &lv_font_montserrat_28, C_GRAY, LV_ALIGN_TOP_MID, 0, 62);
    _lblNet = mkLabel(t3, "", &font_nanum_18, C_WHITE, LV_ALIGN_TOP_LEFT, 84, 110);
    lv_obj_set_width(_lblNet, 300);
    lv_label_set_long_mode(_lblNet, LV_LABEL_LONG_WRAP);
    mkLabel(t3, "brightness", &lv_font_montserrat_16, C_YELLOW, LV_ALIGN_TOP_LEFT, 92, 318);
    lv_obj_t *sl = lv_slider_create(t3);
    lv_obj_set_size(sl, 260, 16);
    lv_obj_align(sl, LV_ALIGN_TOP_MID, 0, 346);
    lv_slider_set_range(sl, 10, 100);
    lv_slider_set_value(sl, device_power::brightness(), LV_ANIM_OFF);   // the Settings app may have
                                                                       // restored something else at boot
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_YELLOW), LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_WHITE), LV_PART_KNOB);
    lv_obj_add_event_cb(sl, brightnessCb, LV_EVENT_VALUE_CHANGED, nullptr);
}

// ---------------------------------------------------------------- crew animation (100 ms tick)
void ClaudeHud::animateCrew(uint32_t now)
{
    float dt = (float)(now - _lastTickMs) / 1000.0f;
    if (dt < 0 || dt > 1.0f) dt = 0.1f;
    bool any = false;

    for (int i = 0; i < MAX_SESSIONS; i++) {
        CrewSlot &c = _crew[i];
        const Session &s = _ss[i];
        if (!s.used) {
            if (c.shown) { lv_obj_add_flag(c.box, LV_OBJ_FLAG_HIDDEN); c.shown = false; c.level = -1; }
            continue;
        }
        any = true;
        if (!c.shown) { lv_obj_remove_flag(c.box, LV_OBJ_FLAG_HIDDEN); c.shown = true; }

        // name tag = pure session label
        if (strcmp(c.lastName, s.label) != 0) {
            strncpy(c.lastName, s.label, sizeof(c.lastName) - 1);
            c.lastName[sizeof(c.lastName) - 1] = 0;
            lv_label_set_text(c.name, s.label);
        }

        // state machine: level drives colour, frame rate and motion
        int lvl = claude_hud::sessionLevel(s, now);
        if (lvl != c.level) {
            c.level = lvl;
            c.nextFrameMs = 0;
            lv_obj_set_style_text_color(c.art, lv_color_hex(LVL_COLOR[lvl]), 0);
            lv_obj_set_style_text_color(c.name, lv_color_hex(lvl ? C_WHITE : C_GRAY), 0);
        }
        if (now >= c.nextFrameMs) {
            c.frame ^= 1;
            c.nextFrameMs = now + LVL_FRAME_MS[lvl];
            lv_label_set_text(c.art, lvl ? CREW_ART[i].active[c.frame] : CREW_ART[i].idle[c.frame]);
        }
        c.phase += LVL_SPEED[lvl] * dt;
        if (c.phase > 6.2831853f) c.phase -= 6.2831853f;
        int dx, dy;
        if (lvl == 0) {                                  // idle: slow breathing
            dx = 0;
            dy = (int)lroundf(1.5f * sinf(c.phase));
        } else {                                         // working: sway + bounce, bigger with level
            dx = (int)lroundf(LVL_AMP[lvl] * sinf(c.phase));
            dy = -(int)lroundf(LVL_AMP[lvl] * 0.6f * fabsf(sinf(2.0f * c.phase)));
        }
        lv_obj_set_pos(c.box, c.baseX + dx, c.baseY + dy);
    }
    if (any) lv_obj_add_flag(_lblCrewHint, LV_OBJ_FLAG_HIDDEN);
    else     lv_obj_remove_flag(_lblCrewHint, LV_OBJ_FLAG_HIDDEN);
}

// ---------------------------------------------------------------- data refresh (on change / 1.5 s)
void ClaudeHud::refresh(bool force)
{
    State &st = State::instance();
    uint32_t now = claude_hud::nowMs();
    uint32_t ver = st.version();
    if (!force && ver == _seenVersion && now - _lastRefreshMs < 1500) return;
    _seenVersion = ver;
    _lastRefreshMs = now;

    st.snapshot(_ss, _lim);
    Session (&ss)[MAX_SESSIONS] = _ss;
    Limits &lim = _lim;

    // ---- sessions ----
    int active = 0, shown = 0;
    bool anyActive = false;
    float totalCost = 0, maxCtx = 0;
    for (auto &s : ss) {
        if (!s.used) continue;
        bool idle = claude_hud::sessionIdle(s, now);
        if (now - s.lastSeenMs < claude_hud::SESSION_IDLE_MS) active++;
        if (!idle) anyActive = true;
        totalCost += s.costUsd;
        if (s.ctxUsedPct > maxCtx) maxCtx = s.ctxUsedPct;
        if (shown < 4) {
            char line[120];
            if (s.host[0]) snprintf(line, sizeof(line), "%s:%s  %s", s.host, s.label, s.activity);
            else           snprintf(line, sizeof(line), "%s  %s", s.label, s.activity);
            lv_label_set_text(_rows[shown], line);
            lv_obj_set_style_text_color(_rows[shown], lv_color_hex(idle ? C_GRAY : stateColor(s.state)), 0);
            shown++;
        }
    }
    for (int i = shown; i < 4; i++) lv_label_set_text(_rows[i], i == 0 ? "no sessions yet" : "");
    char buf[64];
    snprintf(buf, sizeof(buf), "%d active", active);
    lv_label_set_text(_lblActive, buf);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(anyActive ? C_GREEN : C_AMBER), LV_PART_INDICATOR);

    // ---- usage ----
    snprintf(buf, sizeof(buf), "$%.3f", totalCost);
    lv_label_set_text(_lblCost, buf);
    snprintf(buf, sizeof(buf), "%d sess", active);
    lv_label_set_text(_lblSess, buf);
    snprintf(buf, sizeof(buf), "ctx %d%%", (int)maxCtx);
    lv_label_set_text(_lblCtx, buf);
    lv_bar_set_value(_barCtx, (int)maxCtx, LV_ANIM_OFF);
    if (lim.has) {
        snprintf(buf, sizeof(buf), "5h  %d%%", (int)lim.rl5hPct);
        lv_label_set_text(_lblRl5, buf);
        lv_bar_set_value(_barRl5, (int)lim.rl5hPct, LV_ANIM_OFF);
        snprintf(buf, sizeof(buf), "7d  %d%%", (int)lim.rl7dPct);
        lv_label_set_text(_lblRl7, buf);
        lv_bar_set_value(_barRl7, (int)lim.rl7dPct, LV_ANIM_OFF);
    } else {
        lv_label_set_text(_lblRl5, "5h  n/a");
        lv_label_set_text(_lblRl7, "7d  n/a");
    }

    // ---- info ----
    auto &net = st.net;
    char info[300];
    const char *bleState = net.bleConn ? "connected" : (net.bleAdv ? "advertising" : (net.bleReady ? "idle" : "starting"));
    if (net.bleErr[0]) bleState = net.bleErr;
    if (net.rxBle) {
        snprintf(info, sizeof(info), "BLE  %s\nname  claude-hud\nrx %lu  last %lus ago\nup %lus",
                 bleState, (unsigned long)net.rxBle, (unsigned long)((now - net.lastRxMs) / 1000),
                 (unsigned long)(now / 1000));
    } else {
        snprintf(info, sizeof(info), "BLE  %s\nname  claude-hud\nrx none yet\nup %lus",
                 bleState, (unsigned long)(now / 1000));
    }
    lv_label_set_text(_lblNet, info);
}

// Registered only when the build carries the app (menuconfig: AgentZero watch apps). The
// component itself is always built: AskBot uses its BLE transport, font and device code.
#if CONFIG_WATCH_APP_CLAUDE_HUD
ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, ClaudeHud, APP_NAME, []()
{
    return std::shared_ptr<ClaudeHud>(ClaudeHud::requestInstance(), [](ClaudeHud * p) {});
})
#endif

} // namespace esp_brookesia::apps
