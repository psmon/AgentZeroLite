// AskBot core: the same conversation flow as brookesia_app_chat, carried over
// Akka.NET remoting instead of BLE.
//
// The device registers a client actor at
// akka.tcp://<local system>@<ip>:<port>/user/chat and sends from it, so the host
// actor's Sender.Tell(...) lands right back here - stages, reply chunks, session
// changes - as ordinary actor messages. Protocol details:
// project/samples/akka/PROTOCOL.md.
//
// Unlike the BLE apps in this firmware, this one needs IP: Akka classic remoting
// is TCP. WiFi is therefore started lazily, the first time the app is opened, so a
// user who never touches AskBot never has the radio brought up.
#pragma once
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace askbot {

enum class Link : uint8_t { Down, WifiConnecting, WifiFailed, Associating, Up };
enum class Stage : uint8_t { Idle, Recording, Sending, Stt, Think, Reply, Speaking, Error };

/// What the host should do with an answer besides sending the text: nothing, speak it
/// on this watch, or speak it on the PC's own speakers (no audio crosses the link).
enum class AnswerMode : uint8_t { TextOnly = 0, TextAndVoice = 1, TextAndPc = 2 };

/// Wire name of an answer mode - the "out" field of every request.
inline const char *answerModeWire(AnswerMode m)
{
    return m == AnswerMode::TextAndVoice ? "watch" : m == AnswerMode::TextAndPc ? "pc" : "off";
}

struct Snapshot {
    Link link = Link::Down;
    char ip[16] = "";           // this device's address, as advertised to the host
    char peer[64] = "";         // akka.tcp://AskBot@host:port
    char host[40] = "";         // host machine name, from its hostinfo reply
    char provider[24] = "";     // chat CLI the host runs
    bool hostOnline = false;    // hostinfo received on this association
    int  chatNo = 1;            // conversation number the host has us on

    Stage stage = Stage::Idle;
    int   reqId = 0;
    bool  micOk = false;        // the codec opened at least once
    float level = 0;            // 0..1 input level while recording
    uint32_t recMs = 0;         // how long the current capture has run
    uint32_t framesSent = 0;    // microphone frames pushed to the host
    char  transcript[256] = ""; // what the host heard
    char  question[256] = "";   // what we last asked
    char  reply[1200] = "";     // answer, chunks concatenated
    bool  replyDone = false;
    char  error[96] = "";

    uint32_t askMs = 0;         // round trip of the last completed answer
    uint32_t chunks = 0;        // chunks received for the current answer
    uint32_t sent = 0, dropped = 0;

    // Spoken answer (SuperTonic on the host, ADPCM over Akka byte[] messages).
    AnswerMode mode = AnswerMode::TextOnly;
    bool     hostTts = false;   // host reported a usable voice in its hostinfo
    bool     hostPcOut = false; // ...and that it can speak on the PC (hostinfo "pcOut")
    uint32_t speakMs = 0;       // length the host announced
    uint32_t speakGot = 0, speakWant = 0;   // frames decoded / announced
};

class Core {
public:
    static Core &instance();

    // Idempotent. Brings up WiFi and the Akka association on a background task.
    void start();

    // --- user actions, safe from the LVGL task ---
    bool sendText(const char *text);
    bool startVoice(uint32_t maxMs = 30000);   // begin capture; false if the link or codec is not ready
    void stopVoice();                          // end capture -> host runs STT -> chat
    void cancel();
    void newChat();
    void clear();

    AnswerMode mode();
    void       setMode(AnswerMode m);

    void     snapshot(Snapshot &out);
    uint32_t version();

    // internal (public for the task trampolines)
    void linkTask();
    void playTask();
    void captureTask();

private:
    Core() = default;
    bool queueJson(const char *json);
    bool queueFrame(const uint8_t *data, size_t len);   // microphone audio, sent as byte[]
    bool queueItem(const uint8_t *data, size_t len, bool binary);
    void onMessage(const char *json);
    void onSpeechFrame(const uint8_t *data, size_t len);
    void setStage(Stage stage, const char *error = nullptr);
    void bump() { ver_++; }

    std::mutex m_;
    Snapshot   s_;
    uint32_t   ver_ = 1;
    int        nextId_ = 1;
    uint32_t   askStartMs_ = 0;

    void *task_ = nullptr;      // TaskHandle_t (link)
    void *queue_ = nullptr;     // QueueHandle_t of char*

    // Answer audio, decoded into PSRAM as frames arrive and played when the host
    // says the utterance is complete. Two buffers, because the next answer's frames
    // start arriving while the current one is still playing - the Chat app learned
    // that the hard way (one buffer truncated a 17 s answer to 3.6 s).
    // Microphone capture: the codec is a shared device service (device_mic), held only while
    // recording so the Chat app can record too.
    void    *captureTask_ = nullptr;   // TaskHandle_t
    volatile bool recording_ = false;
    uint32_t maxMs_ = 30000;

    void    *spk_ = nullptr;        // esp_codec_dev_handle_t, opened lazily
    void    *playTask_ = nullptr;   // TaskHandle_t
    uint8_t *spkBuf_[2] = {nullptr, nullptr};   // PCM16 @ 16 kHz
    size_t   spkLen_[2] = {0, 0};
    int      fillIdx_ = 0;          // buffer the frame handler writes into
    volatile int playIdx_ = -1;     // buffer the play task holds, -1 = none
    int      pendingIdx_ = -1;      // buffer waiting to be played
    int      spkId_ = -1;           // request id the current audio belongs to
    int      spkLastSeq_ = -1;
    volatile bool playReady_ = false;
    volatile bool playAbort_ = false;
};

} // namespace askbot
