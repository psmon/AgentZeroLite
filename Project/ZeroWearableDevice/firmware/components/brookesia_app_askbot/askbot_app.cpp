#include <cstdio>
#include <cstring>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:AskBot"
#include "esp_lib_utils.h"
#include "askbot_app.hpp"

#define APP_NAME "AskBot"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using askbot::Core;
using askbot::Link;
using askbot::Snapshot;
using askbot::Stage;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_askbot_112_112);
LV_FONT_DECLARE(font_nanum_18);          // shared with brookesia_app_claude_hud (Latin + Hangul)

namespace esp_brookesia::apps {

static const uint32_t C_BG     = 0x000000;
static const uint32_t C_WHITE  = 0xFFFFFF;
static const uint32_t C_GRAY   = 0x8A8F98;
static const uint32_t C_DIM    = 0x232733;
static const uint32_t C_GREEN  = 0x3DD68C;
static const uint32_t C_AMBER  = 0xF5A524;
static const uint32_t C_RED    = 0xFF5C5C;
static const uint32_t C_CYAN   = 0x5CE1E6;
static const uint32_t C_BLUE   = 0x4C8DFF;
static const uint32_t C_PURPLE = 0xA78BFA;

// There is no on-screen keyboard on this panel (the Chat app has none either), so
// questions come from a short rotation. Keep them one line each: the pill is 200 px.
static const char *PRESETS[] = {
    "What time is it? Answer in one short sentence.",
    "지금 이 기기에서 액터 모델이 동작하고 있어? 한 문장으로.",
    "Tell me one short fact about the actor model.",
    // The host's agent has tools on the PC; this one is a whole round trip you can hear.
    "내 음악 조회해서 한 곡 재생해줘.",
};
static const char *PRESET_LABELS[] = { "time", "actor?", "fact", "music" };
static constexpr int PRESET_COUNT = sizeof(PRESETS) / sizeof(PRESETS[0]);

// Same circle-aware geometry as the Chat app: at a given y the usable half-width is
// sqrt(233^2 - (y-233)^2), so rows near the rim have to stay narrow.
static constexpr int W_LINK = 232, Y_LINK = 30;
static constexpr int Y_ROW = 56, H_ROW = 28;
static constexpr int W_PRESET = 146, X_PRESET = -46;
static constexpr int W_NEW = 84, X_NEW = 77;
static constexpr int W_BOX = 344, Y_BOX = 92, H_BOX = 182;
static constexpr int Y_STAGE = 282, Y_BAR = 304;
static constexpr int MIC_D = 88, Y_MIC = 318;      // hold to talk, same place as the Chat app
static constexpr int SIDE_D = 52, Y_SIDE = 336, X_SIDE = 96;

AskBot *AskBot::_instance = nullptr;

AskBot *AskBot::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) {
        _instance = new (std::nothrow) AskBot(use_status_bar, use_navigation_bar);
        ESP_UTILS_CHECK_NULL_RETURN(_instance, nullptr, "Create instance failed");
    }
    return _instance;
}

AskBot::AskBot(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_askbot_112_112, true, use_status_bar, use_navigation_bar)
{
}

AskBot::~AskBot() {}

// Same shape as the Chat app: the link comes up at install time (i.e. at boot), so
// the association survives the app being closed and an answer in flight still
// lands. WiFi itself belongs to the device and the Settings app already started it.
bool AskBot::init(void)
{
    Core::instance().start();
    return true;
}

bool AskBot::run(void)
{
    Core::instance().start();       // idempotent; already running from init()
    buildUi(lv_scr_act());
    _seen = 0;
    _lastReplyLen = 0;
    refresh();
    _timer = lv_timer_create(timerCb, 120, this);
    return true;
}

bool AskBot::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

// The link task keeps running: an answer in flight should still land, and
// re-associating on every open would cost a handshake each time.
bool AskBot::close(void)
{
    _timer = nullptr;
    return true;
}

void AskBot::timerCb(lv_timer_t *t)
{
    auto *self = (AskBot *)lv_timer_get_user_data(t);
    if (self) self->refresh();
}

