#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/../.." && pwd -P)"
dotnet="${repository_dir}/.dotnet/dotnet"
tool="${repository_dir}/tools/Asura.SecurityCampaign/Asura.SecurityCampaign.csproj"
registry="${repository_dir}/scripts/acceptance/security-campaign/cases.v1.json"
receipt_schema="${repository_dir}/scripts/acceptance/security-campaign/receipt.schema.json"
output=""

if [[ "${1:-}" != "--source-only" || "${2:-}" != "--output" || -z "${3:-}" || $# -ne 3 ]]; then
    echo "Usage: ./scripts/acceptance/run-security-campaign.sh --source-only --output <directory>" >&2
    exit 64
fi
output="$3"

working_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-security-campaign.XXXXXX")"
cleanup() {
    rm -rf -- "${working_directory}"
}
trap cleanup EXIT

"${dotnet}" run --project "${tool}" --configuration Release -- \
    validate-definition \
    --repository "${repository_dir}" \
    --registry "${registry}" \
    --receipt-schema "${receipt_schema}"

while IFS= read -r project; do
    result_name="$(basename "${project}" .csproj)"
    "${dotnet}" test "${repository_dir}/${project}" \
        --configuration Release \
        --logger "trx;LogFileName=${result_name}.trx" \
        --results-directory "${working_directory}/results"
done < <(
    "${dotnet}" run --project "${tool}" --configuration Release -- \
        list-test-projects \
        --repository "${repository_dir}" \
        --registry "${registry}"
)

"${dotnet}" run --project "${tool}" --configuration Release -- \
    assemble-source-evidence \
    --repository "${repository_dir}" \
    --registry "${registry}" \
    --receipt-schema "${receipt_schema}" \
    --test-results "${working_directory}/results" \
    --output "${output}"

"${dotnet}" run --project "${tool}" --configuration Release --no-build -- \
    validate-evidence \
    --repository "${repository_dir}" \
    --registry "${registry}" \
    --receipt-schema "${receipt_schema}" \
    --test-results "${working_directory}/results" \
    --evidence "${output}"
