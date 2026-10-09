#include "askbot_core.hpp"

#include <cstdio>
#include <cstring>
#include <string>

#include "bsp/esp-bsp.h"
#include "cJSON.h"
#include "esp_codec_dev.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"

#include "akka/remote_client.h"
#include "hud_transport.hpp"
#include "askbot/ima_adpcm.h"
#include "askbot_ble_stream.hpp"
#include "device_voice.hpp"
#include "device_mic.hpp"

static const char *TAG = "askbot";

namespace askbot {
namespace {

constexpr int TX_QUEUE_LEN = 12;
constexpr size_t TX_MAX = 640;   // one JSON message; the frame budget is far larger

// The outbound queue carries both kinds of message: JSON lines and microphone frames. They
// have to share one queue to keep their order - a frame that overtakes its "voice" header
// would be dropped by the host as belonging to no capture.
struct TxItem {
    uint8_t *data;
    uint16_t len;
    bool binary;
};

constexpr const char *CHAT_ACTOR = "/user/chat";   // on the host
constexpr const char *LOCAL_ACTOR = "chat";        // ours: /user/chat on this node

constexpr int    SAMPLE_RATE = 16000;              // what the host resamples SuperTonic down to
constexpr size_t SPK_MAX_BYTES = 20 * SAMPLE_RATE * 2;   // 20 s of PCM16 per buffer

uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

void taskTrampoline(void *arg) { ((Core *)arg)->linkTask(); }
void playTrampoline(void *arg) { ((Core *)arg)->playTask(); }
void captureTrampoline(void *arg) { ((Core *)arg)->captureTask(); }

constexpr size_t MIC_SAMPLES_PER_BLOCK = 960;   // 60 ms at 16 kHz -> 484-byte payload

}  // namespace

Core &Core::instance()
{
    static Core core;
    return core;
}

void Core::start()
{
    if (task_) return;
    queue_ = xQueueCreate(TX_QUEUE_LEN, sizeof(TxItem));
    if (!queue_) {
        ESP_LOGE(TAG, "tx queue alloc failed");
        return;
    }
    TaskHandle_t handle = nullptr;
    // 8 KB: std::string/std::vector in the protocol layer plus lwIP. 4 KB overflows.
    if (xTaskCreate(taskTrampoline, "askbot", 8192, this, 5, &handle) != pdPASS) {
        ESP_LOGE(TAG, "link task create failed");
        return;
    }
    task_ = handle;

    // Playback lives on its own task: esp_codec_dev_write blocks until the I2S DMA
    // has room, which is exactly what must not happen on the socket task.
    TaskHandle_t play = nullptr;
    if (xTaskCreatePinnedToCore(playTrampoline, "askbot_spk", 4096, this, 5, &play, 1) == pdPASS) {
        playTask_ = play;
    } else {
        ESP_LOGW(TAG, "play task create failed - answers stay text only");
    }

    // Capture runs on its own task for the same reason as playback: the codec read blocks
    // until the I2S DMA has samples.
    TaskHandle_t capture = nullptr;
    if (xTaskCreatePinnedToCore(captureTrampoline, "askbot_mic", 4096, this, 5, &capture, 1) == pdPASS) {
        captureTask_ = capture;
    } else {
        ESP_LOGW(TAG, "capture task create failed - the microphone will not work");
    }
}

// ---------------------------------------------------------------- microphone
bool Core::startVoice(uint32_t maxMs)
{
    {
        std::lock_guard<std::mutex> lock(m_);
        if (s_.link != Link::Up || !s_.hostOnline) return false;
        if (recording_) return false;
    }
    maxMs_ = maxMs;
    recording_ = true;      // the capture task sends the "voice" header itself
    return true;
}

void Core::stopVoice()
{
    recording_ = false;     // the capture task sends "end"
}

void Core::captureTask()
{
    // Internal (DMA-capable) memory for the codec read and the outgoing frame; PSRAM would
    // work for the frame but not for the read.
    int16_t *pcm = (int16_t *)heap_caps_malloc(MIC_SAMPLES_PER_BLOCK * sizeof(int16_t),
                                              MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    uint8_t *frame = (uint8_t *)heap_caps_malloc(8 + MIC_SAMPLES_PER_BLOCK / 2,
                                                MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!pcm || !frame) {
        ESP_LOGE(TAG, "no internal memory for capture buffers");
        vTaskDelete(nullptr);
        return;
    }

    for (;;) {
        if (!recording_) {
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }

        if (!device_mic::acquire()) {
            recording_ = false;
            setStage(Stage::Error, "microphone busy");
            continue;
        }

        int id;
        {
            std::lock_guard<std::mutex> lock(m_);
            id = nextId_++;
            s_.reqId = id;
            s_.micOk = device_mic::ok();
            s_.transcript[0] = 0;
            s_.question[0] = 0;
            s_.reply[0] = 0;
            s_.replyDone = false;
            s_.error[0] = 0;
            s_.framesSent = 0;
            s_.recMs = 0;
            s_.level = 0;
            s_.stage = Stage::Recording;
            askStartMs_ = nowMs();
            bump();
        }

        // Header first, then frames with the same id, then "end" - the host keys its capture
        // buffer on that id and drops frames from an abandoned utterance.
        const device_voice::Prefs vp = device_voice::get();
        char hdr[192];
        snprintf(hdr, sizeof(hdr),
                 "{\"t\":\"voice\",\"id\":%d,\"fmt\":\"adpcm\",\"rate\":%d,\"ch\":1,\"tts\":%s,"
                 "\"out\":\"%s\",\"lang\":\"%s\",\"outLang\":\"%s\",\"voice\":\"%s\"}",
                 id, device_mic::SAMPLE_RATE, mode() == AnswerMode::TextAndVoice ? "true" : "false",
                 answerModeWire(mode()), vp.inLang, vp.outLang, vp.voice);
        queueJson(hdr);

        askbot::AdpcmState adpcm;
        uint16_t seq = 0;
        const uint32_t t0 = nowMs();
        uint32_t sent = 0;

        while (recording_) {
            if (!device_mic::read(pcm, MIC_SAMPLES_PER_BLOCK * sizeof(int16_t))) {
                ESP_LOGW(TAG, "codec read failed");
                break;
            }

            int32_t peak = 0;
            for (size_t i = 0; i < MIC_SAMPLES_PER_BLOCK; ++i) {
                const int32_t v = pcm[i] < 0 ? -pcm[i] : pcm[i];
                if (v > peak) peak = v;
            }

            const size_t blockLen = askbot::EncodeAdpcmBlock(pcm, MIC_SAMPLES_PER_BLOCK, &adpcm, frame + 4);
            const size_t frameLen = askbot::BuildMicFrame((uint8_t)id, seq++, frame + 4, blockLen, frame,
                                                          8 + MIC_SAMPLES_PER_BLOCK / 2);
            // Audio goes as a .NET byte[] straight to the host's chat actor, the same shape the
            // answer audio uses in the other direction.
            if (frameLen && queueFrame(frame, frameLen)) sent++;

            const uint32_t elapsed = nowMs() - t0;
            {
                std::lock_guard<std::mutex> lock(m_);
                s_.level = (float)peak / 32768.0f;
                s_.recMs = elapsed;
                s_.framesSent = sent;
                if ((seq & 3) == 0) bump();
            }
            if (elapsed >= maxMs_) break;
        }

        recording_ = false;
        device_mic::release();          // hand the codec back so the Chat app can record

        char end[48];
        snprintf(end, sizeof(end), "{\"t\":\"end\",\"id\":%d}", id);
        queueJson(end);
        ESP_LOGI(TAG, "capture #%d: %lu ms, %lu frames", id, (unsigned long)(nowMs() - t0),
                 (unsigned long)sent);

        {
            std::lock_guard<std::mutex> lock(m_);
            s_.level = 0;
            if (s_.stage == Stage::Recording) s_.stage = Stage::Sending;
            bump();
        }
    }
}

// ---------------------------------------------------------------- user actions
bool Core::queueJson(const char *json)
{
    if (!json) return false;
    // strlen, not strnlen with a 640 bound: every caller passes a real C string from a
    // smaller local buffer, and gcc rejects a bound larger than the source it can see.
    const size_t n = strlen(json);
    if (n + 1 > TX_MAX) return false;
    return queueItem((const uint8_t *)json, n + 1, false);   // include the terminator
}

bool Core::queueFrame(const uint8_t *data, size_t len)
{
    return queueItem(data, len, true);
}

bool Core::queueItem(const uint8_t *data, size_t len, bool binary)
{
    if (!queue_ || !data || len == 0) return false;
    uint8_t *copy = (uint8_t *)malloc(len);
    if (!copy) return false;
    memcpy(copy, data, len);

    TxItem item{copy, (uint16_t)len, binary};
    if (xQueueSend((QueueHandle_t)queue_, &item, 0) != pdPASS) {
        free(copy);
        std::lock_guard<std::mutex> lock(m_);
        s_.dropped++;
        return false;
    }
    return true;
}

bool Core::sendText(const char *text)
{
    if (!text || !*text) return false;
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        if (s_.link != Link::Up) return false;
        id = nextId_++;
        s_.reqId = id;
        snprintf(s_.question, sizeof(s_.question), "%s", text);
        s_.reply[0] = 0;
        s_.replyDone = false;
        s_.error[0] = 0;
        s_.chunks = 0;
        s_.stage = Stage::Sending;
        askStartMs_ = nowMs();
        bump();
    }

    // Output language and voice come from the device-wide Settings screen, the same ones
    // the Chat app uses - one setting, both apps.
    const device_voice::Prefs vp = device_voice::get();
    cJSON *js = cJSON_CreateObject();
    cJSON_AddStringToObject(js, "t", "text");
    cJSON_AddNumberToObject(js, "id", id);
    cJSON_AddStringToObject(js, "text", text);
    // "tts" stays for hosts that predate "out": they treat PC mode as text only.
    cJSON_AddBoolToObject(js, "tts", mode() == AnswerMode::TextAndVoice);
    cJSON_AddStringToObject(js, "out", answerModeWire(mode()));
    cJSON_AddStringToObject(js, "outLang", vp.outLang);
    cJSON_AddStringToObject(js, "voice", vp.voice);
    char *out = cJSON_PrintUnformatted(js);
    const bool ok = out && queueJson(out);
    cJSON_free(out);
    cJSON_Delete(js);
    if (!ok) setStage(Stage::Error, "send failed");
    return ok;
}

AnswerMode Core::mode()
{
    std::lock_guard<std::mutex> lock(m_);
    return s_.mode;
}

void Core::setMode(AnswerMode m)
{
    std::lock_guard<std::mutex> lock(m_);
    if (s_.mode == m) return;
    s_.mode = m;
    bump();
}

void Core::cancel()
{
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        id = s_.reqId;
    }
    playAbort_ = true;   // stop whatever the speaker is in the middle of
    char js[64];
    snprintf(js, sizeof(js), "{\"t\":\"cancel\",\"id\":%d}", id);
    queueJson(js);
}

