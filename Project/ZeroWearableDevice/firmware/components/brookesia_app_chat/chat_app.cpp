#include <cstdio>
#include <cstring>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:Chat"
#include "esp_lib_utils.h"
#include "chat_app.hpp"
#include "hud_transport.hpp"

#define APP_NAME "Chat"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using voice_chat::AnswerMode;
using voice_chat::Core;
using voice_chat::Snapshot;
using voice_chat::Stage;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_chat_112_112);
LV_IMG_DECLARE(chat_icon_mic_48);        // tools/gen_mic_glyph.py — LVGL has no microphone symbol
LV_FONT_DECLARE(font_nanum_18);          // shared with brookesia_app_claude_hud (Latin + Hangul)

namespace esp_brookesia::apps {

static const uint32_t C_BG     = 0x000000;
static const uint32_t C_WHITE  = 0xFFFFFF;
static const uint32_t C_GRAY   = 0x8A8F98;
static const uint32_t C_DIM    = 0x232733;
static const uint32_t C_GREEN  = 0x3DD68C;
static const uint32_t C_BLUE   = 0x4C8DFF;
static const uint32_t C_AMBER  = 0xF5A524;
static const uint32_t C_RED    = 0xFF5C5C;
static const uint32_t C_CYAN   = 0x5CE1E6;
static const uint32_t C_PURPLE = 0xA78BFA;
static const uint32_t C_PURPLE_BG = 0x2A2440;

static const char *PRESET_TEXT = "What time is it? Answer in one short sentence.";

// Layout tuned to the 466x466 circle: at a given y the usable half-width is sqrt(233^2 - (y-233)^2),
// so anything near the top or the bottom has to stay narrow or the bezel clips it.
static constexpr int W_HOST = 232, Y_HOST  = 30;
// Top row holds two pills: the answer-mode toggle and "new chat". Widths are picked so the pair stays
// inside the circle at this y (usable half-width there is about 149 px).
static constexpr int Y_MODE = 56, H_MODE = 28;
static constexpr int W_MODE = 146, X_MODE = -46;
static constexpr int W_NEW  = 84,  X_NEW  = 77;
static constexpr int W_BOX  = 344, Y_BOX   = 92, H_BOX  = 182;
static constexpr int Y_STAGE = 282, Y_BAR  = 308;
static constexpr int MIC_D  = 88,  Y_MIC   = 324;
static constexpr int SIDE_D = 52,  Y_SIDE  = 342, X_SIDE = 96;

VoiceChat *VoiceChat::_instance = nullptr;

VoiceChat *VoiceChat::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) {
        _instance = new (std::nothrow) VoiceChat(use_status_bar, use_navigation_bar);
        ESP_UTILS_CHECK_NULL_RETURN(_instance, nullptr, "Create instance failed");
    }
    return _instance;
}

VoiceChat::VoiceChat(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_chat_112_112, true, use_status_bar, use_navigation_bar)
{
}

VoiceChat::~VoiceChat() {}

bool VoiceChat::init(void)
{
    ESP_UTILS_LOGI("Init: BLE transport + chat core");
    claude_hud::startBle();          // idempotent; whichever app installs first starts the stack
    Core::instance().init();
    return true;
}

bool VoiceChat::run(void)
{
    buildUi(lv_scr_act());
    _seen = 0;
    _lastReplyLen = 0;
    refresh();
    _timer = lv_timer_create(timerCb, 100, this);   // recycled by the core on close
    return true;
}

bool VoiceChat::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

bool VoiceChat::close(void)
{
    Core::instance().stopVoice();
    _timer = nullptr;
    return true;
}

void VoiceChat::timerCb(lv_timer_t *t)
{
    auto *self = (VoiceChat *)lv_timer_get_user_data(t);
    if (self) self->refresh();
}

