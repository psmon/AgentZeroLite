// Minimal protobuf wire codec - only what Akka's remoting PDUs need.
//
// Akka classic remoting uses eight small proto3 messages whose shape is frozen
// since 1.3 (WireFormats.proto / ContainerFormats.proto). Hand-coding the wire
// format keeps this module free of a codegen step and of nanopb, which matters
// when the same sources have to build under ESP-IDF.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace akka {
namespace pb {

enum WireType : uint32_t {
    kVarint = 0,
    kFixed64 = 1,
    kLenDelim = 2,
    kFixed32 = 5,
};

class Writer {
public:
    void Varint(uint64_t v);
    void Tag(uint32_t field, uint32_t wire) { Varint(static_cast<uint64_t>(field) << 3 | wire); }

    void AddVarint(uint32_t field, uint64_t v);
    void AddFixed64(uint32_t field, uint64_t v);
    void AddBytes(uint32_t field, const void* data, size_t len);
    void AddString(uint32_t field, const std::string& s) { AddBytes(field, s.data(), s.size()); }
    void AddMessage(uint32_t field, const Writer& sub) { AddBytes(field, sub.data(), sub.size()); }

    const uint8_t* data() const { return buf_.empty() ? nullptr : buf_.data(); }
    size_t size() const { return buf_.size(); }
    const std::vector<uint8_t>& bytes() const { return buf_; }
    std::vector<uint8_t> take() { return std::move(buf_); }

private:
    std::vector<uint8_t> buf_;
};

class Reader {
public:
    Reader(const uint8_t* data, size_t len) : p_(data), end_(data + len) {}

    bool done() const { return p_ >= end_; }

    // Reads the next tag. Returns false at end of buffer or on a malformed tag.
    bool Next(uint32_t* field, uint32_t* wire);

    bool Varint(uint64_t* v);
    bool Fixed64(uint64_t* v);
    bool LenDelim(const uint8_t** data, size_t* len);
    bool Skip(uint32_t wire);

private:
    const uint8_t* p_;
    const uint8_t* end_;
};

}  // namespace pb
}  // namespace akka
