// IMA ADPCM (4-bit) decoder for the speech frames the host streams.
//
// Shared between the PC simulator and the device app, and deliberately the mirror
// image of the host's encoder (AskBot.Host/Voice/DeviceAudio.cs):
//
//   frame : 0xA6 | id(1) | seq(2, little-endian) | block
//   block : [predictor int16 LE][step index u8][reserved u8][nibbles, low first]
//
// Every block carries its own predictor and step index, so a dropped frame costs
// one block (60 ms at 16 kHz) instead of the rest of the utterance.
#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace askbot {

constexpr uint8_t kSpeechMagic = 0xA6;  // host -> device
constexpr uint8_t kMicMagic = 0xA5;     // device -> host

struct SpeechFrame {
    uint8_t id = 0;
    uint16_t seq = 0;
    const uint8_t* block = nullptr;
    size_t block_len = 0;
};

// Recognises and splits a speech frame. False when the buffer is too short or is
// not a speech frame (a text message, or a microphone frame going the other way).
bool ParseSpeechFrame(const uint8_t* data, size_t len, SpeechFrame* out);

// Decodes one ADPCM block, appending 16-bit mono PCM samples to `pcm`.
void DecodeAdpcmBlock(const uint8_t* block, size_t len, std::vector<int16_t>* pcm);

// Same, into a caller-owned buffer; returns bytes written. The device decodes
// straight into its PSRAM playback buffer, so nothing grows or reallocates while
// frames are arriving.
size_t DecodeAdpcmBlockTo(const uint8_t* block, size_t len, uint8_t* out, size_t max_bytes);

// Encoder state: carried across blocks so the predictor tracks the signal, but written into
// every block header so a lost frame costs only that block.
struct AdpcmState {
    int predictor = 0;
    int index = 0;
};

// Encodes `count` samples (even) into `out`, which needs 4 + count/2 bytes. Returns bytes
// written. Mirror of the host's ImaAdpcm.EncodeBlock.
size_t EncodeAdpcmBlock(const int16_t* samples, size_t count, AdpcmState* state, uint8_t* out);

// Builds one microphone frame: 0xA5 | id | seq(2 LE) | block. Returns total length.
size_t BuildMicFrame(uint8_t id, uint16_t seq, const uint8_t* block, size_t block_len, uint8_t* out,
                     size_t out_capacity);

// 16 kHz mono PCM16 wrapped in a WAV container, for saving what was received.
std::vector<uint8_t> PcmToWav(const std::vector<int16_t>& pcm, uint32_t rate);

}  // namespace askbot
