#include "askbot_ble_stream.hpp"

#include <cstring>

#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/ringbuf.h"
#include "freertos/task.h"

#include "hud_transport.hpp"

static const char *TAG = "askbot_ble";

namespace askbot {
namespace {

// 16 KB of slack for inbound bytes. The host can push a whole utterance (hundreds of
// 484-byte ADPCM frames) far faster than the link task drains it, and dropping bytes
// mid-PDU would desync the Akka framing - which is unrecoverable, unlike a dropped
// audio frame.
constexpr size_t RX_RING_BYTES = 16 * 1024;

RingbufHandle_t s_rx = nullptr;

// Runs on the NimBLE host task: claim 0xAB and copy the payload out fast.
bool frameHook(const uint8_t *data, size_t len)
{
    if (len < 2 || data[0] != kTunnelMagic) return false;   // not ours, let Chat look
    if (!s_rx) return true;

    if (xRingbufferSend(s_rx, data + 1, len - 1, 0) != pdTRUE) {
        // A full ring means the Akka stream is now missing bytes; the association
        // cannot survive that, so say so loudly rather than corrupting it silently.
        ESP_LOGE(TAG, "rx ring full, %u bytes dropped - association will reset", (unsigned)(len - 1));
    }
    return true;
}

class BleStream final : public akka::IByteStream {
public:
    ~BleStream() override { Close(); }

    bool Connect(const std::string &, uint16_t, int timeout_ms) override
    {
        // Nothing to dial: wait for the PC's bridge to be connected and subscribed.
        if (!s_rx) {
            s_rx = xRingbufferCreate(RX_RING_BYTES, RINGBUF_TYPE_BYTEBUF);
            if (!s_rx) {
                ESP_LOGE(TAG, "rx ring alloc failed");
                return false;
            }
        }
        claude_hud::startBle();             // idempotent; whichever app got there first wins
        claude_hud::setFrameHook(frameHook);

        // Wait for *subscribed*, not merely connected - which is what the comment above
        // always said and what this loop did not do. A central is connected the moment the
        // link is up, but it enables TX notifications only after service discovery and the
        // CCCD write; on Windows that lands 1.3 s later. Opening the tunnel in between means
        // the first ASSOCIATE is notified into a void, WriteAll gives up after its 200 ms of
        // back-pressure retries, and the association is dropped and retried 5 s later. That
        // cost a failed ASSOCIATE and ~5 s on every single connect.
        const int64_t deadline = esp_timer_get_time() / 1000 + timeout_ms;
        while (esp_timer_get_time() / 1000 < deadline) {
            if (claude_hud::bleSubscribed()) {
                Drain();                    // start from a clean stream
                open_ = true;
                ESP_LOGI(TAG, "tunnel open, %d bytes per notification",
                         claude_hud::bleMaxPayload() - 1);
                return true;
            }
            vTaskDelay(pdMS_TO_TICKS(50));  // 200 ms granularity added a quarter second for nothing
        }
        ESP_LOGW(TAG, "central did not subscribe to TX within %d ms", timeout_ms);
        return false;
    }

    void Close() override { open_ = false; }

    // Same bar as Connect: a central that unsubscribes has stopped being reachable, even
    // though the link is still up.
    bool IsOpen() const override { return open_ && claude_hud::bleSubscribed(); }

    int Read(uint8_t *buf, size_t len, int timeout_ms) override
    {
        if (!IsOpen()) return -1;
        if (!s_rx) return -1;

        size_t got = 0;
        void *item = xRingbufferReceiveUpTo(s_rx, &got, pdMS_TO_TICKS(timeout_ms), len);
        if (item == nullptr) return 0;       // nothing within the timeout, not an error
        memcpy(buf, item, got);
        vRingbufferReturnItem(s_rx, item);
        return (int)got;
    }

    bool WriteAll(const uint8_t *buf, size_t len) override
    {
        if (!IsOpen()) return false;

        // One notification per chunk, minus the tag byte.
        const int payload = claude_hud::bleMaxPayload() - 1;
        if (payload <= 0) return false;

        uint8_t frame[520];
        const size_t max = sizeof(frame) - 1 < (size_t)payload ? sizeof(frame) - 1 : (size_t)payload;

        size_t sent = 0;
        while (sent < len) {
            const size_t n = len - sent < max ? len - sent : max;
            frame[0] = kTunnelMagic;
            memcpy(frame + 1, buf + sent, n);

            // bleNotify fails while the controller has no free buffers; that is back
            // pressure, not an error, so retry briefly before giving up on the link.
            bool ok = false;
            for (int attempt = 0; attempt < 40 && !ok; ++attempt) {
                ok = claude_hud::bleNotify(frame, n + 1);
                if (!ok) vTaskDelay(pdMS_TO_TICKS(5));
                if (!claude_hud::bleConnected()) return false;
            }
            if (!ok) {
                ESP_LOGW(TAG, "notify blocked for 200 ms, dropping the tunnel");
                return false;
            }
            sent += n;
        }
        return true;
    }

private:
    void Drain()
    {
        if (!s_rx) return;
        size_t got = 0;
        while (void *item = xRingbufferReceiveUpTo(s_rx, &got, 0, RX_RING_BYTES)) {
            vRingbufferReturnItem(s_rx, item);
        }
    }

    bool open_ = false;
};

}  // namespace

std::unique_ptr<akka::IByteStream> MakeBleStream() { return std::make_unique<BleStream>(); }

} // namespace askbot