void Core::newChat()
{
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        id = s_.reqId;
        s_.question[0] = 0;
        s_.reply[0] = 0;
        s_.replyDone = false;
        s_.error[0] = 0;
        s_.stage = Stage::Idle;
        bump();
    }
    char js[64];
    snprintf(js, sizeof(js), "{\"t\":\"newsession\",\"id\":%d}", id);
    queueJson(js);
}

void Core::clear()
{
    std::lock_guard<std::mutex> lock(m_);
    s_.question[0] = 0;
    s_.reply[0] = 0;
    s_.replyDone = false;
    s_.error[0] = 0;
    s_.stage = Stage::Idle;
    bump();
}

void Core::snapshot(Snapshot &out)
{
    std::lock_guard<std::mutex> lock(m_);
    out = s_;
}

uint32_t Core::version()
{
    std::lock_guard<std::mutex> lock(m_);
    return ver_;
}

void Core::setStage(Stage stage, const char *error)
{
    std::lock_guard<std::mutex> lock(m_);
    s_.stage = stage;
    if (error) snprintf(s_.error, sizeof(s_.error), "%s", error);
    else if (stage != Stage::Error) s_.error[0] = 0;
    bump();
}

// ---------------------------------------------------------------- inbound
void Core::onMessage(const char *json)
{
    cJSON *js = cJSON_Parse(json);
    if (!js) {
        ESP_LOGW(TAG, "bad json from host: %.60s", json);
        return;
    }

    const cJSON *t = cJSON_GetObjectItem(js, "t");
    const char *type = cJSON_IsString(t) ? t->valuestring : "";

    if (strcmp(type, "hostinfo") == 0) {
        std::lock_guard<std::mutex> lock(m_);
        const cJSON *h = cJSON_GetObjectItem(js, "host");
        const cJSON *p = cJSON_GetObjectItem(js, "provider");
        const cJSON *c = cJSON_GetObjectItem(js, "chat");
        snprintf(s_.host, sizeof(s_.host), "%s", cJSON_IsString(h) ? h->valuestring : "?");
        snprintf(s_.provider, sizeof(s_.provider), "%s", cJSON_IsString(p) ? p->valuestring : "?");
        if (cJSON_IsNumber(c)) s_.chatNo = c->valueint;
        s_.hostTts = cJSON_IsTrue(cJSON_GetObjectItem(js, "tts"));
        s_.hostPcOut = s_.hostTts && cJSON_IsTrue(cJSON_GetObjectItem(js, "pcOut"));
        if (!s_.hostTts) s_.mode = AnswerMode::TextOnly;   // nothing to speak with
        if (!s_.hostPcOut && s_.mode == AnswerMode::TextAndPc) s_.mode = AnswerMode::TextAndVoice;
        s_.hostOnline = true;
        bump();
        cJSON_Delete(js);
        return;
    }

    if (strcmp(type, "cmd") == 0) {
        // Host-driven control, the same idea as the Chat app's "C" commands: lets a PC start an
        // utterance or ask a question without anyone touching the watch.
        const cJSON *c = cJSON_GetObjectItem(js, "cmd");
        const char *cmd = cJSON_IsString(c) ? c->valuestring : "";
        if (strcmp(cmd, "talk") == 0) {
            const cJSON *ms = cJSON_GetObjectItem(js, "ms");
            const uint32_t d = cJSON_IsNumber(ms) ? (uint32_t)ms->valueint : 3000;
            ESP_LOGI(TAG, "host asked for a %lu ms capture", (unsigned long)d);
            startVoice(d);
        } else if (strcmp(cmd, "text") == 0) {
            const cJSON *tx = cJSON_GetObjectItem(js, "text");
            if (cJSON_IsString(tx)) sendText(tx->valuestring);
        } else if (strcmp(cmd, "cancel") == 0) {
            stopVoice();
            cancel();
        } else {
            ESP_LOGW(TAG, "unknown cmd '%s'", cmd);
        }
        cJSON_Delete(js);
        return;
    }

    if (strcmp(type, "answer") != 0) {
        ESP_LOGW(TAG, "unknown message type '%s'", type);
        cJSON_Delete(js);
        return;
    }

    const cJSON *st = cJSON_GetObjectItem(js, "st");
    const cJSON *tx = cJSON_GetObjectItem(js, "text");
    const char *stage = cJSON_IsString(st) ? st->valuestring : "";
    const char *text = cJSON_IsString(tx) ? tx->valuestring : nullptr;

    std::lock_guard<std::mutex> lock(m_);
    if (strcmp(stage, "rec") == 0) {
        s_.stage = Stage::Recording;
    } else if (strcmp(stage, "stt") == 0) {
        // Without text: transcribing. With text: what the host heard, which is worth showing
        // even when the answer is still coming.
        if (text && *text) {
            snprintf(s_.transcript, sizeof(s_.transcript), "%s", text);
            snprintf(s_.question, sizeof(s_.question), "%s", text);
        }
        s_.stage = Stage::Stt;
    } else if (strcmp(stage, "think") == 0) {
        s_.stage = Stage::Think;
    } else if (strcmp(stage, "reply") == 0) {
        const cJSON *seq = cJSON_GetObjectItem(js, "seq");
        // seq 0 starts a fresh answer; a resent or reordered first chunk must not
        // append to the previous one.
        if (cJSON_IsNumber(seq) && seq->valueint == 0) {
            s_.reply[0] = 0;
            s_.chunks = 0;
        }
        if (text) {
            const size_t have = strlen(s_.reply);
            snprintf(s_.reply + have, sizeof(s_.reply) - have, "%s", text);
        }
        s_.chunks++;
        s_.replyDone = cJSON_IsTrue(cJSON_GetObjectItem(js, "done"));
        s_.stage = Stage::Reply;
        if (s_.replyDone) s_.askMs = nowMs() - askStartMs_;
    } else if (strcmp(stage, "speak") == 0) {
        // The host announces the utterance, then pushes ADPCM frames. Decode into the
        // fill buffer as they arrive; play once "speak_end" says it is complete.
        const cJSON *id = cJSON_GetObjectItem(js, "id");
        const cJSON *ms = cJSON_GetObjectItem(js, "ms");
        const cJSON *fr = cJSON_GetObjectItem(js, "frames");
        spkId_ = cJSON_IsNumber(id) ? id->valueint : s_.reqId;
        spkLastSeq_ = -1;
        // Never fill the buffer the speaker is reading from.
        fillIdx_ = (playIdx_ == 0) ? 1 : 0;
        spkLen_[fillIdx_] = 0;
        s_.speakMs = cJSON_IsNumber(ms) ? (uint32_t)ms->valueint : 0;
        s_.speakWant = cJSON_IsNumber(fr) ? (uint32_t)fr->valueint : 0;
        s_.speakGot = 0;
        s_.stage = Stage::Speaking;
    } else if (strcmp(stage, "speak_end") == 0) {
        if (text && *text) {
            snprintf(s_.error, sizeof(s_.error), "%s", text);
            s_.stage = s_.reply[0] ? Stage::Reply : Stage::Error;
        } else if (spkLen_[fillIdx_] > 0) {
            pendingIdx_ = fillIdx_;
            playReady_ = true;          // hand it to the play task
        } else {
            s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
        }
    } else if (strcmp(stage, "session") == 0) {
        const cJSON *n = cJSON_GetObjectItem(js, "n");
        if (cJSON_IsNumber(n)) s_.chatNo = n->valueint;
        s_.stage = Stage::Idle;
    } else if (strcmp(stage, "idle") == 0) {
        s_.stage = Stage::Idle;
    } else if (strcmp(stage, "err") == 0) {
        snprintf(s_.error, sizeof(s_.error), "%s", text ? text : "host error");
        s_.stage = Stage::Error;
    }
    bump();
    cJSON_Delete(js);
}

