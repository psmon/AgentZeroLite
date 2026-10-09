#include "akka/pb.h"

namespace akka {
namespace pb {

void Writer::Varint(uint64_t v)
{
    while (v >= 0x80) {
        buf_.push_back(static_cast<uint8_t>(v) | 0x80);
        v >>= 7;
    }
    buf_.push_back(static_cast<uint8_t>(v));
}

void Writer::AddVarint(uint32_t field, uint64_t v)
{
    Tag(field, kVarint);
    Varint(v);
}

void Writer::AddFixed64(uint32_t field, uint64_t v)
{
    Tag(field, kFixed64);
    for (int i = 0; i < 8; ++i) {
        buf_.push_back(static_cast<uint8_t>(v >> (8 * i)));  // protobuf fixed64 is little-endian
    }
}

void Writer::AddBytes(uint32_t field, const void* data, size_t len)
{
    Tag(field, kLenDelim);
    Varint(len);
    const uint8_t* p = static_cast<const uint8_t*>(data);
    buf_.insert(buf_.end(), p, p + len);
}

bool Reader::Next(uint32_t* field, uint32_t* wire)
{
    uint64_t tag = 0;
    if (!Varint(&tag)) return false;
    const uint32_t f = static_cast<uint32_t>(tag >> 3);
    if (f == 0) return false;
    *field = f;
    *wire = static_cast<uint32_t>(tag & 0x7);
    return true;
}

bool Reader::Varint(uint64_t* v)
{
    uint64_t out = 0;
    int shift = 0;
    while (p_ < end_) {
        const uint8_t b = *p_++;
        out |= static_cast<uint64_t>(b & 0x7F) << shift;
        if ((b & 0x80) == 0) {
            *v = out;
            return true;
        }
        shift += 7;
        if (shift > 63) return false;
    }
    return false;
}

bool Reader::Fixed64(uint64_t* v)
{
    if (end_ - p_ < 8) return false;
    uint64_t out = 0;
    for (int i = 0; i < 8; ++i) {
        out |= static_cast<uint64_t>(*p_++) << (8 * i);
    }
    *v = out;
    return true;
}

bool Reader::LenDelim(const uint8_t** data, size_t* len)
{
    uint64_t n = 0;
    if (!Varint(&n)) return false;
    if (static_cast<uint64_t>(end_ - p_) < n) return false;
    *data = p_;
    *len = static_cast<size_t>(n);
    p_ += n;
    return true;
}

bool Reader::Skip(uint32_t wire)
{
    switch (wire) {
        case kVarint: {
            uint64_t dummy;
            return Varint(&dummy);
        }
        case kFixed64: {
            uint64_t dummy;
            return Fixed64(&dummy);
        }
        case kLenDelim: {
            const uint8_t* d;
            size_t n;
            return LenDelim(&d, &n);
        }
        case kFixed32:
            if (end_ - p_ < 4) return false;
            p_ += 4;
            return true;
        default:
            return false;
    }
}

}  // namespace pb
}  // namespace akka
