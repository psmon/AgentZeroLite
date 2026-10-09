// Voice-chat core: protocol state (device<->host, see amoled_chat_host/PROTOCOL.md), microphone capture task
// and IMA ADPCM streaming over the shared NUS transport. UI-agnostic; the app polls snapshot()/version().
#pragma once
#include <cstdint>
#include <cstddef>
#include <mutex>

namespace voice_chat {

enum class Stage : uint8_t { Idle, Recording, Sending, Stt, Think, Reply, Speaking, Error, Busy };

// What the host should send back for an answer.
enum class AnswerMode : uint8_t { TextOnly = 0, TextAndVoice = 1 };

struct Snapshot {
    Stage    stage = Stage::Idle;
    int      reqId = 0;
    char     transcript[256] = "";     // last STT result from the host
    char     reply[1200] = "";         // answer (chunks concatenated)
    bool     replyDone = false;
    char     error[96] = "";
    char     host[40] = "";            // host machine name from the H line
    char     provider[24] = "";        // chat CLI the host uses
    bool     hostOnline = false;       // H line received on this connection
    bool     bleConnected = false;
    bool     micOk = false;
    int      volume = 100;             // speaker, 0..100 (a value saved in NVS wins)
    int      micGain = 30;             // ES7210 input gain in dB, 0..60
    bool     hostTts = false;          // host reported a usable TTS voice in its hello
    int      chatNo = 1;               // which conversation the host has us on (1-based)
    AnswerMode mode = AnswerMode::TextOnly;
    float    level = 0;                // 0..1 input level while recording
    uint32_t recMs = 0;                // recording length so far
    uint32_t framesSent = 0, framesDropped = 0;
    uint32_t speakMs = 0;              // length of the answer audio the host announced
    uint32_t speakGot = 0, speakWant = 0;   // frames received / announced
};

class Core {
public:
    static Core &instance();

    void init();                       // install-time: register the BLE line hook, start the capture task
    // --- user actions (any task)
    bool startVoice(uint32_t maxMs = 30000);   // begin capture; false if BLE/host not ready or busy
    void stopVoice();                  // end capture -> host runs STT + chat
    bool sendText(const char *text);   // typed/preset prompt
    void cancel();                     // stop recording / playback and tell the host to drop the request
    void newChat();                    // start a fresh conversation (the CLI keeps the old one, we just leave it)
    void clear();                      // clear transcript/reply

    AnswerMode mode();                 // persisted in NVS
    void setMode(AnswerMode m);

    // Audio levels. Persisted in NVS and applied to the codec immediately when it is already open,
    // otherwise at open time - the Settings app can move them before anything has been recorded.
    int  volume();                     // 0..100
    void setVolume(int v);
    int  micGain();                    // dB, 0..60
    void setMicGain(int db);
    void playTestTone();               // short beep so a volume change can be heard immediately

    void     snapshot(Snapshot &out);
    uint32_t version();                // bumps on every visible change

    // internal (public for the task/hook trampolines)
    bool onLine(const char *line, size_t len);
    bool onFrame(const uint8_t *data, size_t len);
    void captureTask();
    void txTask();
    void playTask();

private:
    Core() = default;
    bool micInit();
    void micDone();
    bool sendLine(const char *json);   // "R " + json, blocking - never call from the NimBLE host task
    bool queueLine(const char *json);  // hand "R " + json to the tx task (safe from any task)
    void setStage(Stage st, const char *err = nullptr);
    void bump() { ver_++; }

    std::mutex m_;
    Snapshot   s_;
    uint32_t   ver_ = 1;
    uint8_t    nextId_ = 1;
    volatile bool recording_ = false;  // set by startVoice, cleared by stopVoice / limits
    uint32_t   maxMs_ = 30000;
    void      *mic_ = nullptr;         // esp_codec_dev_handle_t
    void      *task_ = nullptr;        // TaskHandle_t (capture)
    void      *txTask_ = nullptr;      // TaskHandle_t (outbound lines)
    void      *txQueue_ = nullptr;     // QueueHandle_t of TxMsg

    // Answer audio: decoded into PSRAM as frames arrive, played once the host says it is done.
    // Two buffers, because the next answer's frames start arriving long before the current one has
    // finished playing (BLE delivers ~4x faster than the speaker consumes). One buffer meant the new
    // frames overwrote the audio being played, and the tail of a 17 s answer came out as 3.6 s.
    void      *spk_ = nullptr;         // esp_codec_dev_handle_t (opened lazily)
    void      *playTask_ = nullptr;
    uint8_t   *spkBuf_[2] = {nullptr, nullptr};   // PCM16 @16 kHz
    size_t     spkLen_[2] = {0, 0};
    int        fillIdx_ = 0;           // buffer the frame hook writes into
    volatile int playIdx_ = -1;        // buffer the play task holds, -1 = none
    int        pendingIdx_ = -1;       // buffer waiting to be played
    int        spkId_ = -1;
    int        spkLastSeq_ = -1;
    volatile bool playReady_ = false;  // buffer complete, playTask may start
    volatile bool playAbort_ = false;
};

} // namespace voice_chat
