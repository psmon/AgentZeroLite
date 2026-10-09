// The microphone as a device service, not one app's property.
//
// There is one ES7210 and one esp_codec_dev handle for it. The Chat app used to own that
// handle for the life of the firmware, which is why AskBot could not record: a second
// bsp_audio_codec_microphone_init() has nowhere to go. Same story as WiFi, same fix - one
// owner, reference counted, whoever is holding it gets the audio.
//
// Gain lives here too (NVS, shared with the Settings screen), because it is a property of the
// microphone rather than of a conversation.
#pragma once

#include <cstddef>
#include <cstdint>

namespace device_mic {

constexpr int SAMPLE_RATE = 16000;   // what both apps send and what whisper expects

// Opens the codec if this is the first holder. Returns false when the codec refuses, in
// which case nothing was acquired.
bool acquire();

// Drops one hold; the codec is closed when the last holder lets go, so the other app can
// take it.
void release();

// True once the codec has been opened at least once, i.e. the microphone works at all.
bool ok();

// Blocking read of `bytes` bytes of 16 kHz mono PCM16. Returns false on a codec error.
bool read(void *buffer, size_t bytes);

int  gain();              // dB, 0..60
void setGain(int db);     // applied immediately when open, stored in NVS either way

} // namespace device_mic
