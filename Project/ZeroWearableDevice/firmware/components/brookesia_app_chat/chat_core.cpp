#include "chat_core.hpp"
#include <cstring>
#include <cstdio>
#include <cmath>
#include <algorithm>
#include "esp_log.h"
#include "esp_timer.h"
#include "cJSON.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "bsp/esp-bsp.h"
#include "esp_codec_dev.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "hud_transport.hpp"
#include "device_voice.hpp"
#include "device_power.hpp"
#include "device_mic.hpp"

static const char *TAG = "chat_core";

namespace voice_chat {

using namespace claude_hud;

static constexpr int      SAMPLE_RATE = 16000;
static constexpr size_t   MAX_SAMPLES_PER_BLOCK = 960;      // 60 ms
static constexpr uint32_t HARD_MAX_MS = 60000;
// Answer audio is buffered whole before playback: BLE delivers ~8 kB/s of ADPCM while the speaker
// eats 32 kB/s of PCM, so playing as it arrives would stutter. 20 s of PCM16 = 640 kB per buffer,
// two buffers, in PSRAM.
static constexpr size_t   SPK_MAX_BYTES = 20 * SAMPLE_RATE * 2;
static constexpr const char *NVS_NS = "chat";

static inline uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

// ---------------------------------------------------------------- IMA ADPCM (block = [pred i16][idx u8][0][nibbles])
static const int IDX_TAB[16] = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
static const int STEP_TAB[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
    118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876,
    963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
    5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385,
    24623, 27086, 29794, 32767 };

struct AdpcmState { int predictor = 0; int index = 0; };

// Encodes n samples (n even) into out; returns bytes written = 4 + n/2. Mirrors ChatHost ImaAdpcm.EncodeBlock.
static size_t adpcmEncodeBlock(const int16_t *s, size_t n, AdpcmState &st, uint8_t *out)
{
    out[0] = (uint8_t)st.predictor; out[1] = (uint8_t)(st.predictor >> 8); out[2] = (uint8_t)st.index; out[3] = 0;
    for (size_t i = 0; i < n; i++) {
        int step = STEP_TAB[st.index];
        int diff = s[i] - st.predictor;
        int nib = 0;
        if (diff < 0) { nib = 8; diff = -diff; }
        int vp = step >> 3;
        if (diff >= step)        { nib |= 4; diff -= step;      vp += step; }
        if (diff >= (step >> 1)) { nib |= 2; diff -= step >> 1; vp += step >> 1; }
        if (diff >= (step >> 2)) { nib |= 1;                    vp += step >> 2; }
        int p = (nib & 8) ? st.predictor - vp : st.predictor + vp;
        st.predictor = p < -32768 ? -32768 : (p > 32767 ? 32767 : p);
        int idx = st.index + IDX_TAB[nib];
        st.index = idx < 0 ? 0 : (idx > 88 ? 88 : idx);
        if ((i & 1) == 0) out[4 + i / 2] = (uint8_t)nib; else out[4 + i / 2] |= (uint8_t)(nib << 4);
    }
    return 4 + (n + 1) / 2;
}

// Decode one self-contained IMA ADPCM block into PCM16. Mirror of adpcmEncodeBlock / the host's
// ImaAdpcm.DecodeBlock; each block carries its own predictor so a lost frame costs one block only.
static size_t adpcmDecodeBlock(const uint8_t *blk, size_t n, uint8_t *out, size_t outCap)
{
    if (n < 4) return 0;
    int predictor = (int16_t)(blk[0] | (blk[1] << 8));
    int index = blk[2] > 88 ? 88 : blk[2];
    size_t w = 0;
    auto step = [&](int nib) {
        int st = STEP_TAB[index];
        int diff = st >> 3;
        if (nib & 1) diff += st >> 2;
        if (nib & 2) diff += st >> 1;
        if (nib & 4) diff += st;
        int p = (nib & 8) ? predictor - diff : predictor + diff;
        predictor = p < -32768 ? -32768 : (p > 32767 ? 32767 : p);
        int idx = index + IDX_TAB[nib];
        index = idx < 0 ? 0 : (idx > 88 ? 88 : idx);
        if (w + 2 <= outCap) { out[w] = (uint8_t)predictor; out[w + 1] = (uint8_t)(predictor >> 8); w += 2; }
    };
    for (size_t i = 4; i < n; i++) { step(blk[i] & 0x0F); step(blk[i] >> 4); }
    return w;
}

// ---------------------------------------------------------------- singleton / hooks
Core &Core::instance() { static Core c; return c; }

static bool lineHookTrampoline(const char *line, size_t len) { return Core::instance().onLine(line, len); }
static bool frameHookTrampoline(const uint8_t *d, size_t n) { return Core::instance().onFrame(d, n); }
static void playTaskTrampoline(void *) { Core::instance().playTask(); }
static void captureTaskTrampoline(void *) { Core::instance().captureTask(); }
static void txTaskTrampoline(void *) { Core::instance().txTask(); }

// Outbound line handed from the NimBLE host task (or the UI) to the tx task. The queue carries a heap
// pointer, not the buffer: a 400-byte struct on the stack of the NimBLE host task is a real cost there.
static constexpr size_t TX_MAX = 400;

void Core::init()
{
    if (task_) return;
    setLineHook(lineHookTrampoline);
    setFrameHook(frameHookTrampoline);

    // answer-mode setting survives a reboot
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READONLY, &nvs) == ESP_OK) {
        uint8_t v = 0;
        if (nvs_get_u8(nvs, "mode", &v) == ESP_OK) s_.mode = (v == 1) ? AnswerMode::TextAndVoice : AnswerMode::TextOnly;
        if (nvs_get_u8(nvs, "vol", &v) == ESP_OK) s_.volume = v > 100 ? 100 : v;
        if (nvs_get_u8(nvs, "gain", &v) == ESP_OK) s_.micGain = v > 60 ? 60 : v;
        nvs_close(nvs);
    }
    // The tx task exists so a reply to an inbound line never calls into NimBLE from inside its own
    // GATT-write callback (the host lock is held there; notifying from it can deadlock).
    txQueue_ = xQueueCreate(4, sizeof(char *));
    TaskHandle_t t = nullptr;
    xTaskCreatePinnedToCore(txTaskTrampoline, "chat_tx", 4096, nullptr, 4, &t, 0);
    txTask_ = t;
    // Capture task: internal-RAM stack (I2S DMA reads), core 1 keeps it away from the NimBLE host task on core 0.
    TaskHandle_t h = nullptr;
    xTaskCreatePinnedToCore(captureTaskTrampoline, "chat_mic", 6144, nullptr, 5, &h, 1);
    task_ = h;
    TaskHandle_t pl = nullptr;
    xTaskCreatePinnedToCore(playTaskTrampoline, "chat_spk", 4096, nullptr, 5, &pl, 1);
    playTask_ = pl;
    ESP_LOGI(TAG, "init: hooks set, capture + tx + play tasks started (answer mode %s)",
             s_.mode == AnswerMode::TextAndVoice ? "text+voice" : "text");
}

