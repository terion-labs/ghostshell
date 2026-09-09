cmake_minimum_required(VERSION 3.20)
project(AsuraOpenVpnEngine LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
foreach(required OPENVPN_SOURCE ASIO_SOURCE OPENSSL_SOURCE LZ4_SOURCE)
    if(NOT IS_DIRECTORY "${${required}}")
        message(FATAL_ERROR "${required} must point to the verified source dependency")
    endif()
endforeach()

# Core is built from the verified release unchanged. No platform TUN factory,
# DCO, shell commands, or external PKI provider is enabled in this executable.
add_library(openvpn_core STATIC
    "${OPENVPN_SOURCE}/client/ovpncli.cpp"
    "${OPENVPN_SOURCE}/openvpn/crypto/data_epoch.cpp")
target_compile_definitions(openvpn_core PUBLIC
    USE_ASIO ASIO_STANDALONE USE_OPENSSL OPENVPN_EXTERNAL_TUN_FACTORY HAVE_LZ4)
target_include_directories(openvpn_core SYSTEM PUBLIC
    "${OPENVPN_SOURCE}" "${ASIO_SOURCE}/asio/include"
    "${OPENSSL_SOURCE}/include" "${LZ4_SOURCE}/lib")
target_link_libraries(openvpn_core PUBLIC
    "${OPENSSL_SOURCE}/libssl.a" "${OPENSSL_SOURCE}/libcrypto.a"
    "${LZ4_SOURCE}/lib/liblz4.a")
if(APPLE)
    target_link_libraries(openvpn_core PUBLIC
        "-framework CoreFoundation" "-framework IOKit" "-framework SystemConfiguration")
else()
    target_link_libraries(openvpn_core PUBLIC pthread dl)
endif()

add_executable(asura-openvpn-engine main.cpp packet_client.cpp)
target_link_libraries(asura-openvpn-engine PRIVATE openvpn_core)
target_compile_options(asura-openvpn-engine PRIVATE -Wall -Wextra -Werror)

include(CTest)
if(BUILD_TESTING)
    add_executable(packet-client-tests packet_client_tests.cpp packet_client.cpp)
    target_link_libraries(packet-client-tests PRIVATE openvpn_core)
    target_compile_options(packet-client-tests PRIVATE -Wall -Wextra -Werror)
    add_test(NAME packet-client-contract COMMAND packet-client-tests)
    add_test(NAME reject-invalid-arguments COMMAND asura-openvpn-engine --tun-fd invalid)
    set_tests_properties(reject-invalid-arguments PROPERTIES WILL_FAIL TRUE)
endif()
