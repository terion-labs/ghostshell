#include "packet_client.hpp"
#include <openvpn/tun/extern/config.hpp>
#include <openvpn/tun/builder/client.hpp>
#include <cerrno>
#include <iostream>
#include <sstream>

namespace {
void require(bool value, const char *message) {
    if (!value) throw std::runtime_error(message);
}
struct SocketPair {
    int descriptors[2];
    explicit SocketPair(int type = SOCK_DGRAM) {
        require(socketpair(AF_UNIX, type, 0, descriptors) == 0, "create pair");
    }
    ~SocketPair() { close(descriptors[0]); close(descriptors[1]); }
};
}

int main() {
    try {
        volatile sig_atomic_t interrupted = 0;
        std::ostringstream readiness;
        SocketPair pair;
        asura::PacketClient client(pair.descriptors[0], readiness, interrupted);
        openvpn::ExternalTun::Config settings;
        openvpn::OptionList options;
        auto *factory = dynamic_cast<openvpn::TunBuilderClient::ClientConfig *>(client.new_tun_factory(settings, options));
        require(factory && !factory->tun_prefix, "actual Core factory never adds the macOS utun prefix");
        require(client.tun_builder_new(), "begin builder");
        require(!client.tun_builder_set_layer(2), "reject TAP");
        require(client.tun_builder_set_layer(3), "accept IP");
        require(!client.tun_builder_add_address("bad\"address", 24, "", false, false), "reject injection");
        require(client.tun_builder_add_address("10.8.0.2", 24, "", false, false), "IPv4");
        require(!client.tun_builder_add_address("10.8.0.3", 24, "", false, false), "duplicate family");
        require(client.tun_builder_add_address("fd00::2", 64, "", true, false), "IPv6");
        require(client.tun_builder_set_mtu(1500), "MTU");
        require(!client.tun_builder_set_mtu(65536), "invalid MTU");
        require(client.tun_builder_reroute_gw(true, false, 0), "redirect IPv4");
        require(client.tun_builder_add_route("fd12::", 48, 0, true), "split IPv6");
        require(client.tun_builder_exclude_route("192.0.2.0", 24, 0, false), "exclude");
        openvpn::DnsOptions dns;
        dns.servers[0].addresses.emplace_back("10.8.0.1");
        dns.servers[0].addresses.emplace_back("10.8.0.1");
        require(client.tun_builder_set_dns_options(dns), "DNS");
        const int core_fd = client.tun_builder_establish();
        require(core_fd >= 3 && core_fd != pair.descriptors[0], "Core receives duplicate descriptor");
        require((fcntl(core_fd, F_GETFD) & FD_CLOEXEC) != 0, "duplicate is close-on-exec");
        const unsigned char packet[] = {0x45, 0, 0, 4};
        require(send(pair.descriptors[1], packet, sizeof(packet), 0) == sizeof(packet), "send raw datagram");
        unsigned char received[32]{};
        require(read(core_fd, received, sizeof(received)) == sizeof(packet), "raw datagram has no TUN prefix");
        require(received[0] == 0x45, "raw IPv4 prefix");
        close(core_fd);
        require(fcntl(pair.descriptors[0], F_GETFD) >= 0, "Core close preserves original descriptor");
        require(readiness.str().empty(), "do not announce before CONNECTED");
        openvpn::ClientAPI::Event connected;
        connected.name = "CONNECTED";
        client.event(connected);
        require(readiness.str() == "READY v1 {\"ipv4_address\":\"10.8.0.2/24\",\"ipv6_address\":\"fd00::2/64\",\"mtu\":1500,\"dns_servers\":[\"10.8.0.1\"],\"ipv4_routes\":null,\"ipv6_routes\":[\"fd12::/48\"],\"exclude_routes\":[\"192.0.2.0/24\"]}\n", "readiness contract");
        require(!client.tun_builder_new(), "reject renegotiation of attached stack");
        SocketPair stream(SOCK_STREAM);
        bool rejected = false;
        try { asura::PacketClient wrong(stream.descriptors[0], readiness, interrupted); }
        catch (const std::invalid_argument &) { rejected = true; }
        require(rejected, "reject non-datagram socket");
        std::ostringstream empty_readiness;
        asura::PacketClient empty(pair.descriptors[0], empty_readiness, interrupted);
        require(empty.tun_builder_new(), "begin empty builder");
        require(empty.tun_builder_establish() == -1, "require negotiated address");
        require(empty.tun_builder_add_address("fd00::2", 64, "", true, false), "IPv6 address");
        require(empty.tun_builder_set_mtu(576), "accept minimum IPv4 MTU before family validation");
        require(empty.tun_builder_establish() == -1, "reject IPv6 MTU below1280");
        std::cout << "packet-client contract tests passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
