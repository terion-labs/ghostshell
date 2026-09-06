#include "packet_client.hpp"
#include <openssl/crypto.h>
#include <sys/stat.h>
#include <charconv>
#include <iostream>

namespace {
volatile sig_atomic_t interrupted = 0;
void interrupt(int) { interrupted = 1; }

std::string read_profile(const std::string &path) {
    constexpr off_t maximum = 2 * 1024 * 1024;
    int fd = open(path.c_str(), O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) throw std::runtime_error("profile_unavailable");
    struct stat info{};
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || info.st_size <= 0 || info.st_size > maximum) {
        close(fd); throw std::runtime_error("profile_invalid");
    }
    std::string content(static_cast<size_t>(info.st_size), '\0');
    size_t offset = 0;
    while (offset < content.size()) {
        const auto count = read(fd, content.data() + offset, content.size() - offset);
        if (count <= 0) { close(fd); throw std::runtime_error("profile_read_failed"); }
        offset += static_cast<size_t>(count);
    }
    close(fd);
    return content;
}

std::string read_password() {
    std::string password;
    char character;
    while (std::cin.get(character) && character != '\n') {
        if (password.size() >= 8192 || character == '\0' || character == '\r')
            throw std::runtime_error("credential_input_invalid");
        password.push_back(character);
    }
    if (std::cin.peek() != std::char_traits<char>::eof()) throw std::runtime_error("credential_input_invalid");
    return password;
}
}

int main(int argc, char **argv) {
    std::string profile, username;
    int packet_fd = -1;
    bool validate = false;
    for (int index = 1; index < argc; ++index) {
        const std::string argument = argv[index];
        if (argument == "--validate") { validate = true; continue; }
        if (index + 1 >= argc) { std::cerr << "ERROR arguments_invalid\n"; return 64; }
        const std::string value = argv[++index];
        if (argument == "--config" && profile.empty()) profile = value;
        else if (argument == "--username" && username.empty()) username = value;
        else if (argument == "--tun-fd" && packet_fd == -1) {
            const auto parsed = std::from_chars(value.data(), value.data() + value.size(), packet_fd);
            if (parsed.ec != std::errc{} || parsed.ptr != value.data() + value.size()) packet_fd = -1;
        } else { std::cerr << "ERROR arguments_invalid\n"; return 64; }
    }
    if (profile.empty() || (!validate && packet_fd < 3) || username.size() > 4096) {
        std::cerr << "ERROR arguments_invalid\n"; return 64;
    }
    try {
        openvpn::ClientAPI::Config config;
        config.content = read_profile(profile);
        config.connTimeout = 60;
        config.clockTickMS = 100;
        config.tunPersist = false;
        config.compressionMode = "no";
        if (validate) {
            openvpn::ClientAPI::OpenVPNClientHelper helper;
            const auto result = helper.eval_config(config);
            OPENSSL_cleanse(config.content.data(), config.content.size());
            if (result.error) { std::cerr << "ERROR profile_invalid\n"; return 65; }
            std::cout << "VALID v1\n";
            return 0;
        }
        ghostshell::PacketClient client(packet_fd, std::cout, interrupted);
        const auto evaluation = client.eval_config(config);
        OPENSSL_cleanse(config.content.data(), config.content.size());
        if (evaluation.error) { std::cerr << "ERROR profile_invalid\n"; return 65; }
        openvpn::ClientAPI::ProvideCreds credentials;
        credentials.username = username;
        credentials.password = read_password();
        const auto provided = client.provide_creds(credentials);
        OPENSSL_cleanse(credentials.password.data(), credentials.password.size());
        if (provided.error) { std::cerr << "ERROR credential_invalid\n"; return 65; }
        std::signal(SIGTERM, interrupt); std::signal(SIGINT, interrupt); std::signal(SIGHUP, interrupt);
        std::signal(SIGPIPE, SIG_IGN);
        const auto status = client.connect();
        if (interrupted) return 0;
        if (status.error || !client.failure().empty() || !client.connected()) {
            std::cerr << "ERROR " << (client.failure().empty() ? "connection_failed" : client.failure()) << '\n';
            return 1;
        }
        std::cerr << "ERROR session_stopped\n";
        return 1;
    } catch (const std::exception &) {
        // Exception details may contain inline key/cert material. Never print them.
        std::cerr << "ERROR engine_failed\n";
        return 1;
    }
}
