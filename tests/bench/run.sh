#!/usr/bin/env bash
# Performance check: build the plugin, start a throwaway Jellyfin (podman) with a large synthetic library,
# and time the Subsonic endpoints clients call most. Fails when one is over its budget (bench.py).
#
# usage: tests/bench/run.sh [--keep]    (ARTISTS=1500 and GENRES=150 set the library's size)
#   --keep  leave the Jellyfin container running afterwards (http://127.0.0.1:18097)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
WORK="${WORK:-$HERE/.work}"
ARTISTS="${ARTISTS:-1500}"
GENRES="${GENRES:-150}"
IMAGE=${IMAGE:-docker.io/jellyfin/jellyfin:12.1.20260915-010956}
NAME=subfin-bench
URL=http://127.0.0.1:18097
KEEP=
[ "${1:-}" = --keep ] && KEEP=1
for tool in podman ffmpeg jq curl uv dotnet; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 1; }
done
mkdir -p "$WORK"

echo "== building the plugin"
dotnet publish -c Release "$ROOT/Jellyfin.Plugin.Subsonic" --nologo -v quiet
PLUGIN="$ROOT/Jellyfin.Plugin.Subsonic/bin/Release/net10.0/publish/Jellyfin.Plugin.Subsonic.dll"

echo "== library: $ARTISTS artists, $GENRES genres"
if [ "$(cat "$WORK/media/.size" 2>/dev/null)" != "$ARTISTS $GENRES" ]; then
  ARTISTS=$ARTISTS GENRES=$GENRES bash "$HERE/make-library.sh" "$WORK/media"
  echo "$ARTISTS $GENRES" > "$WORK/media/.size"
fi

echo "== starting Jellyfin"
podman rm -f "$NAME" >/dev/null 2>&1 || true
podman unshare rm -rf "$WORK/config" "$WORK/cache"
ver=$(jq -r .version "$ROOT/meta.json")
mkdir -p "$WORK/config/plugins/Subfin_$ver" "$WORK/cache"
cp "$PLUGIN" "$ROOT/meta.json" "$WORK/config/plugins/Subfin_$ver/"
podman run -d --name "$NAME" -p 127.0.0.1:18097:8096 \
  -v "$WORK/config:/config:Z" -v "$WORK/cache:/cache:Z" -v "$WORK/media:/media:ro,Z" "$IMAGE" >/dev/null
until curl -sf "$URL/Startup/Configuration" >/dev/null; do sleep 1; done
c() { command curl -sS --fail-with-body --retry 5 --retry-all-errors --retry-delay 1 "$@"; }
c -X POST "$URL/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
c "$URL/Startup/User" >/dev/null
c -X POST "$URL/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"tester","Password":"jfpass"}'
c -X POST "$URL/Startup/Complete"
TOKEN=$(c -X POST "$URL/Users/AuthenticateByName" -H 'Authorization: MediaBrowser Client="bench", Device="cli", DeviceId="bench-1", Version="1.0"' \
  -H 'Content-Type: application/json' -d '{"Username":"tester","Pw":"jfpass"}' | jq -r .AccessToken)
AUTH="MediaBrowser Token=\"$TOKEN\""
NOFETCH=$(jq -nc '[("MusicArtist","MusicAlbum","Audio") | {Type: ., MetadataFetchers: [], ImageFetchers: [], MetadataFetcherOrder: [], ImageFetcherOrder: []}]')
c -X POST "$URL/Library/VirtualFolders?name=Music&collectionType=music&refreshLibrary=false" -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
  -d "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"/media/music\"}],\"EnableInternetProviders\":false,\"TypeOptions\":$NOFETCH}}"
c -X POST "$URL/Library/Refresh" -H "Authorization: $AUTH"
want=$((ARTISTS * 2))
until [ "$(c "$URL/Items/Counts" -H "Authorization: $AUTH" | jq .SongCount)" = "$want" ] \
  && [ "$(c "$URL/ScheduledTasks?isHidden=false" -H "Authorization: $AUTH" | jq -r '[.[] | select(.Key=="RefreshLibrary") | .State][0]')" = Idle ]; do sleep 3; done
echo "library: $(c "$URL/Items/Counts" -H "Authorization: $AUTH" | jq -c '{SongCount,AlbumCount,ArtistCount}')"

echo "== timings"
status=0
uv run -q "$HERE/bench.py" "$URL" "$ARTISTS" "$WORK/bench.json" || status=$?
[ -n "$KEEP" ] || podman rm -f "$NAME" >/dev/null
exit $status