// ---------------------------------------------------------------- events
void AskBot::askEvent(lv_event_t *e)
{
    auto *self = (AskBot *)lv_event_get_user_data(e);
    if (self) Core::instance().sendText(PRESETS[self->_preset]);
}

// Hold to talk: press starts the capture, release ends it and the host takes over with STT.
// PRESS_LOST matters - a finger sliding off the button must not leave it recording.
void AskBot::micEvent(lv_event_t *e)
{
    const auto code = lv_event_get_code(e);
    if (code == LV_EVENT_PRESSED) {
        Core::instance().startVoice(30000);
    } else if (code == LV_EVENT_RELEASED || code == LV_EVENT_PRESS_LOST) {
        Core::instance().stopVoice();
    }
}

void AskBot::presetEvent(lv_event_t *e)
{
    auto *self = (AskBot *)lv_event_get_user_data(e);
    if (self) {
        self->_preset = (self->_preset + 1) % PRESET_COUNT;
        lv_label_set_text(self->_lblPreset, PRESET_LABELS[self->_preset]);
    }
}

void AskBot::stopEvent(lv_event_t *) { Core::instance().cancel(); }

// Tap to cycle: text only -> spoken on the watch -> spoken on the PC -> text only. The
// host synthesises with SuperTonic; for the watch it streams IMA ADPCM frames, for the
// PC it plays the answer on the PC's speakers and sends no audio. The pill only appears
// when the host reported a usable voice, and PC is skipped when it did not offer it.
void AskBot::modeEvent(lv_event_t *)
{
    using askbot::AnswerMode;
    Core &c = Core::instance();
    askbot::Snapshot s;
    c.snapshot(s);
    const bool pc = s.hostPcOut;
    switch (c.mode()) {
    case AnswerMode::TextOnly:     c.setMode(AnswerMode::TextAndVoice); break;
    case AnswerMode::TextAndVoice: c.setMode(pc ? AnswerMode::TextAndPc : AnswerMode::TextOnly); break;
    case AnswerMode::TextAndPc:    c.setMode(AnswerMode::TextOnly); break;
    }
}

// The chat CLI owns the history (netclaw resumes by session id); this only asks the
// host actor to move to a fresh one. The old conversation is left behind, not deleted.
void AskBot::newChatEvent(lv_event_t *) { Core::instance().newChat(); }

// ---------------------------------------------------------------- UI helpers
static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    return l;
}

// `code` must be a specific event. LV_EVENT_ALL here would fire on every draw event
// too, which in the Chat app once turned a button into a prompt flood.
static lv_obj_t *mkBtn(lv_obj_t *parent, int w, int h, int radius, uint32_t color,
                       lv_event_cb_t cb, lv_event_code_t code, void *user)
{
    lv_obj_t *b = lv_button_create(parent);
    lv_obj_set_size(b, w, h);
    lv_obj_set_style_radius(b, radius, 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), LV_STATE_PRESSED);
    lv_obj_set_style_shadow_width(b, 0, 0);
    lv_obj_set_style_pad_all(b, 0, 0);
    lv_obj_add_event_cb(b, cb, code, user);
    return b;
}

