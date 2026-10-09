#include "akka/akka_wire.h"

#include "akka/pb.h"

namespace akka {
namespace {

// AddressData: system=1, hostname=2, port=3 (uint32), protocol=4
pb::Writer EncodeAddress(const Address& a)
{
    pb::Writer w;
    w.AddString(1, a.system);
    w.AddString(2, a.host);
    w.AddVarint(3, a.port);
    w.AddString(4, a.protocol);
    return w;
}

bool DecodeAddress(const uint8_t* data, size_t len, Address* out)
{
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (field == 3 && wire == pb::kVarint) {
            uint64_t v;
            if (!r.Varint(&v)) return false;
            out->port = static_cast<uint32_t>(v);
            continue;
        }
        if (wire != pb::kLenDelim) {
            if (!r.Skip(wire)) return false;
            continue;
        }
        const uint8_t* d;
        size_t n;
        if (!r.LenDelim(&d, &n)) return false;
        const std::string s(reinterpret_cast<const char*>(d), n);
        switch (field) {
            case 1: out->system = s; break;
            case 2: out->host = s; break;
            case 4: out->protocol = s; break;
            default: break;
        }
    }
    return true;
}

// AkkaProtocolMessage: payload=1 (bytes), instruction=2 (AkkaControlMessage)
std::vector<uint8_t> WrapControl(ControlCommand command, const pb::Writer* handshake)
{
    pb::Writer control;                                        // AkkaControlMessage
    control.AddVarint(1, static_cast<uint64_t>(command));      //   commandType = 1
    if (handshake != nullptr) control.AddMessage(2, *handshake);  // handshakeInfo = 2

    pb::Writer pdu;                                            // AkkaProtocolMessage
    pdu.AddMessage(2, control);                                //   instruction = 2
    return pdu.take();
}

// ActorRefData: path=1
pb::Writer EncodeActorRef(const std::string& path)
{
    pb::Writer w;
    w.AddString(1, path);
    return w;
}

bool DecodeActorRefPath(const uint8_t* data, size_t len, std::string* out)
{
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (field == 1 && wire == pb::kLenDelim) {
            const uint8_t* d;
            size_t n;
            if (!r.LenDelim(&d, &n)) return false;
            out->assign(reinterpret_cast<const char*>(d), n);
            continue;
        }
        if (!r.Skip(wire)) return false;
    }
    return true;
}

// Payload: message=1 (bytes), serializerId=2 (int32), messageManifest=3 (bytes)
pb::Writer EncodePayloadField(const Envelope& e)
{
    pb::Writer w;
    if (!e.message.empty()) w.AddBytes(1, e.message.data(), e.message.size());
    w.AddVarint(2, static_cast<uint64_t>(static_cast<uint32_t>(e.serializer_id)));
    if (!e.manifest.empty()) w.AddString(3, e.manifest);
    return w;
}

bool DecodePayloadField(const uint8_t* data, size_t len, Envelope* out)
{
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (field == 2 && wire == pb::kVarint) {
            uint64_t v;
            if (!r.Varint(&v)) return false;
            out->serializer_id = static_cast<int32_t>(static_cast<uint32_t>(v));
            continue;
        }
        if (wire != pb::kLenDelim) {
            if (!r.Skip(wire)) return false;
            continue;
        }
        const uint8_t* d;
        size_t n;
        if (!r.LenDelim(&d, &n)) return false;
        if (field == 1) {
            out->message.assign(d, d + n);
        } else if (field == 3) {
            out->manifest.assign(reinterpret_cast<const char*>(d), n);
        }
    }
    return true;
}

bool DecodeEnvelope(const uint8_t* data, size_t len, Envelope* out)
{
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (field == 5 && wire == pb::kFixed64) {  // seq
            uint64_t v;
            if (!r.Fixed64(&v)) return false;
            out->seq = v;
            continue;
        }
        if (wire != pb::kLenDelim) {
            if (!r.Skip(wire)) return false;
            continue;
        }
        const uint8_t* d;
        size_t n;
        if (!r.LenDelim(&d, &n)) return false;
        switch (field) {
            case 1:
                if (!DecodeActorRefPath(d, n, &out->recipient_path)) return false;
                break;
            case 2:
                if (!DecodePayloadField(d, n, out)) return false;
                break;
            case 4:
                if (!DecodeActorRefPath(d, n, &out->sender_path)) return false;
                break;
            default:
                break;
        }
    }
    return true;
}

