#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
run_id=$(date -u +%Y%m%dT%H%M%SZ)
output_directory=${1:-"$repository_root/artifacts/platform-acceptance/$run_id-$$-$RANDOM-linux-arm64-xvfb"}
image=mcr.microsoft.com/dotnet/sdk:10.0
container_name="asura-linux-xvfb-$RANDOM-$$"
staging_directory=$(mktemp -d)
image_reference=$image
source_digest=unavailable
failure_stage=coordinator-initialization

output_parent=$(dirname -- "$output_directory")
mkdir -p "$output_parent"
output_parent=$(cd -- "$output_parent" && pwd)
output_directory="$output_parent/$(basename -- "$output_directory")"
if ! mkdir "$output_directory"; then
  echo "Refusing to merge acceptance evidence into existing path: $output_directory" >&2
  rm -rf -- "$staging_directory"
  exit 73
fi

cleanup() {
  docker rm --force "$container_name" >/dev/null 2>&1 || true
  rm -rf -- "$staging_directory"
}

finalize() {
  status=$?
  trap - EXIT
  set +e
  if [[ -f "$staging_directory/source-snapshot.sha256" ]]; then
    source_digest=$(tr -d '\n' <"$staging_directory/source-snapshot.sha256")
  fi
  if [[ ! -f "$staging_directory/evidence.json" ]]; then
    python3 "$repository_root/scripts/acceptance/linux_x11_xvfb_acceptance.py" \
      --output "$staging_directory" \
      --source-digest "$source_digest" \
      --image-reference "$image_reference" \
      --infrastructure-failure-stage "$failure_stage" \
      --infrastructure-failure-exit-code "$status" >/dev/null 2>&1
  fi

  copy_status=0
  find "$staging_directory" -mindepth 1 -maxdepth 1 \
    ! -name evidence.json \
    -exec cp -a {} "$output_directory/" \; || copy_status=$?
  if [[ -f "$staging_directory/evidence.json" && $copy_status -eq 0 ]]; then
    receipt_temporary="$output_directory/.evidence.json.$$.tmp"
    cp "$staging_directory/evidence.json" "$receipt_temporary" \
      && mv "$receipt_temporary" "$output_directory/evidence.json" \
      || copy_status=$?
  fi
  cleanup
  echo "Linux Xvfb acceptance evidence: $output_directory"
  if ((copy_status != 0)); then
    exit "$copy_status"
  fi
  exit "$status"
}
trap finalize EXIT

failure_stage=docker-image-pull
docker pull "$image" >/dev/null
failure_stage=docker-daemon-inspection
daemon_platform=$(docker info --format '{{.OSType}}/{{.Architecture}}')
if [[ "$daemon_platform" != "linux/aarch64" && "$daemon_platform" != "linux/arm64" ]]; then
  echo "This acceptance run requires a native Linux arm64 Docker daemon; found $daemon_platform." >&2
  exit 69
fi
failure_stage=docker-image-fingerprint
image_reference=$(docker image inspect \
  --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' \
  "$image")
docker version --format '{{json .Server}}' >"$staging_directory/docker-server.json"
printf '%s\n' "$daemon_platform" >"$staging_directory/docker-daemon-platform.txt"

failure_stage=container-startup
docker run \
  --detach \
  --name "$container_name" \
  --platform linux/arm64 \
  --volume "$repository_root:/repo:ro" \
  --volume "$staging_directory:/host-output" \
  "$image" \
  sleep infinity >/dev/null

failure_stage=container-dependency-install
docker exec "$container_name" bash -lc '
  apt-get update >/dev/null
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    dbus-x11 file fontconfig fonts-dejavu-core fonts-jetbrains-mono \
    fonts-noto-cjk fonts-noto-color-emoji fonts-noto-core imagemagick jq less \
    libdbus-1-3 libdrm2 libfontconfig1 \
    libfreetype6 libgbm1 libgl1 libice6 libsm6 libx11-6 libxcomposite1 \
    libxcursor1 libxext6 libxfixes3 libxi6 libxinerama1 libxrandr2 \
    libxrender1 ncurses-bin openbox procps python3 util-linux x11-utils \
    x11-xserver-utils xclip xdotool xterm xvfb >/dev/null
'

failure_stage=source-copy
docker exec "$container_name" bash -lc '
  mkdir -p /work/source
  tar -C /repo \
    --exclude=.dotnet \
    --exclude=.codex-audit \
    --exclude=artifacts \
    --exclude=docs/acceptance \
    --exclude="*/bin" \
    --exclude="*/bin/*" \
    --exclude="*/obj" \
    --exclude="*/obj/*" \
    -cf - . | tar -C /work/source -xf -
'

set +e
failure_stage=container-acceptance
docker exec "$container_name" \
  bash /work/source/scripts/acceptance/run_linux_x11_xvfb.sh \
  /work/source \
  /host-output \
  "$image_reference"
acceptance_status=$?
set -e

exit "$acceptance_status"
