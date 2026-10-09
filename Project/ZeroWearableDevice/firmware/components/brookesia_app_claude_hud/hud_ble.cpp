// The only transport: BLE Nordic UART Service (NUS) on NimBLE, advertising as "claude-hud".
// Same UUIDs as project/samples/claude_hud, so the same PC-side BLE sender protocol works:
//   service 6E400001-B5A3-F393-E0A9-E50E24DCCA9E, RX (write) ...0002, TX (notify) ...0003
// Any failure to start/advertise is logged as a warning and recorded in NetInfo::bleErr; there is
// no fallback transport by design (see README).
#include "hud_transport.hpp"
#include "hud_state.hpp"
#include <cstring>
#include <cstdio>
#include <string>
#include <algorithm>
#include "esp_log.h"
#include "nvs_flash.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/ble_att.h"
#include "host/util/util.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

#define BLE_NAME "claude-hud"

static const char *TAG = "hud_ble";

namespace claude_hud {

// 128-bit UUIDs are little-endian byte arrays in NimBLE.
static const ble_uuid128_t NUS_SVC_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x01, 0x00, 0x40, 0x6e);
static const ble_uuid128_t NUS_RX_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x02, 0x00, 0x40, 0x6e);
static const ble_uuid128_t NUS_TX_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x03, 0x00, 0x40, 0x6e);

static uint16_t    s_txHandle = 0;
static uint8_t     s_ownAddrType = 0;
static std::string s_rxBuf;
static uint16_t    s_connHandle = BLE_HS_CONN_HANDLE_NONE;
static uint16_t    s_mtu = 23;
static bool        s_txSubscribed = false;
static bool        s_started = false;
static LineHook    s_lineHook = nullptr;

// More than one app wants binary writes now: Chat takes 0xA6 speech frames, AskBot
// takes 0xAB tunnel chunks. Hooks are tried in order until one claims the frame.
static constexpr int MAX_FRAME_HOOKS = 4;
static FrameHook   s_frameHooks[MAX_FRAME_HOOKS] = {};

// First byte of a host->device binary write. Cannot collide with a text line: every tag is ASCII.
static constexpr uint8_t SPEECH_MAGIC = 0xA6;
static constexpr uint8_t TUNNEL_MAGIC = 0xAB;

static void setErr(const char *msg, int rc)
{
    auto &net = State::instance().net;
    snprintf(net.bleErr, sizeof(net.bleErr), "%s (rc=%d)", msg, rc);
    ESP_LOGW(TAG, "%s", net.bleErr);
}

// True when [s, s+n) is one complete JSON object: braces balanced and the last one not inside a
// string. Used to accept a line from a sender that omitted the trailing newline WITHOUT accepting a
// line that merely happens to be cut after a '}' - which is what a long line split across several
// BLE writes looks like.
static bool completeObject(const char *s, size_t n)
{
    int depth = 0;
    bool inStr = false, esc = false, seen = false;
    for (size_t i = 0; i < n; i++) {
        char c = s[i];
        if (inStr) {
            if (esc)            esc = false;
            else if (c == '\\') esc = true;
            else if (c == '"')  inStr = false;
            continue;
        }
        if (c == '"')      inStr = true;
        else if (c == '{') { depth++; seen = true; }
        else if (c == '}') { if (--depth < 0) return false; }
    }
    return seen && depth == 0 && !inStr;
}

static void feed(const char *data, size_t len)
{
    auto &st = State::instance();
    s_rxBuf.append(data, len);
    // Split on newlines; a chunk that ends with '}' without a newline is also treated as complete.
    auto accept = [&](size_t n) {
        char tag = s_rxBuf[0];
        bool ok;
        if (tag == 'S' || tag == 'E') ok = st.handleLine(s_rxBuf.data(), n);
        else                          ok = s_lineHook ? s_lineHook(s_rxBuf.data(), n) : false;
        if (ok) { st.net.rxBle++; st.net.lastRxMs = nowMs(); }
        ESP_LOGI(TAG, "rx %c %u bytes -> %s (total %lu)", tag, (unsigned)n, ok ? "ok" : "rejected",
                 (unsigned long)st.net.rxBle);
    };
    for (;;) {
        size_t nl = s_rxBuf.find('\n');
        if (nl == std::string::npos) break;
        if (nl > 0) accept(nl);
        s_rxBuf.erase(0, nl + 1);
    }
    if (s_rxBuf.size() > 2 && s_rxBuf.back() == '}' &&
        completeObject(s_rxBuf.data() + 2, s_rxBuf.size() - 2)) {
        accept(s_rxBuf.size());
        s_rxBuf.clear();
    }
    if (s_rxBuf.size() > 8192) s_rxBuf.clear();
}

