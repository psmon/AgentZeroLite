// Device-wide voice preferences: input language, output language, output voice.
//
// Lives in this component because it is the one both chat and askbot already depend on -
// putting it in the Settings app would make brookesia_app_chat depend on Settings, which
// already depends on chat for the audio codec, and that is a cycle.
//
// Input and output are separate on purpose:
//   - the input language is a hint to whisper; telling it "ko" instead of letting it guess
//     measurably improves recognition and cuts the time spent detecting,
//   - the output language is what SuperTonic speaks, and the two are genuinely different
//     settings (ask in Korean, answer in English is a reasonable thing to want).
//
// The voice list is SuperTonic's 10 speaker styles. They are style embeddings extracted
// from reference audio with no language binding (the model is "opensource-multilingual"),
// so every voice can speak every language - verified by synthesising F1/M2 in Korean and
// F3/M5 in English. The host falls back to its configured default if a voice is missing.
#pragma once

#include <cstdint>

namespace device_voice {

struct Prefs {
    const char *inLang;    // "auto" | "ko" | "en"
    const char *outLang;   // "ko" | "en"
    const char *voice;     // "F1".."F5", "M1".."M5"
};

// Reads the stored preferences (NVS), falling back to auto / ko / F1.
Prefs get();

// Each setter stores the value and returns what is now in effect.
const char *cycleInLang();    // auto -> ko -> en -> auto
const char *cycleOutLang();   // ko -> en -> ko
const char *cycleVoice();     // F1..F5, M1..M5, wrapping

// Set a value directly (the host can do this over the link; the UI cycles instead).
// Unknown values are ignored, so a stale host cannot wedge the device on a voice the
// synthesiser does not have.
bool setInLang(const char *code);
bool setOutLang(const char *code);
bool setVoice(const char *id);

// A label for the UI, e.g. "auto" or "한국어".
const char *langLabel(const char *code);

} // namespace device_voice