void AskBot::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    _lblLink = mkLabel(scr, "starting WiFi", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblLink, W_LINK);
    lv_label_set_long_mode(_lblLink, LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_align(_lblLink, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblLink, LV_ALIGN_TOP_MID, 0, Y_LINK);

    _btnPreset = mkBtn(scr, W_PRESET, H_ROW, H_ROW / 2, C_DIM, presetEvent, LV_EVENT_CLICKED, this);
    lv_obj_align(_btnPreset, LV_ALIGN_TOP_MID, X_PRESET, Y_ROW);
    _lblPreset = mkLabel(_btnPreset, PRESET_LABELS[0], &font_nanum_18, C_GRAY);
    lv_obj_center(_lblPreset);

    _btnNew = mkBtn(scr, W_NEW, H_ROW, H_ROW / 2, C_DIM, newChatEvent, LV_EVENT_CLICKED, this);
    lv_obj_align(_btnNew, LV_ALIGN_TOP_MID, X_NEW, Y_ROW);
    lv_obj_center(mkLabel(_btnNew, "New chat", &font_nanum_18, C_GRAY));

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

    _lblStage = mkLabel(scr, "", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblStage, 236);
    lv_label_set_long_mode(_lblStage, LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_align(_lblStage, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblStage, LV_ALIGN_TOP_MID, 0, Y_STAGE);

    // Centre: the microphone, held down to speak.
    _btnMic = mkBtn(scr, MIC_D, MIC_D, LV_RADIUS_CIRCLE, C_BLUE, micEvent, LV_EVENT_PRESSED, this);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_RELEASED, this);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_PRESS_LOST, this);
    lv_obj_align(_btnMic, LV_ALIGN_TOP_MID, 0, Y_MIC);
    _lblMic = mkLabel(_btnMic, LV_SYMBOL_AUDIO, &lv_font_montserrat_22, C_WHITE);
    lv_obj_center(_lblMic);

    // Left: the preset question, for when talking is not an option.
    _btnAsk = mkBtn(scr, SIDE_D, SIDE_D, LV_RADIUS_CIRCLE, C_DIM, askEvent, LV_EVENT_CLICKED, this);
    lv_obj_align(_btnAsk, LV_ALIGN_TOP_MID, -X_SIDE, Y_SIDE);
    _lblAsk = mkLabel(_btnAsk, "T", &lv_font_montserrat_20, C_WHITE);
    lv_obj_center(_lblAsk);

    _btnStop = mkBtn(scr, SIDE_D, SIDE_D, LV_RADIUS_CIRCLE, C_DIM, stopEvent, LV_EVENT_CLICKED, this);
    lv_obj_align(_btnStop, LV_ALIGN_TOP_MID, X_SIDE, Y_SIDE);
    lv_obj_center(mkLabel(_btnStop, LV_SYMBOL_STOP, &lv_font_montserrat_20, C_WHITE));

    _btnMode = mkBtn(scr, SIDE_D, SIDE_D, LV_RADIUS_CIRCLE, C_DIM, modeEvent, LV_EVENT_CLICKED, this);
    lv_obj_align(_btnMode, LV_ALIGN_TOP_MID, -X_SIDE - 48, Y_STAGE - 10);
    _lblMode = mkLabel(_btnMode, LV_SYMBOL_MUTE, &lv_font_montserrat_20, C_GRAY);
    lv_obj_center(_lblMode);
    lv_obj_add_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);

    // Doubles as the speech-download progress while frames are arriving.
    _bar = lv_bar_create(scr);
    lv_obj_set_size(_bar, 200, 6);
    lv_obj_align(_bar, LV_ALIGN_TOP_MID, 0, Y_BAR);
    lv_bar_set_range(_bar, 0, 100);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_PURPLE), LV_PART_INDICATOR);
    lv_obj_add_flag(_bar, LV_OBJ_FLAG_HIDDEN);
}

