#!/bin/sh
set -eu

case " $* " in
    *" --atleast-pkgconfig-version "*) exit 0 ;;
esac

package=
for argument in "$@"; do
    case "${argument}" in
        libxml-2.0|openssl|zlib) package="${argument}" ;;
    esac
done
if [ -z "${package}" ]; then
    exit 1
fi

case " $* " in
    *" --exists "*|*" --atleast-version="*) exit 0 ;;
    *" --modversion "*)
        case "${package}" in
            libxml-2.0) printf '%s\n' 2.9.0 ;;
            openssl) printf '%s\n' 3.6.4 ;;
            zlib) printf '%s\n' 1.2.12 ;;
        esac
        ;;
    *" --cflags "*)
        case "${package}" in
            libxml-2.0) /usr/bin/xml2-config --cflags ;;
            openssl) printf '%s\n' "-I${OPENSSL_BUILD_ROOT:?}/include" ;;
            zlib) ;;
        esac
        ;;
    *" --libs "*)
        case "${package}" in
            libxml-2.0) /usr/bin/xml2-config --libs ;;
            openssl)
                printf '%s\n' \
                    "${OPENSSL_BUILD_ROOT:?}/libssl.a ${OPENSSL_BUILD_ROOT}/libcrypto.a -lz -pthread"
                ;;
            zlib) printf '%s\n' -lz ;;
        esac
        ;;
    *) exit 1 ;;
esac