// The handshake travels at the WRAPPED TRANSPORT layer, where the scheme is
// plain "tcp"; AkkaProtocolTransport augments it to "akka.tcp" on receipt. Send
// "akka.tcp" here and the peer registers you as akka.akka.tcp://..., so replies
// to your akka.tcp:// sender path find no endpoint and it dials your advertised
// port instead - which on a device is nothing at all.
std::string TransportScheme(const std::string& protocol)
{
    const std::string prefix = "akka.";
    if (protocol.compare(0, prefix.size(), prefix) == 0) return protocol.substr(prefix.size());
    return protocol;
}

std::string AkkaScheme(const std::string& protocol)
{
    if (protocol.empty()) return "akka.tcp";
    const std::string prefix = "akka.";
    if (protocol.compare(0, prefix.size(), prefix) == 0) return protocol;
    return prefix + protocol;
}

}  // namespace

std::string Address::ToString() const
{
    return protocol + "://" + system + "@" + host + ":" + std::to_string(port);
}

std::string Address::PathOf(const std::string& local_path) const
{
    if (local_path.empty()) return ToString();
    if (local_path.compare(0, 1, "/") == 0) return ToString() + local_path;
    return ToString() + "/" + local_path;
}

std::vector<uint8_t> EncodeAssociate(const Handshake& info)
{
    pb::Writer handshake;                                   // AkkaHandshakeInfo
    Address origin_address = info.origin;
    origin_address.protocol = TransportScheme(origin_address.protocol);
    const pb::Writer origin = EncodeAddress(origin_address);
    handshake.AddMessage(1, origin);                        //   origin = 1
    handshake.AddFixed64(2, info.uid);                      //   uid = 2 (fixed64)
    // cookie = 3 is marked "not used" in WireFormats.proto, so it is omitted.
    return WrapControl(ControlCommand::kAssociate, &handshake);
}

std::vector<uint8_t> EncodeHeartbeat()
{
    return WrapControl(ControlCommand::kHeartbeat, nullptr);
}

std::vector<uint8_t> EncodeControl(ControlCommand command)
{
    return WrapControl(command, nullptr);
}

std::vector<uint8_t> EncodeMessage(const Envelope& e)
{
    pb::Writer envelope;                                        // RemoteEnvelope
    const pb::Writer recipient = EncodeActorRef(e.recipient_path);
    envelope.AddMessage(1, recipient);                          //   recipient = 1
    const pb::Writer payload = EncodePayloadField(e);
    envelope.AddMessage(2, payload);                            //   message = 2
    if (!e.sender_path.empty()) {
        const pb::Writer sender = EncodeActorRef(e.sender_path);
        envelope.AddMessage(4, sender);                         //   sender = 4
    }
    envelope.AddFixed64(5, e.seq);                              //   seq = 5, always written

    pb::Writer container;                                       // AckAndEnvelopeContainer
    container.AddMessage(2, envelope);                          //   envelope = 2

    pb::Writer pdu;                                             // AkkaProtocolMessage
    pdu.AddBytes(1, container.data(), container.size());        //   payload = 1
    return pdu.take();
}

std::vector<uint8_t> EncodePureAck(uint64_t cumulative_ack)
{
    pb::Writer ack;                                             // AcknowledgementInfo
    ack.AddFixed64(1, cumulative_ack);                          //   cumulativeAck = 1
    pb::Writer container;                                       // AckAndEnvelopeContainer
    container.AddMessage(1, ack);                               //   ack = 1
    pb::Writer pdu;
    pdu.AddBytes(1, container.data(), container.size());
    return pdu.take();
}

