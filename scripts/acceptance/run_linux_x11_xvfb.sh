#!/usr/bin/env bash
set -euo pipefail

if (($# != 3)); then
  echo "usage: $0 SOURCE_ROOT OUTPUT_DIRECTORY IMAGE_REFERENCE" >&2
  exit 64
fi

source_root=$1
output_directory=$2
image_reference=$3
package_directory=$(mktemp -d /work/asura-package.XXXXXX)
runtime_evidence=$(mktemp -d /work/asura-runtime-evidence.XXXXXX)
xvfb_log="$output_directory/xvfb.log"
openbox_log="$output_directory/openbox.log"

mkdir -p "$output_directory" "$runtime_evidence" /work/home /work/runtime
chmod 700 /work/runtime

dotnet --info >"$output_directory/dotnet-info.txt"
cat /etc/os-release >"$output_directory/os-release.txt"
uname -a >"$output_directory/uname.txt"
{
  fc-match 'JetBrains Mono'
  fc-match 'Noto Sans CJK JP'
  fc-match 'Noto Color Emoji'
} >"$output_directory/fontconfig-matches.txt"

find "$source_root" -type f \
  ! -path '*/bin/*' \
  ! -path '*/obj/*' \
  ! -path '*/docs/acceptance/*' \
  -print0 \
  | sort -z \
  | xargs -0 sha256sum \
  | sha256sum \
  | awk '{print $1}' >"$output_directory/source-snapshot.sha256"
source_digest=$(tr -d '\n' <"$output_directory/source-snapshot.sha256")

set +e
dotnet restore "$source_root/src/Asura.Desktop/Asura.Desktop.csproj" \
  --runtime linux-arm64 \
  --locked-mode \
  --disable-build-servers \
  >"$output_directory/restore.log" 2>&1
restore_status=$?
if ((restore_status != 0)); then
  set -e
  echo "linux-arm64 locked restore failed; see $output_directory/restore.log" >&2
  exit "$restore_status"
fi
dotnet publish "$source_root/src/Asura.Desktop/Asura.Desktop.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  --no-restore \
  -p:RestoreLockedMode=true \
  --output "$package_directory" \
  --disable-build-servers \
  >"$output_directory/publish.log" 2>&1
publish_status=$?
set -e
if ((publish_status != 0)); then
  echo "linux-arm64 self-contained publish failed; see $output_directory/publish.log" >&2
  exit "$publish_status"
fi

file "$package_directory/Asura" >"$output_directory/package-file.txt"
ldd "$package_directory/Asura" >"$output_directory/package-ldd.txt"
find "$package_directory" -maxdepth 1 -type f -print0 \
  | sort -z \
  | xargs -0 sha256sum >"$output_directory/package-files.sha256"

Xvfb :99 -screen 0 1440x900x24 -nolisten tcp >"$xvfb_log" 2>&1 &
xvfb_pid=$!
openbox_pid=''
cleanup() {
  if [[ -n "$openbox_pid" ]]; then
    kill "$openbox_pid" 2>/dev/null || true
    wait "$openbox_pid" 2>/dev/null || true
  fi
  kill "$xvfb_pid" 2>/dev/null || true
  wait "$xvfb_pid" 2>/dev/null || true
}
trap cleanup EXIT

export DISPLAY=:99
export XDG_SESSION_TYPE=x11
for _ in {1..100}; do
  if xdpyinfo >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
xdpyinfo >"$output_directory/xdpyinfo.txt"
xmodmap -pm >"$output_directory/xmodmap-modifiers.txt"
openbox --sm-disable >"$openbox_log" 2>&1 &
openbox_pid=$!
for _ in {1..100}; do
  if xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | grep -q 'window id'; then
    break
  fi
  sleep 0.1
done
if ! kill -0 "$openbox_pid" 2>/dev/null \
  || ! xprop -root _NET_SUPPORTING_WM_CHECK >"$output_directory/window-manager.txt" 2>&1 \
  || ! grep -q 'window id' "$output_directory/window-manager.txt"; then
  echo 'Openbox did not publish the X11 window-manager boundary.' >&2
  exit 70
fi

set +e
dbus-run-session -- python3 "$source_root/scripts/acceptance/linux_x11_xvfb_acceptance.py" \
  --package "$package_directory" \
  --source-root "$source_root" \
  --output "$output_directory" \
  --runtime-evidence "$runtime_evidence" \
  --source-digest "$source_digest" \
  --image-reference "$image_reference"
acceptance_status=$?
set -e

exit "$acceptance_status"
