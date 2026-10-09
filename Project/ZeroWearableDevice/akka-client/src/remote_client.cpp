#include "akka/remote_client.h"

#include <chrono>
#include <cstdio>

#include <utility>

namespace akka {
namespace {

// akka.remote.dot-netty.tcp.maximum-frame-size defaults to 128000b; anything
// larger on the wire means we lost frame sync, so bail instead of allocating.
constexpr size_t kMaxFrameSize = 128000;
constexpr const char* kTempPrefix = "/temp/";

// Akka only needs the uid to differ from the previous incarnation of this
// address, so clock entropy run through splitmix64 is enough. std::random_device
// is avoided on purpose: under ESP-IDF's newlib it is not a dependable source.
uint64_t DeriveUid()
{
    using namespace std::chrono;
    uint64_t x = static_cast<uint64_t>(system_clock::now().time_since_epoch().count());
    x ^= static_cast<uint64_t>(steady_clock::now().time_since_epoch().count()) * 0x9E3779B97F4A7C15ULL;

    x += 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
    x = x ^ (x >> 31);
    return x == 0 ? 1 : x;
}

}  // namespace

int64_t NowMs()
{
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

RemoteClient::RemoteClient(ClientConfig config, std::unique_ptr<IByteStream> stream)
    : config_(std::move(config)), stream_(std::move(stream))
{
}

RemoteClient::~RemoteClient() { Disconnect(); }

void RemoteClient::Log(const char* level, const std::string& message) const
{
    if (log_) log_(level, message);
}

bool RemoteClient::Connect()
{
    if (!config_.remote.valid() || !config_.local.valid()) {
        Log("error", "remote/local address incomplete (need system, host and port)");
        return false;
    }
    if (config_.uid == 0) config_.uid = DeriveUid();

    rx_.clear();
    associated_ = false;
    peer_ = Address{};
    peer_uid_ = 0;

    if (!stream_->Connect(config_.remote.host, static_cast<uint16_t>(config_.remote.port),
                          config_.connect_timeout_ms)) {
        Log("error", "tcp connect to " + config_.remote.host + ":" +
                         std::to_string(config_.remote.port) + " failed");
        return false;
    }
    Log("info", "tcp connected to " + config_.remote.host + ":" +
                    std::to_string(config_.remote.port));

    Handshake info;
    info.origin = config_.local;
    info.uid = config_.uid;
    if (!SendPdu(EncodeAssociate(info))) {
        Log("error", "sending ASSOCIATE failed");
        stream_->Close();
        return false;
    }
    Log("info", "sent ASSOCIATE as " + config_.local.ToString() + " uid=" +
                    std::to_string(config_.uid));

    // The inbound side answers an association attempt with its own ASSOCIATE
    // (AkkaProtocolTransport, WaitHandshake state), which is what tells us the
    // peer's system name and uid.
    const int64_t deadline = NowMs() + config_.handshake_timeout_ms;
    while (!associated_) {
        if (NowMs() > deadline) {
            Log("error", "handshake timed out");
            stream_->Close();
            return false;
        }
        if (!PumpReads(200)) {
            stream_->Close();
            return false;
        }
    }

    last_heartbeat_ms_ = NowMs();
    return true;
}

void RemoteClient::Disconnect()
{
    if (stream_ && stream_->IsOpen()) {
        if (associated_) SendPdu(EncodeControl(ControlCommand::kDisassociate));
        stream_->Close();
    }
    associated_ = false;
    pending_.clear();
}

bool RemoteClient::SendPdu(const std::vector<uint8_t>& body)
{
    if (!stream_->IsOpen()) return false;
    if (body.size() > kMaxFrameSize) {
        Log("error", "outbound frame too large: " + std::to_string(body.size()));
        return false;
    }
    // DotNetty LengthFieldPrepender: 4-byte length, little-endian by default,
    // not counting the length field itself.
    const uint32_t n = static_cast<uint32_t>(body.size());
    uint8_t header[4] = {
        static_cast<uint8_t>(n & 0xFF),
        static_cast<uint8_t>((n >> 8) & 0xFF),
        static_cast<uint8_t>((n >> 16) & 0xFF),
        static_cast<uint8_t>((n >> 24) & 0xFF),
    };
    if (!stream_->WriteAll(header, sizeof(header))) return false;
    return stream_->WriteAll(body.data(), body.size());
}

bool RemoteClient::SendPayload(const std::string& actor_path, const std::string& sender_path,
                               int32_t serializer_id, const std::string& manifest,
                               const uint8_t* data, size_t len)
{
    if (!associated_) {
        Log("error", "not associated");
        return false;
    }
    Envelope e;
    e.recipient_path = config_.remote.PathOf(actor_path);
    e.sender_path = sender_path;
    e.serializer_id = serializer_id;
    e.manifest = manifest;
    e.message.assign(data, data + len);
    e.seq = kSeqUndefined;
    return SendPdu(EncodeMessage(e));
}

bool RemoteClient::SendString(const std::string& actor_path, const std::string& text,
                              const std::string& sender_path)
{
    // strings go on the wire as UTF-8, with no length prefix or escaping
    return SendPayload(actor_path, sender_path, kSerializerPrimitive, kManifestString,
                       reinterpret_cast<const uint8_t*>(text.data()), text.size());
}

bool RemoteClient::Tell(const std::string& actor_path, const std::string& text)
{
    return SendString(actor_path, text, config_.local.PathOf("/user/client"));
}

void RemoteClient::Register(const std::string& name, Receive receive)
{
    local_actors_[name] = std::move(receive);
}

bool RemoteClient::TellAs(const std::string& local_name, const std::string& actor_path,
                          const std::string& text)
{
    return SendString(actor_path, text, LocalPath(local_name));
}

bool RemoteClient::TellBytesAs(const std::string& local_name, const std::string& actor_path,
                               const uint8_t* data, size_t len)
{
    // ByteArraySerializer (id 4) is a plain Serializer, not a
    // SerializerWithStringManifest, so the manifest field stays empty.
    return SendPayload(actor_path, LocalPath(local_name), kSerializerByteArray, std::string(), data,
                       len);
}

uint64_t RemoteClient::Ask(const std::string& actor_path, const std::string& text)
{
    const std::string temp_name = "$" + std::to_string(next_temp_++);
    const std::string sender = config_.local.PathOf(std::string(kTempPrefix) + temp_name);
    if (!SendString(actor_path, text, sender)) return 0;

    const uint64_t correlation = next_correlation_++;
    pending_[temp_name] = correlation;
    return correlation;
}

bool RemoteClient::Poll(int timeout_ms)
{
    if (!stream_->IsOpen()) return false;
    if (!PumpReads(timeout_ms)) return false;
    MaybeHeartbeat();
    return stream_->IsOpen();
}

void RemoteClient::MaybeHeartbeat()
{
    if (!associated_) return;
    const int64_t now = NowMs();
    if (now - last_heartbeat_ms_ < config_.heartbeat_interval_ms) return;
    last_heartbeat_ms_ = now;
    if (!SendPdu(EncodeHeartbeat())) {
        Log("warn", "heartbeat send failed");
        stream_->Close();
    }
}

bool RemoteClient::PumpReads(int timeout_ms)
{
    uint8_t buf[1024];
    const int n = stream_->Read(buf, sizeof(buf), timeout_ms);
    if (n < 0) {
        Log("warn", "connection closed by peer");
        associated_ = false;
        stream_->Close();
        return false;
    }
    if (n > 0) rx_.insert(rx_.end(), buf, buf + n);

    std::vector<uint8_t> frame;
    while (TakeFrame(&frame)) {
        Pdu pdu;
        if (!DecodePdu(frame.data(), frame.size(), &pdu)) {
            Log("error", "undecodable PDU, dropping link");
            associated_ = false;
            stream_->Close();
            return false;
        }
        if (!HandlePdu(pdu)) return false;
    }
    return true;
}

bool RemoteClient::TakeFrame(std::vector<uint8_t>* frame)
{
    if (rx_.size() < 4) return false;
    const uint32_t len = static_cast<uint32_t>(rx_[0]) | (static_cast<uint32_t>(rx_[1]) << 8) |
                         (static_cast<uint32_t>(rx_[2]) << 16) |
                         (static_cast<uint32_t>(rx_[3]) << 24);
    if (len > kMaxFrameSize) {
        Log("error", "inbound frame length out of range: " + std::to_string(len));
        rx_.clear();
        associated_ = false;
        stream_->Close();
        return false;
    }
    if (rx_.size() < 4 + len) return false;

    frame->assign(rx_.begin() + 4, rx_.begin() + 4 + len);
    rx_.erase(rx_.begin(), rx_.begin() + 4 + len);
    return true;
}

bool RemoteClient::HandlePdu(const Pdu& pdu)
{
    if (pdu.is_control) {
        switch (pdu.command) {
            case ControlCommand::kAssociate:
                if (pdu.has_handshake) {
                    peer_ = pdu.handshake.origin;
                    peer_uid_ = pdu.handshake.uid;
                    if (peer_.system.empty()) peer_.system = config_.remote.system;
                }
                associated_ = true;
                Log("info", "associated with " + peer_.ToString() + " uid=" +
                                std::to_string(peer_uid_));
                return true;

            case ControlCommand::kHeartbeat:
                return true;

            case ControlCommand::kDisassociate:
            case ControlCommand::kDisassociateShuttingDown:
            case ControlCommand::kDisassociateQuarantined:
                Log("warn", std::string("peer sent ") + ToString(pdu.command));
                associated_ = false;
                stream_->Close();
                return false;

            default:
                Log("warn", "unknown control command");
                return true;
        }
    }

    AckAndEnvelope container;
    if (!DecodeAckAndEnvelope(pdu.payload.data(), pdu.payload.size(), &container)) {
        Log("error", "undecodable AckAndEnvelopeContainer");
        return true;  // a bad payload is not a reason to drop the association
    }
    if (container.has_envelope) HandleEnvelope(container.envelope);
    return true;
}

void RemoteClient::HandleEnvelope(const Envelope& e)
{
    // Anything with a real sequence number is a system message travelling on the
    // reliable-delivery path. We do not implement that path, but acking stops the
    // peer from resending it forever.
    if (e.seq != kSeqUndefined) {
        SendPdu(EncodePureAck(e.seq));
    }

    std::string text;
    if (IsStringPayload(e.serializer_id, e.manifest)) {
        text.assign(e.message.begin(), e.message.end());
    } else if (e.serializer_id == kSerializerJson) {
        // NewtonSoftJsonSerializer payloads are UTF-8 JSON text; pass them
        // through verbatim rather than pretending to understand the type.
        text.assign(e.message.begin(), e.message.end());
    }

    // A message addressed to one of our local actors: hand it over with the
    // sender path intact so the handler can answer it.
    const std::string user_prefix = "/user/";
    const size_t user = e.recipient_path.find(user_prefix);
    if (user != std::string::npos) {
        const std::string name = e.recipient_path.substr(user + user_prefix.size());
        auto it = local_actors_.find(name);
        if (it != local_actors_.end()) {
            Message message;
            message.sender_path = e.sender_path;
            message.text = text;
            message.bytes = &e.message;
            message.serializer_id = e.serializer_id;
            message.manifest = e.manifest;
            it->second(message);
            return;
        }
    }

    const size_t temp = e.recipient_path.find(kTempPrefix);
    if (temp != std::string::npos) {
        const std::string name = e.recipient_path.substr(temp + std::string(kTempPrefix).size());
        auto it = pending_.find(name);
        if (it != pending_.end()) {
            const uint64_t correlation = it->second;
            pending_.erase(it);
            if (on_reply_) on_reply_(correlation, text);
            return;
        }
        Log("warn", "reply for unknown temp path " + e.recipient_path);
    }

    if (on_message_) on_message_(e, text);
}

}  // namespace akka