// ---------------------------------------------------------------- answer audio
void Core::onSpeechFrame(const uint8_t *data, size_t len)
{
    askbot::SpeechFrame frame;
    if (!askbot::ParseSpeechFrame(data, len, &frame)) return;

    std::lock_guard<std::mutex> lock(m_);
    if (spkId_ >= 0 && frame.id != (uint8_t)spkId_) return;   // frames of an abandoned answer

    const int b = fillIdx_;
    if (!spkBuf_[b]) {
        spkBuf_[b] = (uint8_t *)heap_caps_malloc(SPK_MAX_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        if (!spkBuf_[b]) {
            ESP_LOGE(TAG, "no PSRAM for answer audio");
            return;
        }
    }
    if (spkLastSeq_ >= 0 && frame.seq != (uint16_t)((spkLastSeq_ + 1) & 0xFFFF)) {
        ESP_LOGW(TAG, "speech gap at seq %u (expected %d)", frame.seq, (spkLastSeq_ + 1) & 0xFFFF);
    }
    spkLastSeq_ = frame.seq;

    spkLen_[b] += askbot::DecodeAdpcmBlockTo(frame.block, frame.block_len, spkBuf_[b] + spkLen_[b],
                                            SPK_MAX_BYTES - spkLen_[b]);
    s_.speakGot++;
    if ((s_.speakGot & 7) == 0) bump();
}

void Core::playTask()
{
    for (;;) {
        if (!playReady_) {
            vTaskDelay(pdMS_TO_TICKS(30));
            continue;
        }
        playReady_ = false;

        int idx;
        size_t len;
        {
            std::lock_guard<std::mutex> lock(m_);
            idx = pendingIdx_;
            len = idx >= 0 ? spkLen_[idx] : 0;
        }
        playAbort_ = false;      // the abort belonged to the answer this one replaces
        if (idx < 0 || len == 0) continue;
        playIdx_ = idx;

        if (!spk_) {
            esp_codec_dev_handle_t h = bsp_audio_codec_speaker_init();
            if (!h) {
                ESP_LOGW(TAG, "speaker init failed - answer stays text only");
                playIdx_ = -1;
                std::lock_guard<std::mutex> lock(m_);
                spkLen_[idx] = 0;
                s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
                bump();
                continue;
            }
            esp_codec_dev_set_out_vol(h, (float)CONFIG_ASKBOT_VOLUME);
            esp_codec_dev_sample_info_t fs = {};
            fs.sample_rate = SAMPLE_RATE;
            fs.channel = 1;
            fs.bits_per_sample = 16;
            const int rc = esp_codec_dev_open(h, &fs);
            if (rc != ESP_CODEC_DEV_OK) {
                ESP_LOGW(TAG, "speaker open rc=%d", rc);
                playIdx_ = -1;
                continue;
            }
            spk_ = h;
            ESP_LOGI(TAG, "speaker ready: %d Hz mono 16-bit", SAMPLE_RATE);
        }

        ESP_LOGI(TAG, "playing %u ms of answer audio (buffer %d)",
                 (unsigned)(len / (SAMPLE_RATE * 2 / 1000)), idx);
        const size_t CHUNK = 2048;
        for (size_t off = 0; off < len && !playAbort_; off += CHUNK) {
            const size_t n = len - off < CHUNK ? len - off : CHUNK;
            // Blocks until the I2S DMA has room, which paces playback for us.
            if (esp_codec_dev_write((esp_codec_dev_handle_t)spk_, spkBuf_[idx] + off, (int)n) !=
                ESP_CODEC_DEV_OK) {
                break;
            }
        }
        playIdx_ = -1;
        {
            std::lock_guard<std::mutex> lock(m_);
            spkLen_[idx] = 0;
            if (s_.stage == Stage::Speaking) s_.stage = s_.reply[0] ? Stage::Reply : Stage::Idle;
            bump();
        }
    }
}

// ---------------------------------------------------------------- link task
void Core::linkTask()
{
    {
        std::lock_guard<std::mutex> lock(m_);
        s_.link = Link::WifiConnecting;
        bump();
    }

    // No WiFi: the PDUs ride the BLE link this firmware already keeps up, and a
    // bridge on the PC relays them to the Akka node. WiFi cost more than it was worth
    // here - it starved the internal DMA heap and the LCD tore while drawing.
    ESP_LOGI(TAG, "BLE tunnel mode (free internal DMA heap %u B, largest block %u B)",
             (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA),
             (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA));

    akka::ClientConfig config;
    config.remote.system = CONFIG_ASKBOT_HOST_SYSTEM;
    config.remote.host = CONFIG_ASKBOT_HOST_IP;
    config.remote.port = CONFIG_ASKBOT_HOST_PORT;
    config.local.system = CONFIG_ASKBOT_LOCAL_SYSTEM;
    // The handshake has to advertise a host:port (Akka refuses to serialise an address
    // without them) but nothing ever dials it: use_passive_connections keeps the peer
    // talking back down the connection we opened, which here is the BLE tunnel.
    config.local.host = CONFIG_ASKBOT_LOCAL_HOST;
    config.local.port = CONFIG_ASKBOT_LOCAL_PORT;
    // The bridge is on the far side of the link, so a handshake reply crosses BLE twice.
    config.connect_timeout_ms = 30000;
    config.handshake_timeout_ms = 15000;

    {
        std::lock_guard<std::mutex> lock(m_);
        snprintf(s_.ip, sizeof(s_.ip), "%s", "BLE");
        bump();
    }

    while (true) {
        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Associating;
            s_.hostOnline = false;
            bump();
        }

        akka::RemoteClient client(config, askbot::MakeBleStream());
        client.set_logger([](const char *level, const std::string &message) {
            if (strcmp(level, "error") == 0)      ESP_LOGE(TAG, "%s", message.c_str());
            else if (strcmp(level, "warn") == 0)  ESP_LOGW(TAG, "%s", message.c_str());
            else                                  ESP_LOGI(TAG, "%s", message.c_str());
        });
        // The client actor. Runs on this task, so it may touch the snapshot directly.
        client.Register(LOCAL_ACTOR, [this](const akka::Message &message) {
            // Speech arrives as .NET byte[] (serializer 4); everything else is JSON text.
            if (message.serializer_id == akka::kSerializerByteArray && message.bytes != nullptr) {
                onSpeechFrame(message.bytes->data(), message.bytes->size());
                return;
            }
            if (!message.text.empty()) onMessage(message.text.c_str());
        });

        if (!client.Connect()) {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Down;
            snprintf(s_.error, sizeof(s_.error), "%s",
                     claude_hud::bleConnected() ? "no bridge on the BLE link" : "waiting for BLE");
            bump();
            vTaskDelay(pdMS_TO_TICKS(5000));
            continue;
        }

        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Up;
            snprintf(s_.peer, sizeof(s_.peer), "%s", client.peer().ToString().c_str());
            s_.error[0] = 0;
            bump();
        }

        client.TellAs(LOCAL_ACTOR, CHAT_ACTOR,
                      "{\"t\":\"hello\",\"name\":\"askbot\",\"fw\":\"akka-1\"}");

        while (client.associated()) {
            if (!client.Poll(100)) break;

            TxItem item{};
            while (xQueueReceive((QueueHandle_t)queue_, &item, 0) == pdPASS) {
                const bool ok = item.binary
                    ? client.TellBytesAs(LOCAL_ACTOR, CHAT_ACTOR, item.data, item.len)
                    : client.TellAs(LOCAL_ACTOR, CHAT_ACTOR, (const char *)item.data);
                free(item.data);
                std::lock_guard<std::mutex> lock(m_);
                if (ok) s_.sent++;
                else    s_.dropped++;
            }
        }

        ESP_LOGW(TAG, "association lost, reconnecting");
        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Down;
            s_.hostOnline = false;
            bump();
        }
        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}

} // namespace askbot