void Core::txTask()
{
    for (;;) {
        char *msg = nullptr;
        if (xQueueReceive((QueueHandle_t)txQueue_, &msg, portMAX_DELAY) == pdTRUE && msg) {
            if (!sendLine(msg)) ESP_LOGW(TAG, "tx dropped: %.60s", msg);
            free(msg);
        }
    }
}

bool Core::queueLine(const char *json)
{
    if (!txQueue_ || !json) return false;
    size_t n = strnlen(json, TX_MAX - 1) + 1;
    char *msg = (char *)malloc(n);
    if (!msg) return false;
    memcpy(msg, json, n - 1);
    msg[n - 1] = 0;
    if (xQueueSend((QueueHandle_t)txQueue_, &msg, 0) != pdTRUE) { free(msg); return false; }
    return true;
}

// The codec is a device service now (device_mic): this app holds it only while recording,
// so AskBot can record too. Keeping the handle open for the life of the firmware is exactly
// what used to make that impossible.
bool Core::micInit()
{
    if (!device_mic::acquire()) return false;
    mic_ = (void *)1;                  // "we are holding it"; the handle lives in device_mic
    { std::lock_guard<std::mutex> g(m_); s_.micOk = true; bump(); }
    return true;
}

void Core::micDone()
{
    if (!mic_) return;
    mic_ = nullptr;
    device_mic::release();
}