// ---------------------------------------------------------------- events
void VoiceChat::micEvent(lv_event_t *e)
{
    auto code = lv_event_get_code(e);
    if (code == LV_EVENT_PRESSED) {
        Core::instance().startVoice(30000);
    } else if (code == LV_EVENT_RELEASED || code == LV_EVENT_PRESS_LOST) {
        Core::instance().stopVoice();
    }
}

void VoiceChat::textEvent(lv_event_t *) { Core::instance().sendText(PRESET_TEXT); }

// Square button means stop, not delete: it aborts the recording, the host request and the answer audio.
void VoiceChat::stopEvent(lv_event_t *) { Core::instance().cancel(); }

void VoiceChat::modeEvent(lv_event_t *)
{
    Core &c = Core::instance();
    c.setMode(c.mode() == AnswerMode::TextAndVoice ? AnswerMode::TextOnly : AnswerMode::TextAndVoice);
}

// The chat CLI owns the conversation history (netclaw resumes it by session id), so this only asks the
// host to move to a fresh id. The previous conversation is not deleted, just left behind.
void VoiceChat::newChatEvent(lv_event_t *) { Core::instance().newChat(); }

// ---------------------------------------------------------------- UI helpers
static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    return l;
}

// `code` must be a specific event (LV_EVENT_CLICKED, ...). Registering LV_EVENT_ALL here would fire the
// callback on every draw/refresh event too — that once turned the T button into a prompt flood.
static lv_obj_t *mkBtn(lv_obj_t *parent, int w, int h, int radius, uint32_t color,
                       lv_event_cb_t cb, lv_event_code_t code)
{
    lv_obj_t *b = lv_button_create(parent);
    lv_obj_set_size(b, w, h);
    lv_obj_set_style_radius(b, radius, 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), LV_STATE_PRESSED);
    lv_obj_set_style_shadow_width(b, 0, 0);
    lv_obj_set_style_pad_all(b, 0, 0);
    lv_obj_add_event_cb(b, cb, code, nullptr);
    return b;
}

