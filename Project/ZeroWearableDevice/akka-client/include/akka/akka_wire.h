// Akka.NET classic remoting PDUs (akka.tcp://), encoded by hand.
//
// Field numbers below are taken from akkadotnet/akka.net @ dev:
//   src/protobuf/WireFormats.proto       - AkkaProtocolMessage, AkkaControlMessage,
//                                          AkkaHandshakeInfo, CommandType,
//                                          AckAndEnvelopeContainer, RemoteEnvelope,
//                                          AcknowledgementInfo
//   src/protobuf/ContainerFormats.proto  - ActorRefData, AddressData, Payload
//
// Framing lives in akka_remote_client: every PDU is prefixed with a 4-byte
// LITTLE-ENDIAN length (DotNetty LengthFieldPrepender with
// akka.remote.dot-netty.tcp.byte-order = "little-endian", the default).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace akka {

// RemoteEnvelope.seq value meaning "unordered, no ack tracking". Akka's decoder
// compares against ulong.MaxValue; proto3 would drop a 0 default, so this field
// must always be written explicitly or the message is mistaken for system
// message #0 and enters the reliable-delivery / resend machinery.
constexpr uint64_t kSeqUndefined = UINT64_MAX;

// akka.actor.serialization-identifiers (Akka.Remote/Configuration/Remote.conf)
constexpr int32_t kSerializerJson = 1;       // NewtonSoftJsonSerializer  (System.Object)
constexpr int32_t kSerializerPrimitive = 17; // PrimitiveSerializers      (string/int/long)
constexpr int32_t kSerializerByteArray = 4; // ByteArraySerializer     (byte[], no manifest)
constexpr int32_t kSerializerMisc = 16;      // MiscMessageSerializer
constexpr int32_t kSerializerSystemMsg = 22; // SystemMessageSerializer

// PrimitiveSerializers manifests. The two long ones are what older/newer .NET
// runtimes emit; accept all three when decoding.
constexpr const char* kManifestString = "S";
constexpr const char* kManifestStringNetCore = "System.String, System.Private.CoreLib";
constexpr const char* kManifestStringNetFx = "System.String, mscorlib";

enum class ControlCommand : uint32_t {
    kNone = 0,
    kAssociate = 1,
    kDisassociate = 2,
    kHeartbeat = 3,
    kDisassociateShuttingDown = 4,
    kDisassociateQuarantined = 5,
};

struct Address {
    std::string protocol = "akka.tcp";
    std::string system;
    std::string host;
    uint32_t port = 0;

    // akka.tcp://system@host:port
    std::string ToString() const;
    // akka.tcp://system@host:port/user/ask
    std::string PathOf(const std::string& local_path) const;
    bool valid() const { return !system.empty() && !host.empty() && port != 0; }
};

struct Handshake {
    Address origin;
    uint64_t uid = 0;
};

struct Pdu {
    bool is_control = false;
    ControlCommand command = ControlCommand::kNone;
    bool has_handshake = false;
    Handshake handshake;
    std::vector<uint8_t> payload;  // AckAndEnvelopeContainer bytes when !is_control
};

struct Envelope {
    std::string recipient_path;
    std::string sender_path;
    int32_t serializer_id = 0;
    std::string manifest;
    std::vector<uint8_t> message;
    uint64_t seq = kSeqUndefined;
};

struct AckAndEnvelope {
    bool has_ack = false;
    uint64_t cumulative_ack = 0;
    std::vector<uint64_t> nacks;
    bool has_envelope = false;
    Envelope envelope;
};

// --- encoders: produce a PDU body, without the 4-byte frame header ---
std::vector<uint8_t> EncodeAssociate(const Handshake& info);
std::vector<uint8_t> EncodeHeartbeat();
std::vector<uint8_t> EncodeControl(ControlCommand command);
std::vector<uint8_t> EncodeMessage(const Envelope& envelope);
std::vector<uint8_t> EncodePureAck(uint64_t cumulative_ack);

// --- decoders ---
bool DecodePdu(const uint8_t* data, size_t len, Pdu* out);
bool DecodeAckAndEnvelope(const uint8_t* data, size_t len, AckAndEnvelope* out);

// True when (serializer_id, manifest) denote a plain .NET string, i.e. the
// payload bytes are the UTF-8 text itself.
bool IsStringPayload(int32_t serializer_id, const std::string& manifest);

const char* ToString(ControlCommand command);

}  // namespace akka
