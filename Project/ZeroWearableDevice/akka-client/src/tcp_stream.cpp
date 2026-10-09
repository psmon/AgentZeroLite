#include "akka/transport.h"

#include <cerrno>
#include <cstring>

#if defined(_WIN32)
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma comment(lib, "ws2_32.lib")
using socket_t = SOCKET;
static const socket_t kInvalidSocket = INVALID_SOCKET;
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
using socket_t = int;
static const socket_t kInvalidSocket = -1;
#endif

namespace akka {
namespace {

#if defined(_WIN32)
// Winsock needs one process-wide startup; a function-local static gives us that
// without an init order problem.
void EnsureWinsock()
{
    static bool started = [] {
        WSADATA data;
        return WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();
    (void)started;
}

void CloseSocket(socket_t s) { closesocket(s); }
#else
void EnsureWinsock() {}
void CloseSocket(socket_t s) { ::close(s); }
#endif

class TcpStream final : public IByteStream {
public:
    ~TcpStream() override { Close(); }

    bool Connect(const std::string& host, uint16_t port, int timeout_ms) override
    {
        EnsureWinsock();
        Close();

        addrinfo hints{};
        hints.ai_family = AF_INET;  // Akka classic remoting addresses here are IPv4
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;

        addrinfo* results = nullptr;
        const std::string service = std::to_string(port);
        if (getaddrinfo(host.c_str(), service.c_str(), &hints, &results) != 0 || results == nullptr) {
            return false;
        }

        bool ok = false;
        for (addrinfo* it = results; it != nullptr && !ok; it = it->ai_next) {
            socket_t s = socket(it->ai_family, it->ai_socktype, it->ai_protocol);
            if (s == kInvalidSocket) continue;

            if (ConnectWithTimeout(s, it->ai_addr, it->ai_addrlen, timeout_ms)) {
                int one = 1;
                setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&one),
                           sizeof(one));
                sock_ = s;
                ok = true;
            } else {
                CloseSocket(s);
            }
        }
        freeaddrinfo(results);
        return ok;
    }

    void Close() override
    {
        if (sock_ != kInvalidSocket) {
            CloseSocket(sock_);
            sock_ = kInvalidSocket;
        }
    }

    bool IsOpen() const override { return sock_ != kInvalidSocket; }

    int Read(uint8_t* buf, size_t len, int timeout_ms) override
    {
        if (!IsOpen()) return -1;
        if (!WaitReadable(timeout_ms)) return 0;

        const int n = static_cast<int>(recv(sock_, reinterpret_cast<char*>(buf),
                                            static_cast<int>(len), 0));
        if (n == 0) return -1;  // peer closed
        if (n < 0) return WouldBlock() ? 0 : -1;
        return n;
    }

    bool WriteAll(const uint8_t* buf, size_t len) override
    {
        if (!IsOpen()) return false;
        size_t sent = 0;
        while (sent < len) {
            const int n = static_cast<int>(send(sock_, reinterpret_cast<const char*>(buf + sent),
                                                static_cast<int>(len - sent), 0));
            if (n <= 0) {
                if (n < 0 && WouldBlock()) continue;
                return false;
            }
            sent += static_cast<size_t>(n);
        }
        return true;
    }

private:
    static bool WouldBlock()
    {
#if defined(_WIN32)
        return WSAGetLastError() == WSAEWOULDBLOCK;
#else
        return errno == EWOULDBLOCK || errno == EAGAIN || errno == EINTR;
#endif
    }

    bool WaitReadable(int timeout_ms)
    {
        fd_set rd;
        FD_ZERO(&rd);
        FD_SET(sock_, &rd);
        timeval tv{};
        tv.tv_sec = timeout_ms / 1000;
        tv.tv_usec = (timeout_ms % 1000) * 1000;
        const int r = select(static_cast<int>(sock_) + 1, &rd, nullptr, nullptr, &tv);
        return r > 0;
    }

    static bool ConnectWithTimeout(socket_t s, const sockaddr* addr, size_t addrlen, int timeout_ms)
    {
        SetNonBlocking(s, true);
        const int rc = connect(s, addr, static_cast<int>(addrlen));
        bool connected = rc == 0;

        if (!connected) {
            fd_set wr, ex;
            FD_ZERO(&wr);
            FD_SET(s, &wr);
            FD_ZERO(&ex);
            FD_SET(s, &ex);
            timeval tv{};
            tv.tv_sec = timeout_ms / 1000;
            tv.tv_usec = (timeout_ms % 1000) * 1000;
            if (select(static_cast<int>(s) + 1, nullptr, &wr, &ex, &tv) > 0 && FD_ISSET(s, &wr)) {
                int err = 0;
                socklen_t errlen = sizeof(err);
                if (getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&err), &errlen) == 0) {
                    connected = err == 0;
                }
            }
        }

        SetNonBlocking(s, false);
        return connected;
    }

    static void SetNonBlocking(socket_t s, bool on)
    {
#if defined(_WIN32)
        u_long mode = on ? 1 : 0;
        ioctlsocket(s, FIONBIO, &mode);
#else
        int flags = fcntl(s, F_GETFL, 0);
        if (flags < 0) return;
        fcntl(s, F_SETFL, on ? (flags | O_NONBLOCK) : (flags & ~O_NONBLOCK));
#endif
    }

    socket_t sock_ = kInvalidSocket;
};

}  // namespace

std::unique_ptr<IByteStream> MakeTcpStream() { return std::make_unique<TcpStream>(); }

}  // namespace akka
