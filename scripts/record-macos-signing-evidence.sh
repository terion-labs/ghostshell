#!/usr/bin/env bash
set -euo pipefail

app=""
notary_result=""
evidence=""

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/record-macos-signing-evidence.sh \
    --app <path/to/Asura.app> \
    --notary-result <notarytool-result.json> \
    --evidence <new/notarization.json>

Validates the final signed and stapled application, then records bounded
notarization, signing-certificate, code-signature, and Gatekeeper evidence.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --app)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            app="$2"
            shift 2
            ;;
        --notary-result)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            notary_result="$2"
            shift 2
            ;;
        --evidence)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            evidence="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            exit 64
            ;;
    esac
done

if [[ -z "${app}" || -z "${notary_result}" || -z "${evidence}" ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "macOS signing evidence requires a macOS host." >&2
    exit 1
fi
if [[ ! -d "${app}" || -L "${app}" || "$(basename "${app}")" != "Asura.app" ]]; then
    echo "--app must name a regular Asura.app directory." >&2
    exit 1
fi
if [[ ! -f "${notary_result}" || -L "${notary_result}" ]]; then
    echo "--notary-result must name a regular notarytool JSON result." >&2
    exit 1
fi

evidence_parent="$(dirname "${evidence}")"
if [[ ! -d "${evidence_parent}" || -L "${evidence_parent}" || -e "${evidence}" ]]; then
    echo "Signing evidence requires an existing regular parent and a new file." >&2
    exit 1
fi
evidence_parent="$(cd -- "${evidence_parent}" && pwd -P)"
evidence="${evidence_parent}/$(basename "${evidence}")"
app="$(cd -- "$(dirname "${app}")" && pwd -P)/Asura.app"
if [[ "${evidence}" == "${app}"/* ]]; then
    echo "Signing evidence must remain outside Asura.app." >&2
    exit 64
fi

notarization_id="$(/usr/bin/plutil -extract id raw "${notary_result}")"
notarization_status="$(/usr/bin/plutil -extract status raw "${notary_result}")"
if [[ ! "${notarization_id}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$ \
    || "${notarization_status}" != "Accepted" ]]; then
    echo "The notary result is not an accepted UUID-identified submission." >&2
    exit 1
fi

/usr/bin/codesign --verify --deep --strict --verbose=2 "${app}"
/usr/bin/xcrun stapler validate "${app}"
/usr/sbin/spctl --assess --type execute --verbose=2 "${app}"

working_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-signing-evidence.XXXXXX")"
evidence_staging="${evidence}.staging"
cleanup() {
    rm -rf -- "${working_directory}"
    rm -f -- "${evidence_staging}"
}
trap cleanup EXIT

codesign_details="${working_directory}/codesign-details.txt"
/usr/bin/codesign --display --verbose=4 "${app}" 2> "${codesign_details}"
team_identifier="$(/usr/bin/sed -n 's/^TeamIdentifier=//p' "${codesign_details}")"
if [[ ! "${team_identifier}" =~ ^[A-Za-z0-9]{1,32}$ ]]; then
    echo "The signed application has no bounded TeamIdentifier." >&2
    exit 1
fi
certificate_prefix="${working_directory}/certificate"
/usr/bin/codesign --display "--extract-certificates=${certificate_prefix}" "${app}"
if [[ ! -f "${certificate_prefix}0" ]]; then
    echo "The signing certificate could not be extracted." >&2
    exit 1
fi
certificate_sha256="$(/usr/bin/shasum -a 256 "${certificate_prefix}0" | /usr/bin/awk '{print $1}')"

/usr/bin/plutil -create xml1 "${evidence_staging}"
/usr/bin/plutil -insert schemaVersion -integer 1 "${evidence_staging}"
/usr/bin/plutil -insert format -string asura-macos-signing-evidence-v1 "${evidence_staging}"
/usr/bin/plutil -insert notarizationId -string "${notarization_id}" "${evidence_staging}"
/usr/bin/plutil -insert notarizationStatus -string "${notarization_status}" "${evidence_staging}"
/usr/bin/plutil -insert teamIdentifier -string "${team_identifier}" "${evidence_staging}"
/usr/bin/plutil -insert certificateSha256 -string "${certificate_sha256}" "${evidence_staging}"
/usr/bin/plutil -insert codeSignatureValid -bool true "${evidence_staging}"
/usr/bin/plutil -insert stapleValid -bool true "${evidence_staging}"
/usr/bin/plutil -insert gatekeeperAccepted -bool true "${evidence_staging}"
/usr/bin/plutil -convert json "${evidence_staging}"
/bin/mv "${evidence_staging}" "${evidence}"
