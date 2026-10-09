// An Akka.NET classic-remoting peer, in C++.
//
// What it does: opens one TCP connection to a .NET ActorSystem that has
// Akka.Remote enabled, completes the Akka association handshake, keeps it alive
// with heartbeats, and sends/receives user messages (tell, and ask via a
// /temp/... sender path).
//
// Why one connection is enough: akka.remote.use-passive-connections defaults to
// "on", so the .NET side reuses the connection we opened for its own outbound
// traffic to us instead of dialling back. That is what lets a device sit behind
// NAT - or have no listening socket at all - and still receive replies.
//
// What it deliberately does not do: cluster membership, DeathWatch, Akka
// serializers other than plain strings, or reliable system-message delivery
// (inbound system messages are acked so the peer stops resending, nothing more).
#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "akka/akka_wire.h"
#include "akka/transport.h"

namespace akka {

struct ClientConfig {
    // The .NET node: system name and the address it advertises
    // (akka.remote.dot-netty.tcp public-hostname / port).
    Address remote;

    // What we claim to be in the handshake. Every sender path we emit is built
    // from this, and the peer registers its outbound endpoint under this
    // address, so it has to stay fixed for the life of the association. The
    // host/port do not have to be reachable while passive connections are on.
    Address local;

    uint64_t uid = 0;  // 0: derive one at Connect() time

    int connect_timeout_ms = 5000;
    int handshake_timeout_ms = 10000;
    // akka.remote.transport-failure-detector.heartbeat-interval defaults to 4s,
    // with acceptable-heartbeat-pause 120s before the peer gives up on us.
    int heartbeat_interval_ms = 4000;
};

// One message delivered to a local (client-side) actor.
struct Message {
    std::string sender_path;  // full akka.tcp:// path of the sender, usable as a reply target
    std::string text;         // decoded payload when it is a string or JSON, else empty
    const std::vector<uint8_t>* bytes = nullptr;  // raw payload, always available
    int32_t serializer_id = 0;
    std::string manifest;
};

class RemoteClient {
public:
    using LogFn = std::function<void(const char* level, const std::string& message)>;
    using ReplyFn = std::function<void(uint64_t correlation, const std::string& text)>;
    using MessageFn = std::function<void(const Envelope& envelope, const std::string& text)>;
    // A local actor's receive function. Called from whichever task drives Poll().
    using Receive = std::function<void(const Message& message)>;

    RemoteClient(ClientConfig config, std::unique_ptr<IByteStream> stream);
    ~RemoteClient();

    void set_logger(LogFn logger) { log_ = std::move(logger); }
    void set_reply_handler(ReplyFn handler) { on_reply_ = std::move(handler); }
    void set_message_handler(MessageFn handler) { on_message_ = std::move(handler); }

    // TCP connect, send ASSOCIATE, wait for the peer's ASSOCIATE reply.
    bool Connect();
    // Sends DISASSOCIATE (best effort) and closes the socket.
    void Disconnect();

    bool associated() const { return associated_; }
    const Address& peer() const { return peer_; }
    uint64_t peer_uid() const { return peer_uid_; }

    // Registers a local actor under /user/<name> on this client's own address.
    // Messages the peer sends to that path are handed to `receive`, and the
    // sender it sees is a path it can reply to - which is what makes the device a
    // participant in the actor system rather than a request/response client.
    void Register(const std::string& name, Receive receive);

    // Full path of a local actor, e.g. akka.tcp://askbot-device@ip:port/user/chat.
    std::string LocalPath(const std::string& name) const { return config_.local.PathOf("/user/" + name); }

    // Fire-and-forget to e.g. "/user/ask". Sender is our own /user/client path.
    bool Tell(const std::string& actor_path, const std::string& text);

    // Same, but sent *as* a registered local actor, so the peer's Sender.Tell(...)
    // lands back in that actor's receive function.
    bool TellAs(const std::string& local_name, const std::string& actor_path, const std::string& text);

    // Raw bytes as a .NET byte[] (serializer id 4). Used for audio frames: base64
    // inside JSON would cost a third more on a link this narrow.
    bool TellBytesAs(const std::string& local_name, const std::string& actor_path, const uint8_t* data,
                     size_t len);

    // Sends with a fresh /temp/... sender path so the actor's Sender.Tell(...)
    // comes back to us. Returns a correlation id (never 0) that the reply
    // handler is called with, or 0 if the send failed.
    uint64_t Ask(const std::string& actor_path, const std::string& text);

    // Pumps inbound bytes and outbound heartbeats. Returns false once the link
    // is gone (peer closed, disassociated, or a protocol error).
    bool Poll(int timeout_ms);

    size_t pending_asks() const { return pending_.size(); }

private:
    bool SendPdu(const std::vector<uint8_t>& body);
    bool SendString(const std::string& actor_path, const std::string& text,
                    const std::string& sender_path);
    bool SendPayload(const std::string& actor_path, const std::string& sender_path,
                     int32_t serializer_id, const std::string& manifest, const uint8_t* data,
                     size_t len);
    bool PumpReads(int timeout_ms);
    bool TakeFrame(std::vector<uint8_t>* frame);
    bool HandlePdu(const Pdu& pdu);
    void HandleEnvelope(const Envelope& envelope);
    void MaybeHeartbeat();
    void Log(const char* level, const std::string& message) const;

    ClientConfig config_;
    std::unique_ptr<IByteStream> stream_;
    LogFn log_;
    ReplyFn on_reply_;
    MessageFn on_message_;

    bool associated_ = false;
    Address peer_;
    uint64_t peer_uid_ = 0;

    std::vector<uint8_t> rx_;
    int64_t last_heartbeat_ms_ = 0;

    uint64_t next_correlation_ = 1;
    uint64_t next_temp_ = 1;
    std::map<std::string, uint64_t> pending_;  // temp actor name -> correlation
    std::map<std::string, Receive> local_actors_;  // name -> receive
};

// Milliseconds from a monotonic clock; exposed because the CLI wants the same one.
int64_t NowMs();

}  // namespace akka
