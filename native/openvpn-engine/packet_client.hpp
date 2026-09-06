#pragma once

// Core header-only packet adapters use these logging hooks. Private protocol
// diagnostics must never escape the engine's bounded readiness protocol.
#define OPENVPN_LOG(...) do {} while (false)
#define OPENVPN_LOG_NTNL(...) do {} while (false)
#define OPENVPN_LOG_STRING(...) do {} while (false)

#include <openvpn/io/io.hpp>
#include <client/ovpncli.hpp>
#include <openvpn/tun/client/tunbase.hpp>
#include <arpa/inet.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <unistd.h>
#include <algorithm>
#include <csignal>
#include <ostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace ghostshell {

// The builder only describes routes to the parent. Core reads and writes raw IP
// datagrams on the supplied Unix socket; no OS TUN, route, or resolver is changed.
class PacketClient final : public openvpn::ClientAPI::OpenVPNClient {
public:
    PacketClient(int packet_fd, std::ostream &readiness, volatile sig_atomic_t &interrupted)
        : packet_fd_(packet_fd), readiness_(readiness), interrupted_(interrupted) {
        int type = 0;
        socklen_t size = sizeof(type);
        sockaddr_storage peer{};
        socklen_t peer_size = sizeof(peer);
        if (packet_fd < 3 || getsockopt(packet_fd, SOL_SOCKET, SO_TYPE, &type, &size) != 0
            || type != SOCK_DGRAM || getpeername(packet_fd, reinterpret_cast<sockaddr *>(&peer), &peer_size) != 0
            || peer.ss_family != AF_UNIX)
            throw std::invalid_argument("packet_socket_invalid");
    }