void AskBot::refresh()
{
    Core &core = Core::instance();
    const uint32_t v = core.version();
    if (v == _seen) return;
    _seen = v;

    Snapshot s;
    core.snapshot(s);

    char buf[192];
    switch (s.link) {
    case Link::Down:            snprintf(buf, sizeof(buf), "no host · %s", s.error); break;
    case Link::WifiConnecting:  snprintf(buf, sizeof(buf), "connecting WiFi"); break;
    case Link::WifiFailed:      snprintf(buf, sizeof(buf), "WiFi failed · %s", s.error); break;
    case Link::Associating:     snprintf(buf, sizeof(buf), "associating (%s)", s.ip); break;
    case Link::Up:
        if (!s.hostOnline)      snprintf(buf, sizeof(buf), "associated, waiting for host");
        else if (s.chatNo > 1)  snprintf(buf, sizeof(buf), "%s · %s · chat %d", s.host, s.provider, s.chatNo);
        else                    snprintf(buf, sizeof(buf), "%s · %s", s.host, s.provider);
        break;
    }
    lv_label_set_text(_lblLink, buf);

    lv_label_set_text(_lblUser, s.transcript[0] ? s.transcript : (s.question[0] ? s.question : ""));
    if (s.reply[0])                                     lv_label_set_text(_lblAi, s.reply);
    else if (s.stage == Stage::Idle && !s.question[0])   lv_label_set_text(_lblAi, "Hold the microphone and speak.");
    else                                                lv_label_set_text(_lblAi, "");

    const char *stage = "";
    uint32_t askColor = C_PURPLE;
    switch (s.stage) {
    case Stage::Idle:
        stage = s.link == Link::Up ? (s.micOk ? "hold to talk" : "ready") : "waiting for the link";
        break;
    case Stage::Recording:
        snprintf(buf, sizeof(buf), "listening %lu.%lus", (unsigned long)(s.recMs / 1000),
                 (unsigned long)((s.recMs / 100) % 10));
        stage = buf;
        askColor = C_RED;
        break;
    case Stage::Stt:      stage = "transcribing"; askColor = C_AMBER; break;
    case Stage::Sending: stage = "sending";  askColor = C_AMBER; break;
    case Stage::Think:   stage = "thinking"; askColor = C_AMBER; break;
    case Stage::Reply:
        if (s.replyDone) {
            snprintf(buf, sizeof(buf), "done · %lu chunk(s) · %lu ms",
                     (unsigned long)s.chunks, (unsigned long)s.askMs);
            askColor = C_GREEN;
        } else {
            snprintf(buf, sizeof(buf), "receiving answer · %lu chunk(s)", (unsigned long)s.chunks);
            askColor = C_AMBER;
        }
        stage = buf;
        break;
    case Stage::Speaking:
        if (s.speakWant && s.speakGot < s.speakWant) {
            snprintf(buf, sizeof(buf), "receiving speech %lu%%",
                     (unsigned long)(100UL * s.speakGot / s.speakWant));
        } else {
            snprintf(buf, sizeof(buf), "speaking %lu.%lus", (unsigned long)(s.speakMs / 1000),
                     (unsigned long)((s.speakMs / 100) % 10));
        }
        stage = buf;
        askColor = C_PURPLE;
        break;
    case Stage::Error:
        snprintf(buf, sizeof(buf), "error: %s", s.error);
        stage = buf;
        askColor = C_RED;
        break;
    }
    lv_label_set_text(_lblStage, stage);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(askColor), 0);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(askColor), LV_STATE_PRESSED);

    const bool canStop = s.stage == Stage::Recording || s.stage == Stage::Sending ||
                         s.stage == Stage::Stt || s.stage == Stage::Think ||
                         s.stage == Stage::Speaking || (s.stage == Stage::Reply && !s.replyDone);
    lv_obj_set_style_bg_color(_btnStop, lv_color_hex(canStop ? C_RED : C_DIM), 0);

    // The voice toggle means nothing until the host says it can speak.
    if (s.hostTts) {
        lv_obj_remove_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);
        // Mute = text only, speaker = the watch speaks, "PC" = the PC speaks.
        switch (s.mode) {
        case askbot::AnswerMode::TextAndVoice:
            lv_label_set_text(_lblMode, LV_SYMBOL_VOLUME_MAX);
            lv_obj_set_style_text_color(_lblMode, lv_color_hex(C_PURPLE), 0);
            break;
        case askbot::AnswerMode::TextAndPc:
            lv_label_set_text(_lblMode, "PC");
            lv_obj_set_style_text_color(_lblMode, lv_color_hex(C_CYAN), 0);
            break;
        default:
            lv_label_set_text(_lblMode, LV_SYMBOL_MUTE);
            lv_obj_set_style_text_color(_lblMode, lv_color_hex(C_GRAY), 0);
            break;
        }
    } else {
        lv_obj_add_flag(_btnMode, LV_OBJ_FLAG_HIDDEN);
    }

    if (s.stage == Stage::Recording) {
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

    // Scroll only when the answer actually grew, and never with LV_COORD_MAX: that
    // overflows the scroll arithmetic and scrambles every child of the box.
    const size_t replyLen = strlen(s.reply);
    if (replyLen != _lastReplyLen) {
        _lastReplyLen = replyLen;
        if (replyLen) lv_obj_scroll_to_view(_lblAi, LV_ANIM_OFF);
    }
}

ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, AskBot, APP_NAME, []()
{
    return std::shared_ptr<AskBot>(AskBot::requestInstance(), [](AskBot * p) {});
})

} // namespace esp_brookesia::apps
