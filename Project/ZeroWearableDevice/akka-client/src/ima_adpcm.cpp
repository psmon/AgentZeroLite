#include "askbot/ima_adpcm.h"

#include <algorithm>
#include <cstring>

namespace askbot {
namespace {

const int kIndexTable[16] = {-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8};

const int kStepTable[89] = {
    7,     8,     9,     10,    11,    12,    13,    14,    16,    17,    19,    21,
    23,    25,    28,    31,    34,    37,    41,    45,    50,    55,    60,    66,
    73,    80,    88,    97,    107,   118,   130,   143,   157,   173,   190,   209,
    230,   253,   279,   307,   337,   371,   408,   449,   494,   544,   598,   658,
    724,   796,   876,   963,   1060,  1166,  1282,  1411,  1552,  1707,  1878,  2066,
    2272,  2499,  2749,  3024,  3327,  3660,  4026,  4428,  4871,  5358,  5894,  6484,
    7132,  7845,  8630,  9493,  10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
    22385, 24623, 27086, 29794, 32767,
};

int16_t Clamp16(int v)
{
    if (v > 32767) return 32767;
    if (v < -32768) return -32768;
    return static_cast<int16_t>(v);
}

void PutU32(std::vector<uint8_t>* out, uint32_t v)
{
    out->push_back(static_cast<uint8_t>(v));
    out->push_back(static_cast<uint8_t>(v >> 8));
    out->push_back(static_cast<uint8_t>(v >> 16));
    out->push_back(static_cast<uint8_t>(v >> 24));
}

void PutU16(std::vector<uint8_t>* out, uint16_t v)
{
    out->push_back(static_cast<uint8_t>(v));
    out->push_back(static_cast<uint8_t>(v >> 8));
}

void PutTag(std::vector<uint8_t>* out, const char* tag)
{
    for (int i = 0; i < 4; ++i) out->push_back(static_cast<uint8_t>(tag[i]));
}

}  // namespace

bool ParseSpeechFrame(const uint8_t* data, size_t len, SpeechFrame* out)
{
    if (data == nullptr || len < 5 || data[0] != kSpeechMagic) return false;
    out->id = data[1];
    out->seq = static_cast<uint16_t>(data[2] | (data[3] << 8));
    out->block = data + 4;
    out->block_len = len - 4;
    return true;
}

void DecodeAdpcmBlock(const uint8_t* block, size_t len, std::vector<int16_t>* pcm)
{
    if (block == nullptr || len < 5 || pcm == nullptr) return;

    int predictor = static_cast<int16_t>(block[0] | (block[1] << 8));
    int index = block[2];
    if (index < 0) index = 0;
    if (index > 88) index = 88;

    for (size_t i = 4; i < len; ++i) {
        for (int half = 0; half < 2; ++half) {
            const int nibble = half == 0 ? (block[i] & 0x0F) : ((block[i] >> 4) & 0x0F);
            const int step = kStepTable[index];

            int delta = step >> 3;
            if (nibble & 4) delta += step;
            if (nibble & 2) delta += step >> 1;
            if (nibble & 1) delta += step >> 2;

            predictor = Clamp16((nibble & 8) ? predictor - delta : predictor + delta);
            index += kIndexTable[nibble];
            if (index < 0) index = 0;
            if (index > 88) index = 88;

            pcm->push_back(static_cast<int16_t>(predictor));
        }
    }
}

size_t DecodeAdpcmBlockTo(const uint8_t* block, size_t len, uint8_t* out, size_t max_bytes)
{
    if (block == nullptr || len < 5 || out == nullptr) return 0;

    int predictor = static_cast<int16_t>(block[0] | (block[1] << 8));
    int index = block[2];
    if (index < 0) index = 0;
    if (index > 88) index = 88;

    size_t written = 0;
    for (size_t i = 4; i < len; ++i) {
        for (int half = 0; half < 2; ++half) {
            if (written + 2 > max_bytes) return written;

            const int nibble = half == 0 ? (block[i] & 0x0F) : ((block[i] >> 4) & 0x0F);
            const int step = kStepTable[index];

            int delta = step >> 3;
            if (nibble & 4) delta += step;
            if (nibble & 2) delta += step >> 1;
            if (nibble & 1) delta += step >> 2;

            predictor = Clamp16((nibble & 8) ? predictor - delta : predictor + delta);
            index += kIndexTable[nibble];
            if (index < 0) index = 0;
            if (index > 88) index = 88;

            out[written++] = static_cast<uint8_t>(predictor);
            out[written++] = static_cast<uint8_t>(static_cast<uint16_t>(predictor) >> 8);
        }
    }
    return written;
}

size_t EncodeAdpcmBlock(const int16_t* samples, size_t count, AdpcmState* state, uint8_t* out)
{
    if (samples == nullptr || state == nullptr || out == nullptr) return 0;

    out[0] = static_cast<uint8_t>(state->predictor);
    out[1] = static_cast<uint8_t>(state->predictor >> 8);
    out[2] = static_cast<uint8_t>(state->index);
    out[3] = 0;

    for (size_t i = 0; i < count; ++i) {
        const int step = kStepTable[state->index];
        int diff = samples[i] - state->predictor;
        int nibble = 0;
        if (diff < 0) {
            nibble = 8;
            diff = -diff;
        }
        int vp = step >> 3;
        if (diff >= step) {
            nibble |= 4;
            diff -= step;
            vp += step;
        }
        if (diff >= (step >> 1)) {
            nibble |= 2;
            diff -= step >> 1;
            vp += step >> 1;
        }
        if (diff >= (step >> 2)) {
            nibble |= 1;
            vp += step >> 2;
        }

        state->predictor = Clamp16((nibble & 8) ? state->predictor - vp : state->predictor + vp);
        const int index = state->index + kIndexTable[nibble];
        state->index = index < 0 ? 0 : (index > 88 ? 88 : index);

        if ((i & 1) == 0) out[4 + i / 2] = static_cast<uint8_t>(nibble);
        else              out[4 + i / 2] |= static_cast<uint8_t>(nibble << 4);
    }
    return 4 + (count + 1) / 2;
}

size_t BuildMicFrame(uint8_t id, uint16_t seq, const uint8_t* block, size_t block_len, uint8_t* out,
                     size_t out_capacity)
{
    if (block == nullptr || out == nullptr || out_capacity < block_len + 4) return 0;
    out[0] = kMicMagic;
    out[1] = id;
    out[2] = static_cast<uint8_t>(seq);
    out[3] = static_cast<uint8_t>(seq >> 8);
    memcpy(out + 4, block, block_len);
    return block_len + 4;
}

std::vector<uint8_t> PcmToWav(const std::vector<int16_t>& pcm, uint32_t rate)
{
    const uint32_t data_size = static_cast<uint32_t>(pcm.size() * 2);
    std::vector<uint8_t> wav;
    wav.reserve(44 + data_size);

    PutTag(&wav, "RIFF");
    PutU32(&wav, 36 + data_size);
    PutTag(&wav, "WAVE");
    PutTag(&wav, "fmt ");
    PutU32(&wav, 16);
    PutU16(&wav, 1);            // PCM
    PutU16(&wav, 1);            // mono
    PutU32(&wav, rate);
    PutU32(&wav, rate * 2);     // byte rate
    PutU16(&wav, 2);            // block align
    PutU16(&wav, 16);           // bits
    PutTag(&wav, "data");
    PutU32(&wav, data_size);

    for (int16_t s : pcm) {
        wav.push_back(static_cast<uint8_t>(s));
        wav.push_back(static_cast<uint8_t>(s >> 8));
    }
    return wav;
}

}  // namespace askbot
