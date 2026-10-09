// Byte-stream abstraction the remote client sits on.
//
// The Akka protocol layer never touches a socket directly, so the same client
// code runs against winsock on the dev box, lwIP on the ESP32-S3, or a tunnel
// (e.g. BLE) later on - only this interface gets a new implementation.
#pragma once

#include <cstdint>
#include <memory>
#include <string>

namespace akka {

class IByteStream {
public:
    virtual ~IByteStream() = default;

    virtual bool Connect(const std::string& host, uint16_t port, int timeout_ms) = 0;
    virtual void Close() = 0;
    virtual bool IsOpen() const = 0;

    // > 0: bytes read. 0: nothing available within timeout_ms. < 0: closed or error.
    virtual int Read(uint8_t* buf, size_t len, int timeout_ms) = 0;

    // Writes every byte or fails.
    virtual bool WriteAll(const uint8_t* buf, size_t len) = 0;
};

// Blocking TCP stream: winsock on Windows, BSD sockets elsewhere (lwIP included).
std::unique_ptr<IByteStream> MakeTcpStream();

}  // namespace akka