void VoiceChat::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    // top: host line
    _lblHost = mkLabel(scr, "waiting for BLE", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblHost, W_HOST);
    lv_label_set_long_mode(_lblHost, LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_align(_lblHost, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblHost, LV_ALIGN_TOP_MID, 0, Y_HOST);

    // the setting: text answer only, or text plus a spoken answer through the speaker
    _btnMode = mkBtn(scr, W_MODE, H_MODE, H_MODE / 2, C_DIM, modeEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnMode, LV_ALIGN_TOP_MID, X_MODE, Y_MODE);
    _lblMode = mkLabel(_btnMode, "Text only", &font_nanum_18, C_GRAY);
    lv_obj_center(_lblMode);

    _btnNew = mkBtn(scr, W_NEW, H_MODE, H_MODE / 2, C_DIM, newChatEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnNew, LV_ALIGN_TOP_MID, X_NEW, Y_MODE);
    lv_obj_center(mkLabel(_btnNew, "New chat", &font_nanum_18, C_GRAY));

    // middle: conversation box (scrollable)
    _box = lv_obj_create(scr);
    lv_obj_set_size(_box, W_BOX, H_BOX);
    lv_obj_align(_box, LV_ALIGN_TOP_MID, 0, Y_BOX);
    lv_obj_set_style_bg_color(_box, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_bg_opa(_box, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(_box, 0, 0);
    lv_obj_set_style_radius(_box, 18, 0);
    lv_obj_set_style_pad_all(_box, 14, 0);
    lv_obj_set_flex_flow(_box, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(_box, 8, 0);
    lv_obj_set_scroll_dir(_box, LV_DIR_VER);

    _lblUser = mkLabel(_box, "", &font_nanum_18, C_CYAN);
    lv_obj_set_width(_lblUser, W_BOX - 32);
    lv_label_set_long_mode(_lblUser, LV_LABEL_LONG_WRAP);
    _lblAi = mkLabel(_box, "Hold the microphone and speak.", &font_nanum_18, C_WHITE);
    lv_obj_set_width(_lblAi, W_BOX - 32);
    lv_label_set_long_mode(_lblAi, LV_LABEL_LONG_WRAP);

    // stage line + progress bar (input level while recording, download progress while receiving speech)
    _lblStage = mkLabel(scr, "", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblStage, 300);
    lv_label_set_long_mode(_lblStage, LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_align(_lblStage, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblStage, LV_ALIGN_TOP_MID, 0, Y_STAGE);

    _bar = lv_bar_create(scr);
    lv_obj_set_size(_bar, 200, 6);
    lv_obj_align(_bar, LV_ALIGN_TOP_MID, 0, Y_BAR);
    lv_bar_set_range(_bar, 0, 100);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_GREEN), LV_PART_INDICATOR);
    lv_obj_add_flag(_bar, LV_OBJ_FLAG_HIDDEN);

    // bottom row: [T]  (mic, hold to talk)  [stop]
    _btnMic = mkBtn(scr, MIC_D, MIC_D, LV_RADIUS_CIRCLE, C_BLUE, micEvent, LV_EVENT_PRESSED);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_RELEASED, nullptr);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_PRESS_LOST, nullptr);
    lv_obj_align(_btnMic, LV_ALIGN_TOP_MID, 0, Y_MIC);
    _imgMic = lv_image_create(_btnMic);
    lv_image_set_src(_imgMic, &chat_icon_mic_48);
    lv_obj_center(_imgMic);

    _btnText = mkBtn(scr, SIDE_D, SIDE_D, LV_RADIUS_CIRCLE, C_DIM, textEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnText, LV_ALIGN_TOP_MID, -X_SIDE, Y_SIDE);
    lv_obj_center(mkLabel(_btnText, "T", &lv_font_montserrat_22, C_WHITE));

    _btnStop = mkBtn(scr, SIDE_D, SIDE_D, LV_RADIUS_CIRCLE, C_DIM, stopEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnStop, LV_ALIGN_TOP_MID, X_SIDE, Y_SIDE);
    lv_obj_center(mkLabel(_btnStop, LV_SYMBOL_STOP, &lv_font_montserrat_20, C_WHITE));
}

