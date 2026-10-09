#include "hud_state.hpp"
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include "cJSON.h"
#include "esp_timer.h"
#include "esp_log.h"

static const char *TAG = "hud_state";

namespace claude_hud {

uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

bool sessionIdle(const Session &s, uint32_t now)
{
    if (!s.used) return true;
    if (now - s.lastSeenMs > SESSION_IDLE_MS) return true;
    return !strcmp(s.state, "done") || !strcmp(s.state, "idle");
}

int sessionLevel(const Session &s, uint32_t now)
{
    if (sessionIdle(s, now)) return 0;
    // energy fades linearly to 0 over the idle window
    float fade = 1.0f - (float)(now - s.lastSeenMs) / (float)SESSION_IDLE_MS;
    if (fade < 0) fade = 0;
    float e = s.energy * fade;
    if (e >= 60) return 3;
    if (e >= 25) return 2;
    return 1;
}

static void copyStr(char *dst, size_t n, const char *src)
{
    if (!src) { dst[0] = 0; return; }
    strncpy(dst, src, n - 1); dst[n - 1] = 0;
}
static const char *jstr(cJSON *d, const char *k, const char *def)
{
    cJSON *v = cJSON_GetObjectItemCaseSensitive(d, k);
    return (v && cJSON_IsString(v) && v->valuestring) ? v->valuestring : def;
}
static bool jnum(cJSON *d, const char *k, double &out)
{
    cJSON *v = cJSON_GetObjectItemCaseSensitive(d, k);
    if (v && cJSON_IsNumber(v)) { out = v->valuedouble; return true; }
    return false;
}

State &State::instance() { static State s; return s; }

// Slot lookup: existing id -> free slot -> oldest idle slot -> oldest slot.
Session *State::get(const char *id)
{
    if (!id || !*id) id = "?";
    uint32_t now = nowMs();
    for (auto &s : s_) if (s.used && !strcmp(s.id, id)) return &s;
    Session *slot = nullptr;
    for (auto &s : s_) if (!s.used) { slot = &s; break; }
    if (!slot) {
        for (auto &s : s_)
            if (sessionIdle(s, now) && (!slot || s.lastSeenMs < slot->lastSeenMs)) slot = &s;
    }
    if (!slot) {
        slot = &s_[0];
        for (auto &s : s_) if (s.lastSeenMs < slot->lastSeenMs) slot = &s;
    }
    *slot = Session();
    slot->used = true;
    copyStr(slot->id, sizeof(slot->id), id);
    size_t L = strlen(id); const char *sfx = (L > 4) ? id + L - 4 : id;
    snprintf(slot->label, sizeof(slot->label), "#%s", sfx);
    return slot;
}

void State::expire()
{
    uint32_t now = nowMs();
    for (auto &s : s_) if (s.used && now - s.lastSeenMs > SESSION_TTL_MS) s.used = false;
}

static void bump(Session *s, float add)
{
    uint32_t now = nowMs();
    float dt = (float)(now - s->lastSeenMs);
    if (s->lastSeenMs) s->energy *= expf(-dt / 30000.0f);   // ~30 s decay
    s->energy += add;
    if (s->energy > 100) s->energy = 100;
    s->lastSeenMs = now;
}

bool State::applyStatus(const char *json)
{
    cJSON *d = cJSON_Parse(json);
    if (!d) { ESP_LOGW(TAG, "bad status json"); return false; }
    {
        std::lock_guard<std::mutex> lk(m_);
        expire();
        Session *s = get(jstr(d, "session", "?"));
        if (cJSON_HasObjectItem(d, "host"))  copyStr(s->host,  sizeof(s->host),  jstr(d, "host", ""));
        if (cJSON_HasObjectItem(d, "label")) copyStr(s->label, sizeof(s->label), jstr(d, "label", ""));
        if (cJSON_HasObjectItem(d, "model")) copyStr(s->model, sizeof(s->model), jstr(d, "model", "-"));
        double v;
        if (jnum(d, "cost_usd", v))         s->costUsd = (float)v;
        if (jnum(d, "context_used_pct", v)) s->ctxUsedPct = (float)v;
        if (jnum(d, "rl5h_used_pct", v)) {
            lim_.has = true;
            lim_.rl5hPct = (float)v;
            if (jnum(d, "rl5h_reset_in", v)) lim_.rl5hResetIn = (long)v;
            if (jnum(d, "rl7d_used_pct", v)) lim_.rl7dPct = (float)v;
            if (jnum(d, "rl7d_reset_in", v)) lim_.rl7dResetIn = (long)v;
        }
        bump(s, 5);
        ver_++;
    }
    cJSON_Delete(d);
    return true;
}

bool State::applyEvent(const char *json)
{
    cJSON *d = cJSON_Parse(json);
    if (!d) { ESP_LOGW(TAG, "bad event json"); return false; }
    {
        std::lock_guard<std::mutex> lk(m_);
        expire();
        Session *s = get(jstr(d, "session", "?"));
        if (cJSON_HasObjectItem(d, "host"))  copyStr(s->host,  sizeof(s->host),  jstr(d, "host", ""));
        if (cJSON_HasObjectItem(d, "label")) copyStr(s->label, sizeof(s->label), jstr(d, "label", ""));
        const char *type = jstr(d, "type", "");
        copyStr(s->state, sizeof(s->state), type);
        if (cJSON_HasObjectItem(d, "msg")) {
            copyStr(s->activity, sizeof(s->activity), jstr(d, "msg", ""));
        } else {
            snprintf(s->activity, sizeof(s->activity), "%s %s", jstr(d, "tool", ""), jstr(d, "target", ""));
        }
        if (!strcmp(type, "prompt_start")) s->turnStartMs = nowMs();
        s->events++;
        bump(s, 25);
        ver_++;
    }
    cJSON_Delete(d);
    return true;
}

bool State::handleLine(const char *line, size_t len)
{
    while (len && (line[0] == ' ' || line[0] == '\r' || line[0] == '\n')) { line++; len--; }
    while (len && (line[len - 1] == '\r' || line[len - 1] == '\n' || line[len - 1] == ' ')) len--;
    if (len < 3 || line[1] != ' ') return false;
    char *js = (char *)malloc(len);           // (len-2) payload chars + NUL fits in len bytes
    if (!js) return false;
    memcpy(js, line + 2, len - 2); js[len - 2] = 0;
    bool ok = false;
    if (line[0] == 'S')      ok = applyStatus(js);
    else if (line[0] == 'E') ok = applyEvent(js);
    free(js);
    return ok;
}

void State::snapshot(Session (&out)[MAX_SESSIONS], Limits &lim)
{
    std::lock_guard<std::mutex> lk(m_);
    expire();
    for (int i = 0; i < MAX_SESSIONS; i++) out[i] = s_[i];
    lim = lim_;
}

uint32_t State::version() { std::lock_guard<std::mutex> lk(m_); return ver_; }

int State::activeCount()
{
    std::lock_guard<std::mutex> lk(m_);
    uint32_t now = nowMs(); int n = 0;
    for (auto &s : s_) if (s.used && now - s.lastSeenMs < SESSION_IDLE_MS) n++;
    return n;
}

bool State::anyActive()
{
    std::lock_guard<std::mutex> lk(m_);
    uint32_t now = nowMs();
    for (auto &s : s_) if (s.used && !sessionIdle(s, now)) return true;
    return false;
}

} // namespace claude_hud