static int rxAccess(uint16_t, uint16_t, struct ble_gatt_access_ctxt *ctxt, void *)
{
    if (ctxt->op != BLE_GATT_ACCESS_OP_WRITE_CHR) return BLE_ATT_ERR_UNLIKELY;
    uint16_t len = OS_MBUF_PKTLEN(ctxt->om);
    char buf[600];
    if (len > sizeof(buf)) len = sizeof(buf);
    uint16_t out = 0;
    if (ble_hs_mbuf_to_flat(ctxt->om, buf, len, &out) != 0) return BLE_ATT_ERR_UNLIKELY;
    const uint8_t magic = out >= 1 ? (uint8_t)buf[0] : 0;
    if (out >= 2 && (magic == SPEECH_MAGIC || magic == TUNNEL_MAGIC)) {
        // Binary, never text: hand it over whole and keep it out of the line buffer.
        for (int i = 0; i < MAX_FRAME_HOOKS; ++i) {
            if (s_frameHooks[i] && s_frameHooks[i]((const uint8_t *)buf, out)) break;
        }
        return 0;
    }
    feed(buf, out);
    return 0;
}

static int txAccess(uint16_t, uint16_t, struct ble_gatt_access_ctxt *, void *)
{
    return 0;   // notify-only characteristic, nothing to read
}

static const struct ble_gatt_chr_def NUS_CHRS[] = {
    { .uuid = &NUS_RX_UUID.u, .access_cb = rxAccess,
      .flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP },
    { .uuid = &NUS_TX_UUID.u, .access_cb = txAccess,
      .flags = BLE_GATT_CHR_F_NOTIFY, .val_handle = &s_txHandle },
    { 0 },
};
static const struct ble_gatt_svc_def NUS_SVCS[] = {
    { .type = BLE_GATT_SVC_TYPE_PRIMARY, .uuid = &NUS_SVC_UUID.u, .characteristics = NUS_CHRS },
    { 0 },
};

static void advertise();

static int gapEvent(struct ble_gap_event *ev, void *)
{
    auto &net = State::instance().net;
    switch (ev->type) {
    case BLE_GAP_EVENT_CONNECT:
        if (ev->connect.status == 0) {
            net.bleConn = true; net.bleAdv = false;
            s_connHandle = ev->connect.conn_handle;
            s_txSubscribed = false;
            ESP_LOGI(TAG, "central connected (handle %u)", s_connHandle);
        } else {
            advertise();
        }
        break;
    case BLE_GAP_EVENT_DISCONNECT:
        net.bleConn = false;
        s_connHandle = BLE_HS_CONN_HANDLE_NONE;
        s_txSubscribed = false;
        ESP_LOGI(TAG, "central disconnected (reason %d); advertising again", ev->disconnect.reason);
        s_rxBuf.clear();
        advertise();
        break;
    case BLE_GAP_EVENT_ADV_COMPLETE:
        advertise();
        break;
    case BLE_GAP_EVENT_MTU:
        s_mtu = ev->mtu.value;
        ESP_LOGI(TAG, "MTU %u", ev->mtu.value);
        break;
    case BLE_GAP_EVENT_SUBSCRIBE:
        if (ev->subscribe.attr_handle == s_txHandle) {
            s_txSubscribed = ev->subscribe.cur_notify;
            ESP_LOGI(TAG, "TX notify %s", s_txSubscribed ? "subscribed" : "unsubscribed");
        }
        break;
    default:
        break;
    }
    return 0;
}

static void advertise()
{
    struct ble_hs_adv_fields f = {};
    f.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    f.name = (uint8_t *)BLE_NAME;
    f.name_len = strlen(BLE_NAME);
    f.name_is_complete = 1;
    int rc = ble_gap_adv_set_fields(&f);
    if (rc) { setErr("adv fields failed", rc); return; }

    struct ble_hs_adv_fields rsp = {};
    rsp.uuids128 = &NUS_SVC_UUID;
    rsp.num_uuids128 = 1;
    rsp.uuids128_is_complete = 1;
    ble_gap_adv_rsp_set_fields(&rsp);

    struct ble_gap_adv_params p = {};
    p.conn_mode = BLE_GAP_CONN_MODE_UND;
    p.disc_mode = BLE_GAP_DISC_MODE_GEN;
    rc = ble_gap_adv_start(s_ownAddrType, nullptr, BLE_HS_FOREVER, &p, gapEvent, nullptr);
    if (rc) { setErr("adv start failed", rc); return; }
    State::instance().net.bleAdv = true;
    State::instance().net.bleErr[0] = 0;
    ESP_LOGI(TAG, "advertising as %s", BLE_NAME);
}