// ---------------------------------------------------------------- state helpers
void Core::setStage(Stage st, const char *err)
{
    std::lock_guard<std::mutex> g(m_);
    s_.stage = st;
    if (err) snprintf(s_.error, sizeof(s_.error), "%s", err);
    else if (st != Stage::Error) s_.error[0] = 0;
    bump();
}

void Core::snapshot(Snapshot &out)
{
    std::lock_guard<std::mutex> g(m_);
    s_.bleConnected = bleConnected();
    out = s_;
}

uint32_t Core::version() { std::lock_guard<std::mutex> g(m_); return ver_; }

AnswerMode Core::mode()
{
    std::lock_guard<std::mutex> g(m_);
    return s_.mode;
}

void Core::setMode(AnswerMode m)
{
    {
        std::lock_guard<std::mutex> g(m_);
        if (s_.mode == m) return;
        s_.mode = m;
        bump();
    }
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_u8(nvs, "mode", (uint8_t)m);
        nvs_commit(nvs);
        nvs_close(nvs);
    }
    ESP_LOGI(TAG, "answer mode -> %s", m == AnswerMode::TextAndVoice ? "text+voice" : "text");
    if (m == AnswerMode::TextOnly) playAbort_ = true;
}

int Core::volume()
{
    std::lock_guard<std::mutex> g(m_);
    return s_.volume;
}

void Core::setVolume(int v)
{
    v = v < 0 ? 0 : (v > 100 ? 100 : v);
    void *spk;
    {
        std::lock_guard<std::mutex> g(m_);
        if (s_.volume == v) return;
        s_.volume = v;
        spk = spk_;
        bump();
    }
    if (spk) esp_codec_dev_set_out_vol((esp_codec_dev_handle_t)spk, (float)v);
    nvs_handle_t nvs;
    if (nvs_open(NVS_NS, NVS_READWRITE, &nvs) == ESP_OK) {
        nvs_set_u8(nvs, "vol", (uint8_t)v);
        nvs_commit(nvs);
        nvs_close(nvs);
    }
    ESP_LOGI(TAG, "speaker volume -> %d", v);
}

int Core::micGain()
{
    return device_mic::gain();
}

void Core::setMicGain(int db)
{
    device_mic::setGain(db);
    std::lock_guard<std::mutex> g(m_);
    s_.micGain = device_mic::gain();
    bump();
}