bool DecodePdu(const uint8_t* data, size_t len, Pdu* out)
{
    *out = Pdu{};
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (wire != pb::kLenDelim) {
            if (!r.Skip(wire)) return false;
            continue;
        }
        const uint8_t* d;
        size_t n;
        if (!r.LenDelim(&d, &n)) return false;

        if (field == 1) {  // payload
            out->payload.assign(d, d + n);
            continue;
        }
        if (field != 2) continue;

        out->is_control = true;
        pb::Reader cr(d, n);
        uint32_t cfield, cwire;
        while (cr.Next(&cfield, &cwire)) {
            if (cfield == 1 && cwire == pb::kVarint) {
                uint64_t v;
                if (!cr.Varint(&v)) return false;
                out->command = static_cast<ControlCommand>(static_cast<uint32_t>(v));
                continue;
            }
            if (cfield == 2 && cwire == pb::kLenDelim) {
                const uint8_t* hd;
                size_t hn;
                if (!cr.LenDelim(&hd, &hn)) return false;
                out->has_handshake = true;
                pb::Reader hr(hd, hn);
                uint32_t hfield, hwire;
                while (hr.Next(&hfield, &hwire)) {
                    if (hfield == 1 && hwire == pb::kLenDelim) {
                        const uint8_t* ad;
                        size_t an;
                        if (!hr.LenDelim(&ad, &an)) return false;
                        if (!DecodeAddress(ad, an, &out->handshake.origin)) return false;
                        // Peer sends "tcp"; store it the way actor paths spell it.
                        out->handshake.origin.protocol = AkkaScheme(out->handshake.origin.protocol);
                        continue;
                    }
                    if (hfield == 2 && hwire == pb::kFixed64) {
                        if (!hr.Fixed64(&out->handshake.uid)) return false;
                        continue;
                    }
                    if (!hr.Skip(hwire)) return false;
                }
                continue;
            }
            if (!cr.Skip(cwire)) return false;
        }
    }
    // A PDU carries either a control instruction or a payload; neither means malformed.
    return out->is_control || !out->payload.empty();
}

bool DecodeAckAndEnvelope(const uint8_t* data, size_t len, AckAndEnvelope* out)
{
    *out = AckAndEnvelope{};
    pb::Reader r(data, len);
    uint32_t field, wire;
    while (r.Next(&field, &wire)) {
        if (wire != pb::kLenDelim) {
            if (!r.Skip(wire)) return false;
            continue;
        }
        const uint8_t* d;
        size_t n;
        if (!r.LenDelim(&d, &n)) return false;

        if (field == 1) {  // AcknowledgementInfo
            out->has_ack = true;
            pb::Reader ar(d, n);
            uint32_t afield, awire;
            while (ar.Next(&afield, &awire)) {
                if (awire != pb::kFixed64) {
                    if (!ar.Skip(awire)) return false;
                    continue;
                }
                uint64_t v;
                if (!ar.Fixed64(&v)) return false;
                if (afield == 1) {
                    out->cumulative_ack = v;
                } else if (afield == 2) {
                    out->nacks.push_back(v);
                }
            }
            continue;
        }
        if (field == 2) {  // RemoteEnvelope
            out->has_envelope = true;
            if (!DecodeEnvelope(d, n, &out->envelope)) return false;
        }
    }
    return true;
}

bool IsStringPayload(int32_t serializer_id, const std::string& manifest)
{
    if (serializer_id != kSerializerPrimitive) return false;
    return manifest == kManifestString || manifest == kManifestStringNetCore ||
           manifest == kManifestStringNetFx;
}

const char* ToString(ControlCommand command)
{
    switch (command) {
        case ControlCommand::kNone: return "NONE";
        case ControlCommand::kAssociate: return "ASSOCIATE";
        case ControlCommand::kDisassociate: return "DISASSOCIATE";
        case ControlCommand::kHeartbeat: return "HEARTBEAT";
        case ControlCommand::kDisassociateShuttingDown: return "DISASSOCIATE_SHUTTING_DOWN";
        case ControlCommand::kDisassociateQuarantined: return "DISASSOCIATE_QUARANTINED";
    }
    return "?";
}

}  // namespace akka