static void onSync()
{
    int rc = ble_hs_util_ensure_addr(0);
    if (rc) { setErr("no BLE address", rc); return; }
    rc = ble_hs_id_infer_auto(0, &s_ownAddrType);
    if (rc) { setErr("addr type failed", rc); return; }
    State::instance().net.bleReady = true;
    advertise();
}

static void onReset(int reason)
{
    State::instance().net.bleAdv = false;
    State::instance().net.bleConn = false;
    setErr("host reset", reason);
}

static void hostTask(void *)
{
    nimble_port_run();              // returns on nimble_port_stop()
    nimble_port_freertos_deinit();
}

bool bleConnected() { return s_connHandle != BLE_HS_CONN_HANDLE_NONE; }
bool bleSubscribed() { return bleConnected() && s_txSubscribed; }
int  bleMaxPayload()
{
    // Ask the stack rather than trusting the cached value: the MTU-exchange event can arrive before
    // (or well after) the connect event, so a cached copy is easily stale or reset to the 23-byte default.
    uint16_t mtu = bleConnected() ? ble_att_mtu(s_connHandle) : 0;
    if (mtu < 23) mtu = s_mtu;
    int n = (int)mtu - 3;
    return n < 20 ? 20 : n;
}
void setLineHook(LineHook hook) { s_lineHook = hook; }
void setFrameHook(FrameHook hook)
{
    for (int i = 0; i < MAX_FRAME_HOOKS; ++i) {
        if (s_frameHooks[i] == hook) return;          // idempotent
        if (s_frameHooks[i] == nullptr) {
            s_frameHooks[i] = hook;
            return;
        }
    }
    ESP_LOGW(TAG, "frame hook table full, hook dropped");
}

bool bleNotify(const uint8_t *data, size_t len)
{
    if (!bleConnected() || !s_txSubscribed || len == 0) return false;
    struct os_mbuf *om = ble_hs_mbuf_from_flat(data, (uint16_t)len);
    if (!om) return false;
    int rc = ble_gatts_notify_custom(s_connHandle, s_txHandle, om);   // consumes om
    if (rc) ESP_LOGD(TAG, "notify rc=%d", rc);
    return rc == 0;
}

bool bleSendLine(const char *line, size_t len)
{
    if (!bleConnected() || !s_txSubscribed) return false;
    std::string buf(line, len);
    if (buf.empty() || buf.back() != '\n') buf.push_back('\n');
    const size_t chunk = (size_t)bleMaxPayload();
    for (size_t off = 0; off < buf.size(); off += chunk) {
        size_t n = std::min(chunk, buf.size() - off);
        bool ok = false;
        for (int retry = 0; retry < 20 && !ok; retry++) {          // controller buffers can be momentarily full
            ok = bleNotify((const uint8_t *)buf.data() + off, n);
            if (!ok) vTaskDelay(pdMS_TO_TICKS(5));
        }
        if (!ok) { ESP_LOGW(TAG, "tx line dropped (%u bytes)", (unsigned)n); return false; }
    }
    return true;
}

void startBle()
{
    if (s_started) return;
    s_started = true;
    esp_err_t r = nvs_flash_init();  // NimBLE/PHY calibration data lives in NVS
    if (r == ESP_ERR_NVS_NO_FREE_PAGES || r == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        r = nvs_flash_init();
    }
    if (r != ESP_OK) { setErr("nvs init failed", (int)r); return; }

    r = nimble_port_init();
    if (r != ESP_OK) { setErr("nimble init failed", (int)r); return; }
    ble_hs_cfg.sync_cb = onSync;
    ble_hs_cfg.reset_cb = onReset;
    ble_svc_gap_init();
    ble_svc_gatt_init();
    int rc = ble_gatts_count_cfg(NUS_SVCS);
    if (rc == 0) rc = ble_gatts_add_svcs(NUS_SVCS);
    if (rc) { setErr("gatt svc failed", rc); return; }
    ble_svc_gap_device_name_set(BLE_NAME);
    nimble_port_freertos_init(hostTask);
    ESP_LOGI(TAG, "NimBLE started, waiting for sync");
}

} // namespace claude_hud
