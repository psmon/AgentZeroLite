// Shared session/usage model for the Claude HUD. Fed by the BLE transport with the same
// "S {json}" (status) / "E {json}" (event) line protocol as project/samples/claude_hud.
#pragma once
#include <cstdint>
#include <cstddef>
#include <mutex>

namespace claude_hud {

constexpr int      MAX_SESSIONS    = 5;        // = CREW slots; oldest idle session is recycled first
constexpr uint32_t SESSION_TTL_MS  = 180000;   // no news for 3 min -> slot freed
constexpr uint32_t SESSION_IDLE_MS = 45000;    // 45 s without news -> idle

struct Session {
    bool     used = false;
    char     id[40] = "";
    char     host[16] = "";        // sending machine (COMPUTERNAME)
    char     label[24] = "";       // repo / folder name (shown as the CREW name tag)
    char     state[16] = "idle";   // idle/prompt_start/thinking/tool/tool_end/done/subagent
    char     activity[64] = "";
    char     model[24] = "-";
    float    costUsd = 0;
    float    ctxUsedPct = 0;
    float    energy = 0;           // activity energy 0..100: +25 per event, decays ~30 s
    uint32_t events = 0;
    uint32_t turnStartMs = 0;
    uint32_t lastSeenMs = 0;
};

struct Limits {                    // account-wide rate limits (latest wins)
    bool  has = false;
    float rl5hPct = 0;  long rl5hResetIn = 0;
    float rl7dPct = 0;  long rl7dResetIn = 0;
};

struct NetInfo {                   // BLE status for the INFO tile
    bool     bleReady = false;     // stack started
    bool     bleAdv = false;
    bool     bleConn = false;
    char     bleErr[48] = "";      // last start/advertise failure, shown on the INFO tile
    uint32_t rxBle = 0;
    uint32_t lastRxMs = 0;
};

uint32_t nowMs();

// True when a session counts as idle (no news for SESSION_IDLE_MS, or its turn ended).
bool sessionIdle(const Session &s, uint32_t now);
// Activity level for the CREW state machine: 0 idle, 1 low, 2 mid, 3 high.
int  sessionLevel(const Session &s, uint32_t now);

class State {
public:
    static State &instance();

    // One protocol line: 'S'/'E', space, JSON. Returns true if accepted.
    bool handleLine(const char *line, size_t len);
    bool applyStatus(const char *json);
    bool applyEvent(const char *json);

    void     snapshot(Session (&out)[MAX_SESSIONS], Limits &lim);
    uint32_t version();            // bumps on every accepted message
    int      activeCount();
    bool     anyActive();

    NetInfo net;                   // plain fields written by the BLE task, read by the UI

private:
    std::mutex m_;
    Session    s_[MAX_SESSIONS];
    Limits     lim_;
    uint32_t   ver_ = 1;

    Session *get(const char *id);  // caller holds m_
    void     expire();             // caller holds m_
};

} // namespace claude_hud