void VoiceChat::refresh()
{
    Core &core = Core::instance();
    uint32_t v = core.version();
    Snapshot s;
    core.snapshot(s);
    bool recording = s.stage == Stage::Recording;
    if (v == _seen && !recording) return;      // the level bar still needs ticks while recording
    _seen = v;

    char buf[192];
    if (!s.bleConnected)      snprintf(buf, sizeof(buf), "waiting for BLE");
    else if (!s.hostOnline)   snprintf(buf, sizeof(buf), "waiting for host");
    else if (s.chatNo > 1)    snprintf(buf, sizeof(buf), "%s · %s · chat %d", s.host, s.provider, s.chatNo);
    else                      snprintf(buf, sizeof(buf), "%s · %s", s.host, s.provider);
    lv_label_set_text(_lblHost, buf);

    // the answer-mode pill only means something when the host actually has a voice installed
    if (s.hostTts) {
        lv_obj_remove_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);
        bool voice = s.mode == AnswerMode::TextAndVoice;
        lv_label_set_text(_lblMode, voice ? LV_SYMBOL_VOLUME_MAX " Text + voice" : "Text only");
        lv_obj_set_style_text_color(_lblMode, lv_color_hex(voice ? C_PURPLE : C_GRAY), 0);
        lv_obj_set_style_bg_color(_btnMode, lv_color_hex(voice ? C_PURPLE_BG : C_DIM), 0);
        lv_obj_set_style_bg_color(_btnMode, lv_color_hex(voice ? C_PURPLE_BG : C_DIM), LV_STATE_PRESSED);
    } else {
        lv_obj_add_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);
    }

    lv_label_set_text(_lblUser, s.transcript[0] ? s.transcript : "");
    if (s.reply[0])                                      lv_label_set_text(_lblAi, s.reply);
    else if (s.stage == Stage::Idle && !s.transcript[0]) lv_label_set_text(_lblAi, "Hold the microphone and speak.");
    else                                                 lv_label_set_text(_lblAi, "");

    const char *stage = "";
    uint32_t micColor = C_BLUE;
    switch (s.stage) {
    case Stage::Idle:
        stage = s.micOk ? "hold to talk" : "hold to talk (mic not ready)";
        break;
    case Stage::Recording:
        snprintf(buf, sizeof(buf), "listening %lu.%lus",
                 (unsigned long)(s.recMs / 1000), (unsigned long)((s.recMs / 100) % 10));
        stage = buf; micColor = C_RED;
        break;
    case Stage::Sending: stage = "sending";        micColor = C_AMBER; break;
    case Stage::Stt:     stage = "transcribing";   micColor = C_AMBER; break;
    case Stage::Think:   stage = "thinking";       micColor = C_AMBER; break;
    case Stage::Reply:
        stage = s.replyDone ? "done" : "receiving answer";
        micColor = s.replyDone ? C_GREEN : C_AMBER;
        break;
    case Stage::Speaking:
        if (s.speakWant && s.speakGot < s.speakWant)
            snprintf(buf, sizeof(buf), "receiving speech %lu%%", (unsigned long)(100UL * s.speakGot / s.speakWant));
        else
            snprintf(buf, sizeof(buf), "speaking");
        stage = buf; micColor = C_PURPLE;
        break;
    case Stage::Busy:
        stage = "finishing previous request"; micColor = C_AMBER;
        break;
    case Stage::Error:
        snprintf(buf, sizeof(buf), "error: %s", s.error); stage = buf; micColor = C_RED;
        break;
    }
    lv_label_set_text(_lblStage, stage);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(micColor), 0);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(micColor), LV_STATE_PRESSED);

    // stop lights up only while there is something to stop
    bool canStop = s.stage != Stage::Idle && s.stage != Stage::Error &&
                   !(s.stage == Stage::Reply && s.replyDone);
    lv_obj_set_style_bg_color(_btnStop, lv_color_hex(canStop ? C_RED : C_DIM), 0);

    if (recording) {
        lv_obj_remove_flag(_bar, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_style_bg_color(_bar, lv_color_hex(C_GREEN), LV_PART_INDICATOR);
        lv_bar_set_value(_bar, (int)(s.level * 100), LV_ANIM_OFF);
    } else if (s.stage == Stage::Speaking && s.speakWant) {
        lv_obj_remove_flag(_bar, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_style_bg_color(_bar, lv_color_hex(C_PURPLE), LV_PART_INDICATOR);
        lv_bar_set_value(_bar, (int)(100UL * s.speakGot / s.speakWant), LV_ANIM_OFF);
    } else {
        lv_obj_add_flag(_bar, LV_OBJ_FLAG_HIDDEN);
    }

    // Scroll to the answer only when it actually grew. Never pass LV_COORD_MAX to lv_obj_scroll_to_y:
    // it is ~5.4e8, and the resulting scroll arithmetic overflows and scrambles every child of _box
    // (both bubbles render as garbage). lv_obj_scroll_to_view does the clamped, correct thing.
    size_t replyLen = strlen(s.reply);
    if (replyLen != _lastReplyLen) {
        _lastReplyLen = replyLen;
        if (replyLen) lv_obj_scroll_to_view(_lblAi, LV_ANIM_OFF);
    }
}

// Registered only when the build carries the app (menuconfig: AgentZero watch apps).
// Unregistered, it never runs init(): no chat_tx / chat_mic / chat_spk tasks.
#if CONFIG_WATCH_APP_CHAT
ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, VoiceChat, APP_NAME, []()
{
    return std::shared_ptr<VoiceChat>(VoiceChat::requestInstance(), [](VoiceChat * p) {});
})
#endif

} // namespace esp_brookesia::apps
