#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_path="${1:-$repo_root/artifacts/paperbatch-demo-ui.png}"
mkdir -p "$(dirname "$output_path")"
output_path="$(realpath "$output_path")"

command -v xvfb-run >/dev/null || { echo "xvfb-run is required (install xvfb)." >&2; exit 1; }
command -v xdotool >/dev/null || { echo "xdotool is required." >&2; exit 1; }
command -v import >/dev/null || { echo "ImageMagick import is required (install imagemagick)." >&2; exit 1; }

cd "$repo_root"
xvfb-run -a -s '-screen 0 1920x1200x24' bash -c '
set -euo pipefail
output_path="$1"
app_dll="$2"
log_path="$(mktemp)"
if [[ ! -f "$app_dll" ]]; then
  echo "Build src/PaperBatch.Desktop/PaperBatch.Desktop.csproj before capturing." >&2
  exit 1
fi
dotnet "$app_dll" --demo >"$log_path" 2>&1 &
app_pid=$!
cleanup() {
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  rm -f "$log_path"
}
trap cleanup EXIT

window_id=""
for attempt in $(seq 1 100); do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo "PaperBatch demo exited before opening its window." >&2
    exit 1
  fi
  window_id="$(xdotool search --onlyvisible --name PaperBatch 2>/dev/null | head -n 1 || true)"
  if [[ -n "$window_id" ]]; then break; fi
  sleep 0.1
done

if [[ -z "$window_id" ]]; then
  cat "$log_path" >&2
  echo "Timed out waiting for the PaperBatch demo window." >&2
  exit 1
fi

xdotool windowsize "$window_id" 1440 1120
sleep 0.5
import -window "$window_id" "$output_path"
identify -format "Captured PaperBatch window: %wx%h PNG\n" "$output_path"
' _ "$output_path" "$repo_root/src/PaperBatch.Desktop/bin/Debug/net10.0/PaperBatch.Desktop.dll"