    bool tun_builder_new() override {
        // A new negotiation needs a new parent stack. Never quietly keep using
        // old addresses after Core reconnects or the server changes its routes.
        if (connected_) return false;
        ipv4_.clear(); ipv6_.clear(); dns_.clear(); routes4_.clear(); routes6_.clear(); excludes_.clear();
        redirect4_ = redirect6_ = false;
        mtu_ = 1500;
        return true;
    }
    bool tun_builder_set_layer(int layer) override { return layer == 3; }
    bool tun_builder_set_remote_address(const std::string &, bool) override { return true; }
    bool tun_builder_set_session_name(const std::string &) override { return true; }
    bool tun_builder_add_address(const std::string &address, int prefix, const std::string &, bool ipv6, bool) override {
        if (!valid_prefix(address, prefix, ipv6)) return false;
        auto &slot = ipv6 ? ipv6_ : ipv4_;
        if (!slot.empty()) return false;
        slot = address + "/" + std::to_string(prefix);
        return true;
    }
    bool tun_builder_set_mtu(int mtu) override {
        if (mtu < 576 || mtu > 65535) return false;
        mtu_ = mtu;
        return true;
    }
    bool tun_builder_reroute_gw(bool ipv4, bool ipv6, unsigned int) override {
        redirect4_ = ipv4; redirect6_ = ipv6;
        return true;
    }
    bool tun_builder_add_route(const std::string &address, int prefix, int, bool ipv6) override {
        return add_route(ipv6 ? routes6_ : routes4_, address, prefix, ipv6);
    }
    bool tun_builder_exclude_route(const std::string &address, int prefix, int, bool ipv6) override {
        return add_route(excludes_, address, prefix, ipv6);
    }
    bool tun_builder_set_dns_options(const openvpn::DnsOptions &options) override {
        dns_.clear();
        for (const auto &[priority, server] : options.servers) {
            (void)priority;
            // The parent contract carries plain DNS endpoints, not DoH/DoT or
            // DNSSEC policy. Reject policies it cannot faithfully implement.
            if (server.transport != openvpn::DnsServer::Transport::Unset
                && server.transport != openvpn::DnsServer::Transport::Plain) return false;
            if (server.dnssec == openvpn::DnsServer::Security::Yes) return false;
            for (const auto &endpoint : server.addresses) {
                if (endpoint.port != 0 && endpoint.port != 53) return false;
                if (!valid_address(endpoint.address, false) && !valid_address(endpoint.address, true)) return false;
                if (std::find(dns_.begin(), dns_.end(), endpoint.address) == dns_.end()) dns_.push_back(endpoint.address);
                if (dns_.size() > 4) return false;
            }
        }
        return true;
    }
    int tun_builder_establish() override {
        if ((ipv4_.empty() && ipv6_.empty()) || (!ipv6_.empty() && mtu_ < 1280)) return -1;
        // Core owns this duplicate and may close it during teardown. The parent
        // retains the opposite endpoint, not a duplicate of this endpoint.
        return fcntl(packet_fd_, F_DUPFD_CLOEXEC, 3);
    }
    bool tun_builder_persist() override { return false; }
    openvpn::TunClientFactory *new_tun_factory(const openvpn::ExternalTun::Config &, const openvpn::OptionList &) override;
    bool socket_protect(openvpn_io::detail::socket_type, std::string, bool) override { return true; }
    bool pause_on_connection_timeout() override { return false; }
    void clock_tick() override { if (interrupted_) stop(); }
    void log(const openvpn::ClientAPI::LogInfo &) override {
        // Core logs contain profile material, peer addresses, and session tokens.
        // Only the allowlisted terminal categories below leave this process.
    }
    void acc_event(const openvpn::ClientAPI::AppCustomControlMessageEvent &) override {}
    void external_pki_cert_request(openvpn::ClientAPI::ExternalPKICertRequest &request) override { request.error = true; }
    void external_pki_sign_request(openvpn::ClientAPI::ExternalPKISignRequest &request) override { request.error = true; }
    void event(const openvpn::ClientAPI::Event &event) override {
        if (event.name == "CONNECTED") {
            if (connected_) { failure_ = "session_reconfigured"; stop(); return; }
            connected_ = true;
            readiness_ << "READY v1 {\"ipv4_address\":" << quoted_or_null(ipv4_)
                       << ",\"ipv6_address\":" << quoted_or_null(ipv6_)
                       << ",\"mtu\":" << mtu_ << ",\"dns_servers\":" << array(dns_)
                       << ",\"ipv4_routes\":" << (redirect4_ ? "null" : array(routes4_))
                       << ",\"ipv6_routes\":" << (redirect6_ ? "null" : array(routes6_))
                       << ",\"exclude_routes\":" << array(excludes_) << "}\n" << std::flush;
            if (!readiness_) { failure_ = "readiness_write_failed"; stop(); }
        } else if (event.fatal || (connected_ && (event.name == "RECONNECTING" || event.name == "DISCONNECTED"))) {
            failure_ = event.name == "AUTH_FAILED" ? "authentication_failed" : "session_stopped";
            stop();
        }
    }
    const std::string &failure() const { return failure_; }
    bool connected() const { return connected_; }

private:
    static bool valid_address(const std::string &address, bool ipv6) {
        unsigned char parsed[16]{};
        return address.find('\0') == std::string::npos
            && inet_pton(ipv6 ? AF_INET6 : AF_INET, address.c_str(), parsed) == 1;
    }
    static bool valid_prefix(const std::string &address, int prefix, bool ipv6) {
        return prefix >= 0 && prefix <= (ipv6 ? 128 : 32) && valid_address(address, ipv6);
    }
    static bool add_route(std::vector<std::string> &routes, const std::string &address, int prefix, bool ipv6) {
        if (routes.size() >= 4096 || !valid_prefix(address, prefix, ipv6)) return false;
        routes.push_back(address + "/" + std::to_string(prefix));
        return true;
    }
    // Only inet_pton-validated addresses and integer prefixes reach the JSON
    // writer. No profile text, display names, or credentials are serialized.
    static std::string quoted_or_null(const std::string &value) { return value.empty() ? "null" : "\"" + value + "\""; }
    static std::string array(const std::vector<std::string> &values) {
        std::string result = "[";
        for (const auto &value : values) { if (result.size() > 1) result += ','; result += quoted_or_null(value); }
        return result + ']';
    }
    int packet_fd_;
    std::ostream &readiness_;
    volatile sig_atomic_t &interrupted_;
    std::string ipv4_, ipv6_, failure_;
    std::vector<std::string> dns_, routes4_, routes6_, excludes_;
    int mtu_ = 1500;
    bool connected_ = false, redirect4_ = false, redirect6_ = false;
    openvpn::TunClientFactory::Ptr tun_factory_;
};
}
