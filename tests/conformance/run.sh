#!/usr/bin/env bash
# End-to-end OpenSubsonic conformance run: build the plugin, start a throwaway Jellyfin (podman) with it
# and a generated test library, and check the Subsonic API against the OpenSubsonic spec.
#
# usage: tests/conformance/run.sh [--base-url /jellyfin] [--image IMAGE] [--keep]
#   --base-url  serve Jellyfin under a path prefix, as behind a path-based reverse proxy
#   --image     Jellyfin container image (default: the 12.1 image the suite was written against)
#   --keep      leave the Jellyfin container running afterwards (http://127.0.0.1:18096)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
export WORK="${WORK:-$HERE/.work}"
SPEC_REPO=https://github.com/opensubsonic/open-subsonic-api.git
SPEC_REF=bed1688  # the spec revision these checks were written against

KEEP=
while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) export BASEURL="$2"; shift 2 ;;
    --image) export IMAGE="$2"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
for tool in podman ffmpeg jq curl uv dotnet git; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 1; }
done
mkdir -p "$WORK"

echo "== building the plugin"
dotnet publish -c Release "$ROOT/Jellyfin.Plugin.Subsonic" --nologo -v quiet
PLUGIN="$ROOT/Jellyfin.Plugin.Subsonic/bin/Release/net10.0/publish/Jellyfin.Plugin.Subsonic.dll"

echo "== OpenSubsonic spec @ $SPEC_REF"
if [ ! -d "$WORK/open-subsonic-api/.git" ]; then
  git clone -q "$SPEC_REPO" "$WORK/open-subsonic-api"
fi
git -C "$WORK/open-subsonic-api" fetch -q origin
git -C "$WORK/open-subsonic-api" checkout -q "$SPEC_REF"

echo "== test library"
bash "$HERE/make-media.sh" "$WORK/media" >/dev/null

echo "== starting Jellyfin"
bash "$HERE/up.sh" "$PLUGIN" "$ROOT/meta.json"

echo "== checks"
status=0
uv run -q "$HERE/conformance.py" "$WORK/creds.env" "$WORK/open-subsonic-api/openapi" "$WORK/report.json" || status=$?
echo "full report: $WORK/report.json"

[ -n "$KEEP" ] || podman rm -f subfin-jf >/dev/null
exit $status
