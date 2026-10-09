// Akka's byte stream, carried over the BLE link this firmware already has.
//
// Akka classic remoting needs an ordered, reliable byte stream - not IP. BLE gives
// exactly that: notifications and writes on one connection are delivered in order or
// the link drops. So instead of bringing up WiFi (which starved the internal DMA heap
// and tore the screen), the PDUs ride the existing Nordic UART Service and a small
// bridge on the PC relays them to the Akka node's TCP port.
//
// Chunks are tagged 0xAB so they cannot be confused with the HUD's text lines (ASCII
// tags) or the Chat app's 0xA6 speech frames:
//
//     0xAB | raw stream bytes ...
//
// The 4-byte length prefix inside the Akka framing is what re-assembles messages, so
// chunk boundaries carry no meaning and can follow the MTU.
#pragma once

#include <memory>

#include "akka/transport.h"

namespace askbot {

// The tag that marks one tunnel chunk in either direction.
constexpr uint8_t kTunnelMagic = 0xAB;

// A stream backed by the shared NUS transport. Connect() waits for a central to be
// connected and subscribed rather than dialling anything.
std::unique_ptr<akka::IByteStream> MakeBleStream();

} // namespace askbot