// A 0.4 s tone, so moving the volume slider does something audible without asking the host to speak.
// Goes through the normal playback path: same buffers, same codec-open logic.
void Core::playTestTone()
{
    std::lock_guard<std::mutex> g(m_);
    int b = (playIdx_ == 0) ? 1 : 0;
    if (!spkBuf_[b]) {
        spkBuf_[b] = (uint8_t *)heap_caps_malloc(SPK_MAX_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        if (!spkBuf_[b]) return;
    }
    const int n = SAMPLE_RATE * 2 / 5;                 // 0.4 s
    for (int i = 0; i < n; i++) {
        double env = i < 400 ? i / 400.0 : (i > n - 400 ? (n - i) / 400.0 : 1.0);   // avoid a click
        int16_t v = (int16_t)(std::sin(2 * M_PI * 660.0 * i / SAMPLE_RATE) * 9000 * env);
        spkBuf_[b][i * 2] = (uint8_t)v;
        spkBuf_[b][i * 2 + 1] = (uint8_t)(v >> 8);
    }
    spkLen_[b] = (size_t)n * 2;
    fillIdx_ = b;
    pendingIdx_ = b;
    playAbort_ = true;                                 // cut off anything already speaking
    playReady_ = true;
}

// Stop everything that is in flight: recording, the host request and any answer playback.
void Core::cancel()
{
    int id;
    { std::lock_guard<std::mutex> g(m_); id = s_.reqId; }
    bool wasRecording = recording_;
    recording_ = false;
    playAbort_ = true;
    playReady_ = false;
    {
        std::lock_guard<std::mutex> g(m_);
        spkLen_[0] = spkLen_[1] = 0;
        pendingIdx_ = -1; spkId_ = -1;
        s_.speakGot = s_.speakWant = 0;
    }

    char js[48];
    snprintf(js, sizeof(js), "{\"t\":\"cancel\",\"id\":%d}", id);
    queueLine(js);
    { std::lock_guard<std::mutex> g(m_); s_.stage = Stage::Idle; s_.error[0] = 0; bump(); }
    ESP_LOGI(TAG, "cancel #%d (was %s)", id, wasRecording ? "recording" : "waiting");
}

// The conversation itself lives in the chat CLI (netclaw resumes it by session id and keeps the history
// on its own side), so "new chat" is only a request for the host to switch to a fresh id. Nothing here
// stores the transcript; we just wipe what is on screen.
void Core::newChat()
{
    cancel();
    {
        std::lock_guard<std::mutex> g(m_);
        s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
        s_.stage = Stage::Idle;
        bump();
    }
    int id;
    { std::lock_guard<std::mutex> g(m_); id = s_.reqId; }
    char js[56];
    snprintf(js, sizeof(js), "{\"t\":\"newsession\",\"id\":%d}", id);
    queueLine(js);
    ESP_LOGI(TAG, "new conversation requested");
}

void Core::clear()
{
    std::lock_guard<std::mutex> g(m_);
    s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
    s_.stage = Stage::Idle;
    bump();
}

// Runs on the tx task only (never the NimBLE host task) - see queueLine.
bool Core::sendLine(const char *json)
{
    char buf[TX_MAX + 8];
    int n = snprintf(buf, sizeof(buf), "R %s", json);
    if (n <= 0 || n >= (int)sizeof(buf)) return false;
    return bleSendLine(buf, (size_t)n);
}

// ---------------------------------------------------------------- user actions
bool Core::startVoice(uint32_t maxMs)
{
    if (recording_) return false;
    if (!bleConnected()) { setStage(Stage::Error, "BLE not connected"); return false; }
    {
        std::lock_guard<std::mutex> g(m_);
        if (s_.stage == Stage::Stt || s_.stage == Stage::Think) { /* host still busy */ }
        s_.reqId = nextId_;
        nextId_ = (uint8_t)(nextId_ == 255 ? 1 : nextId_ + 1);
        s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
        s_.recMs = 0; s_.level = 0; s_.framesSent = 0; s_.framesDropped = 0;
        s_.stage = Stage::Recording;
        bump();
    }
    maxMs_ = std::min(maxMs == 0 ? HARD_MAX_MS : maxMs, HARD_MAX_MS);
    recording_ = true;                 // the capture task picks this up (it sends the "voice" header itself)
    return true;
}

void Core::stopVoice()
{
    recording_ = false;                // capture task sends "end"
}

bool Core::sendText(const char *text)
{
    if (!text || !*text) return false;
    if (!bleConnected()) { setStage(Stage::Error, "BLE not connected"); return false; }
    int id;
    {
        std::lock_guard<std::mutex> g(m_);
        id = s_.reqId = nextId_;
        nextId_ = (uint8_t)(nextId_ == 255 ? 1 : nextId_ + 1);
        snprintf(s_.transcript, sizeof(s_.transcript), "%s", text);
        s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
        s_.stage = Stage::Sending;
        bump();
    }
    const device_voice::Prefs vp = device_voice::get();
    cJSON *js = cJSON_CreateObject();
    cJSON_AddStringToObject(js, "t", "text");
    cJSON_AddNumberToObject(js, "id", id);
    cJSON_AddStringToObject(js, "text", text);
    cJSON_AddBoolToObject(js, "tts", mode() == AnswerMode::TextAndVoice);
    cJSON_AddStringToObject(js, "outLang", vp.outLang);
    cJSON_AddStringToObject(js, "voice", vp.voice);
    char *out = cJSON_PrintUnformatted(js);
    bool ok = out && queueLine(out);
    cJSON_free(out); cJSON_Delete(js);
    if (!ok) setStage(Stage::Error, "send failed");
    return ok;
}

// ---------------------------------------------------------------- inbound (NimBLE host task)
bool Core::onLine(const char *line, size_t len)
{
    if (len < 3 || line[1] != ' ') return false;
    char tag = line[0];
    cJSON *js = cJSON_ParseWithLength(line + 2, len - 2);
    if (!js) { ESP_LOGW(TAG, "bad json on '%c' line", tag); return false; }
    bool ok = true;

    if (tag == 'H') {                                   // host hello
        const cJSON *h = cJSON_GetObjectItem(js, "host");
        const cJSON *p = cJSON_GetObjectItem(js, "provider");
        {
            std::lock_guard<std::mutex> g(m_);
            snprintf(s_.host, sizeof(s_.host), "%s", cJSON_IsString(h) ? h->valuestring : "?");
            snprintf(s_.provider, sizeof(s_.provider), "%s", cJSON_IsString(p) ? p->valuestring : "?");
            s_.hostTts = cJSON_IsTrue(cJSON_GetObjectItem(js, "tts"));
            const cJSON *ch = cJSON_GetObjectItem(js, "chat");
            if (cJSON_IsNumber(ch)) s_.chatNo = ch->valueint;
            if (!s_.hostTts) s_.mode = AnswerMode::TextOnly;   // no voice on the host: hide the option
            s_.hostOnline = true;
            bump();
        }
        ESP_LOGI(TAG, "host online: %s (%s)", s_.host, s_.provider);
        queueLine("{\"t\":\"hello\",\"name\":\"claude-hud\",\"fw\":\"chat-1\",\"fmt\":\"adpcm\"}");
    } else if (tag == 'A') {                            // answer / progress
        const cJSON *st = cJSON_GetObjectItem(js, "st");
        const cJSON *tx = cJSON_GetObjectItem(js, "text");
        const cJSON *id = cJSON_GetObjectItem(js, "id");
        const char *stage = cJSON_IsString(st) ? st->valuestring : "";
        const char *text = cJSON_IsString(tx) ? tx->valuestring : nullptr;
        int rid = cJSON_IsNumber(id) ? id->valueint : 0;
        std::lock_guard<std::mutex> g(m_);
        if (rid != 0 && rid != s_.reqId && strcmp(stage, "busy") != 0) {
            ESP_LOGD(TAG, "answer for old request %d ignored", rid);
        } else if (!strcmp(stage, "rec")) {
            // host accepted the capture — nothing to show
        } else if (!strcmp(stage, "stt")) {
            s_.stage = Stage::Stt;
            if (text) snprintf(s_.transcript, sizeof(s_.transcript), "%s", text);
        } else if (!strcmp(stage, "think")) {
            s_.stage = Stage::Think;
        } else if (!strcmp(stage, "reply")) {
            const cJSON *seq = cJSON_GetObjectItem(js, "seq");
            if (cJSON_IsNumber(seq) && seq->valueint == 0) s_.reply[0] = 0;
            if (text) {
                size_t cur = strlen(s_.reply);
                snprintf(s_.reply + cur, sizeof(s_.reply) - cur, "%s", text);
            }
            s_.stage = Stage::Reply;
            s_.replyDone = cJSON_IsTrue(cJSON_GetObjectItem(js, "done"));
            if (s_.replyDone) ESP_LOGI(TAG, "reply #%d: %s", rid, s_.reply);
        } else if (!strcmp(stage, "err")) {
            s_.stage = Stage::Error;
            snprintf(s_.error, sizeof(s_.error), "%s", text ? text : "error");
            recording_ = false;
        } else if (!strcmp(stage, "speak")) {
            const cJSON *ms = cJSON_GetObjectItem(js, "ms");
            const cJSON *fr = cJSON_GetObjectItem(js, "frames");
            s_.speakMs = cJSON_IsNumber(ms) ? (uint32_t)ms->valueint : 0;
            s_.speakWant = cJSON_IsNumber(fr) ? (uint32_t)fr->valueint : 0;
            s_.speakGot = 0;
            s_.stage = Stage::Speaking;
            playAbort_ = true;                       // newest answer wins; stop speaking the old one
            fillIdx_ = (playIdx_ == 0) ? 1 : 0;      // never fill the buffer the play task is reading
            spkLen_[fillIdx_] = 0;
            spkId_ = rid; spkLastSeq_ = -1;
            playReady_ = false;
            ESP_LOGI(TAG, "answer audio #%d: %lu ms in %lu frames (buffer %d)", rid,
                     (unsigned long)s_.speakMs, (unsigned long)s_.speakWant, fillIdx_);
        } else if (!strcmp(stage, "speak_end")) {
            if (text) ESP_LOGW(TAG, "speech: %s", text);
            ESP_LOGI(TAG, "answer audio #%d complete: %u frames, %u bytes pcm",
                     rid, (unsigned)s_.speakGot, (unsigned)spkLen_[fillIdx_]);
            pendingIdx_ = fillIdx_;
            playReady_ = spkLen_[fillIdx_] > 0;
            if (!playReady_) s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
        } else if (!strcmp(stage, "session")) {
            const cJSON *n = cJSON_GetObjectItem(js, "n");
            if (cJSON_IsNumber(n)) s_.chatNo = n->valueint;
            s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
            s_.stage = Stage::Idle;
            ESP_LOGI(TAG, "now on conversation #%d", s_.chatNo);
        } else if (!strcmp(stage, "idle")) {
            s_.stage = Stage::Idle;
        } else if (!strcmp(stage, "busy")) {
            s_.stage = Stage::Busy;
            recording_ = false;
        } else if (!strcmp(stage, "pong")) {
            // ignore
        } else ok = false;
        bump();
    } else if (tag == 'C') {                            // host debug command: {"cmd":"talk","ms":3000} | {"cmd":"text","text":".."}
        const cJSON *cmd = cJSON_GetObjectItem(js, "cmd");
        const char *c = cJSON_IsString(cmd) ? cmd->valuestring : "";
        if (!strcmp(c, "talk")) {
            const cJSON *ms = cJSON_GetObjectItem(js, "ms");
            uint32_t d = cJSON_IsNumber(ms) ? (uint32_t)ms->valueint : 3000;
            ESP_LOGI(TAG, "host asked for a %lu ms capture", (unsigned long)d);
            ok = startVoice(d);
        } else if (!strcmp(c, "text")) {
            const cJSON *tx = cJSON_GetObjectItem(js, "text");
            ok = cJSON_IsString(tx) && sendText(tx->valuestring);
        } else if (!strcmp(c, "clear")) {
            clear();
        } else if (!strcmp(c, "vol")) {
            const cJSON *v = cJSON_GetObjectItem(js, "v");
            if (cJSON_IsNumber(v)) { setVolume(v->valueint); playTestTone(); } else ok = false;
        } else if (!strcmp(c, "gain")) {
            const cJSON *v = cJSON_GetObjectItem(js, "db");
            if (cJSON_IsNumber(v)) setMicGain(v->valueint); else ok = false;
        } else if (!strcmp(c, "tone")) {
            playTestTone();
        } else if (!strcmp(c, "screen")) {
            // {"cmd":"screen","on":false} - omit "on" to toggle. The BOOT button is the normal way;
            // this exists so a PC can exercise the path without a finger on the case.
            const cJSON *v = cJSON_GetObjectItem(js, "on");
            device_power::setScreen(cJSON_IsBool(v) ? cJSON_IsTrue(v) : !device_power::screenOn());
        } else if (!strcmp(c, "reboot")) {
            device_power::reboot("host command");
        } else if (!strcmp(c, "voicecfg")) {
            // {"cmd":"voicecfg","in":"ko","out":"en","voice":"M2"} - any subset. The screen
            // is the normal way to change these; this exists so a PC can too.
            const cJSON *in = cJSON_GetObjectItem(js, "in");
            const cJSON *out = cJSON_GetObjectItem(js, "out");
            const cJSON *vo = cJSON_GetObjectItem(js, "voice");
            if (cJSON_IsString(in)) ok &= device_voice::setInLang(in->valuestring);
            if (cJSON_IsString(out)) ok &= device_voice::setOutLang(out->valuestring);
            if (cJSON_IsString(vo)) ok &= device_voice::setVoice(vo->valuestring);
            const device_voice::Prefs vp = device_voice::get();
            ESP_LOGI(TAG, "voice prefs now in=%s out=%s voice=%s", vp.inLang, vp.outLang, vp.voice);
            bump();
        } else if (!strcmp(c, "newchat")) {
            newChat();
        } else if (!strcmp(c, "mode")) {
            const cJSON *v = cJSON_GetObjectItem(js, "voice");
            setMode(cJSON_IsTrue(v) ? AnswerMode::TextAndVoice : AnswerMode::TextOnly);
        } else ok = false;
    } else ok = false;

    cJSON_Delete(js);
    return ok;
}

// ---------------------------------------------------------------- capture task
void Core::captureTask()
{
    int16_t *pcm = (int16_t *)heap_caps_malloc(MAX_SAMPLES_PER_BLOCK * sizeof(int16_t), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    uint8_t *frame = (uint8_t *)heap_caps_malloc(4 + 4 + MAX_SAMPLES_PER_BLOCK / 2 + 8, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!pcm || !frame) { ESP_LOGE(TAG, "no memory for capture buffers"); vTaskDelete(nullptr); return; }

    for (;;) {
        if (!recording_) { vTaskDelay(pdMS_TO_TICKS(20)); continue; }

        if (!micInit()) {
            recording_ = false;
            setStage(Stage::Error, "microphone unavailable");
            continue;
        }

        int id;
        { std::lock_guard<std::mutex> g(m_); id = s_.reqId; }

        // header line first; then frames; stop when released, too long, disconnected or the host says busy/err
        // lang tells whisper what to expect (better recognition than letting it guess);
        // outLang/voice choose how the answer is spoken. Both come from the Settings screen.
        const device_voice::Prefs vp = device_voice::get();
        char hdr[192];
        snprintf(hdr, sizeof(hdr),
                 "{\"t\":\"voice\",\"id\":%d,\"fmt\":\"adpcm\",\"rate\":%d,\"ch\":1,\"tts\":%s,"
                 "\"lang\":\"%s\",\"outLang\":\"%s\",\"voice\":\"%s\"}",
                 id, SAMPLE_RATE, mode() == AnswerMode::TextAndVoice ? "true" : "false",
                 vp.inLang, vp.outLang, vp.voice);
        if (!sendLine(hdr)) { recording_ = false; setStage(Stage::Error, "BLE send failed"); continue; }

        AdpcmState st;
        uint16_t seq = 0;
        uint32_t t0 = nowMs();
        uint32_t sent = 0, dropped = 0;
        size_t payloadMax = (size_t)bleMaxPayload();                       // frame incl. 4-byte header
        size_t samples = payloadMax > 12 ? (payloadMax - 8) * 2 : 16;      // 4 frame hdr + 4 block hdr
        samples = std::min(samples, MAX_SAMPLES_PER_BLOCK) & ~(size_t)1;
        ESP_LOGI(TAG, "capture #%d start: %u samples/frame, mtu payload %u", id, (unsigned)samples, (unsigned)payloadMax);

        while (recording_ && bleConnected()) {
            int want = (int)(samples * sizeof(int16_t));
            int rc = device_mic::read(pcm, want) ? ESP_CODEC_DEV_OK : -1;
            if (rc != ESP_CODEC_DEV_OK) { ESP_LOGW(TAG, "codec read rc=%d", rc); vTaskDelay(pdMS_TO_TICKS(10)); continue; }

            double acc = 0;
            for (size_t i = 0; i < samples; i++) acc += (double)pcm[i] * pcm[i];
            float rms = (float)std::sqrt(acc / samples) / 32768.0f;

            frame[0] = 0xA5; frame[1] = (uint8_t)id; frame[2] = (uint8_t)seq; frame[3] = (uint8_t)(seq >> 8);
            size_t n = adpcmEncodeBlock(pcm, samples, st, frame + 4);
            bool ok = false;
            for (int retry = 0; retry < 8 && !ok; retry++) {
                ok = bleNotify(frame, 4 + n);
                if (!ok) vTaskDelay(pdMS_TO_TICKS(3));
            }
            if (ok) sent++; else dropped++;
            seq++;

            uint32_t el = nowMs() - t0;
            {
                std::lock_guard<std::mutex> g(m_);
                s_.level = std::min(1.0f, rms * 6.0f);
                s_.recMs = el;
                s_.framesSent = sent; s_.framesDropped = dropped;
                if ((seq & 3) == 0) bump();
            }
            if (el >= maxMs_) break;
        }
        recording_ = false;
        micDone();          // let the codec go so the other app can record

        char end[48];
        snprintf(end, sizeof(end), "{\"t\":\"end\",\"id\":%d}", id);
        bool ok = sendLine(end);
        ESP_LOGI(TAG, "capture #%d end: %lu ms, %lu frames sent, %lu dropped, end %s",
                 id, (unsigned long)(nowMs() - t0), (unsigned long)sent, (unsigned long)dropped, ok ? "sent" : "FAILED");
        {
            std::lock_guard<std::mutex> g(m_);
            s_.level = 0;
            if (s_.stage == Stage::Recording) s_.stage = ok ? Stage::Sending : Stage::Error;
            if (!ok) snprintf(s_.error, sizeof(s_.error), "BLE send failed");
            bump();
        }
    }
}

// ---------------------------------------------------------------- answer audio (NimBLE host task)
bool Core::onFrame(const uint8_t *data, size_t len)
{
    if (len < 4 || data[0] != 0xA6) return false;
    int id = data[1];
    int seq = data[2] | (data[3] << 8);

    std::lock_guard<std::mutex> g(m_);
    if (spkId_ != id) return false;                            // stale utterance

    int b = fillIdx_;
    if (!spkBuf_[b]) {
        spkBuf_[b] = (uint8_t *)heap_caps_malloc(SPK_MAX_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        if (!spkBuf_[b]) { ESP_LOGE(TAG, "no PSRAM for answer audio"); return false; }
    }
    if (spkLastSeq_ >= 0 && seq != ((spkLastSeq_ + 1) & 0xFFFF))
        ESP_LOGW(TAG, "answer audio gap at seq %d (expected %d)", seq, (spkLastSeq_ + 1) & 0xFFFF);
    spkLastSeq_ = seq;

    size_t w = adpcmDecodeBlock(data + 4, len - 4, spkBuf_[b] + spkLen_[b], SPK_MAX_BYTES - spkLen_[b]);
    spkLen_[b] += w;
    s_.speakGot++;
    if ((s_.speakGot & 7) == 0) bump();
    return true;
}

void Core::playTask()
{
    for (;;) {
        if (!playReady_) { vTaskDelay(pdMS_TO_TICKS(30)); continue; }
        playReady_ = false;

        int idx;
        size_t len;
        {
            std::lock_guard<std::mutex> g(m_);
            idx = pendingIdx_;
            len = (idx >= 0) ? spkLen_[idx] : 0;
        }
        playAbort_ = false;                         // the abort belonged to the answer we just replaced
        if (idx < 0 || len == 0) continue;
        playIdx_ = idx;

        if (!spk_) {
            esp_codec_dev_handle_t h = bsp_audio_codec_speaker_init();
            if (!h) {
                ESP_LOGW(TAG, "speaker init failed - answer stays text only");
                playIdx_ = -1;
                std::lock_guard<std::mutex> g(m_);
                spkLen_[idx] = 0;
                s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
                bump();
                continue;
            }
            esp_codec_dev_set_out_vol(h, (float)volume());
            esp_codec_dev_sample_info_t fs = {};
            fs.sample_rate = SAMPLE_RATE;
            fs.channel = 1;
            fs.bits_per_sample = 16;
            int rc = esp_codec_dev_open(h, &fs);
            if (rc != ESP_CODEC_DEV_OK) { ESP_LOGW(TAG, "speaker open rc=%d", rc); playIdx_ = -1; continue; }
            spk_ = h;
            ESP_LOGI(TAG, "speaker ready: %d Hz mono 16-bit", SAMPLE_RATE);
        }

        ESP_LOGI(TAG, "playing %u ms of answer audio (buffer %d)",
                 (unsigned)(len / (SAMPLE_RATE * 2 / 1000)), idx);
        const size_t CHUNK = 2048;
        for (size_t off = 0; off < len && !playAbort_; off += CHUNK) {
            size_t n = len - off < CHUNK ? len - off : CHUNK;
            // esp_codec_dev_write blocks until the I2S DMA has room, which paces playback for us.
            if (esp_codec_dev_write(spk_, spkBuf_[idx] + off, (int)n) != ESP_CODEC_DEV_OK) break;
        }
        playIdx_ = -1;
        {
            std::lock_guard<std::mutex> g(m_);
            spkLen_[idx] = 0;
            if (s_.stage == Stage::Speaking) s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
            bump();
        }
    }
}

} // namespace voice_chat
