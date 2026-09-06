// This translation unit builds Core's custom TUN factory. Unlike the platform
// factory, it does not add macOS utun's four-byte address-family prefix.
#include "packet_client.hpp"
#include <openvpn/tun/extern/config.hpp>
#include <openvpn/tun/builder/client.hpp>

openvpn::TunClientFactory *ghostshell::PacketClient::new_tun_factory(
    const openvpn::ExternalTun::Config &settings, const openvpn::OptionList &) {
    auto factory = openvpn::TunBuilderClient::ClientConfig::new_obj();
    factory->builder = this;
    factory->tun_prop = settings.tun_prop;
    factory->frame = settings.frame;
    factory->stats = settings.stats;
    factory->tun_prefix = false;
    factory->retain_sd = false;
    tun_factory_ = factory;
    return tun_factory_.get();
}
