#!/usr/bin/env bash
# Start a throwaway Jellyfin (podman) with a given Subfin build, configure it, and link Subsonic devices.
# usage: [IMAGE=...] [BASEURL=/jellyfin] WORK=<dir> up.sh <plugin.dll> <meta.json>   -> writes $WORK/creds.env
#   admin "tester" sees every library; "limited" may only access the Music library.
set -euo pipefail
T="${WORK:?set WORK to the working directory (media/, config/, cache/)}"
IMAGE=${IMAGE:-docker.io/jellyfin/jellyfin:12.1.20260915-010956}
BASEURL=${BASEURL:-}
NAME=subfin-jf
HOST=http://127.0.0.1:18096
URL=$HOST
HDR='MediaBrowser Client="subfin-test", Device="cli", DeviceId="subfin-test-1", Version="1.0"'

podman rm -f "$NAME" >/dev/null 2>&1 || true
podman unshare rm -rf "$T/config" "$T/cache"
ver=$(jq -r .version "$2")
mkdir -p "$T/config/plugins/Subfin_$ver" "$T/cache"
cp "$1" "$2" "$T/config/plugins/Subfin_$ver/"

podman run -d --name "$NAME" -p 127.0.0.1:18096:8096 \
  -v "$T/config:/config:Z" -v "$T/cache:/cache:Z" -v "$T/media:/media:ro,Z" "$IMAGE" >/dev/null
until curl -sf "$URL/Startup/Configuration" >/dev/null; do sleep 1; done  # wizard ready, not just the web host
JF_VERSION=$(curl -s "$URL/System/Info/Public" | jq -r .Version)
echo "jellyfin $JF_VERSION"
curl() { command curl -sS --fail-with-body --retry 5 --retry-all-errors --retry-delay 1 "$@"; }
login() {  # login <user> <password> -> Authorization header value
  local tok; tok=$(curl -X POST "$URL/Users/AuthenticateByName" -H "Authorization: $HDR" -H 'Content-Type: application/json' \
    -d "{\"Username\":\"$1\",\"Pw\":\"$2\"}" | jq -r .AccessToken)
  echo "MediaBrowser Token=\"$tok\", Client=\"subfin-test\", Device=\"cli\", DeviceId=\"subfin-test-$1\", Version=\"1.0\""
}

# startup wizard
curl -X POST "$URL/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl "$URL/Startup/User" >/dev/null
curl -X POST "$URL/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"tester","Password":"jfpass"}'
curl -X POST "$URL/Startup/Complete"
AUTH=$(login tester jfpass)

# libraries with every online metadata/image fetcher disabled, so results depend only on the files.
# (Types missing from TypeOptions get all default fetchers, hence the explicit empty lists.)
NOFETCH=$(jq -nc '[("MusicArtist","MusicAlbum","Audio","Movie","Person") | {Type: ., MetadataFetchers: [], ImageFetchers: [], MetadataFetcherOrder: [], ImageFetcherOrder: []}]')
for lib in "Music music /media/music" "Restricted music /media/restricted" "Movies movies /media/movies"; do
  set -- $lib
  curl -X POST "$URL/Library/VirtualFolders?name=$1&collectionType=$2&refreshLibrary=false" \
    -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
    -d "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"$3\"}],\"EnableInternetProviders\":false,\"TypeOptions\":$NOFETCH}}"
done
curl -X POST "$URL/Library/Refresh" -H "Authorization: $AUTH"

# Counts appear before tags are read; wait until albums carry their tag names and genres exist.
want='["Album One","Compilation","Double Album","Secret Album","Ünïcode Album"]|3'
for i in $(seq 180); do
  c=$(curl "$URL/Items?Recursive=true&IncludeItemTypes=MusicAlbum" -H "Authorization: $AUTH" | jq -c '[.Items[].Name]|sort')
  g=$(curl "$URL/MusicGenres" -H "Authorization: $AUTH" | jq '.Items | length')  # 10.11 reports TotalRecordCount=0 here
  [ "$c|$g" = "$want" ] && break; sleep 1
done
[ "$c|$g" = "$want" ] || { echo "library never finished scanning: $c genres=$g" >&2; exit 1; }
until [ "$(curl "$URL/ScheduledTasks?isHidden=false" -H "Authorization: $AUTH" | jq -r '[.[] | select(.Key=="RefreshLibrary") | .State][0]')" = Idle ]; do sleep 1; done
echo "library: $(curl "$URL/Items/Counts" -H "Authorization: $AUTH" | jq -c '{SongCount,AlbumCount,MovieCount}') albums=$c"

# second user: Music library only
MUSIC_ID=$(curl "$URL/Library/VirtualFolders" -H "Authorization: $AUTH" | jq -r '.[] | select(.Name=="Music") | .ItemId')
LID=$(curl -X POST "$URL/Users/New" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d '{"Name":"limited","Password":"lpass"}' | jq -r .Id)
POLICY=$(curl "$URL/Users/$LID" -H "Authorization: $AUTH" | jq -c --arg m "$MUSIC_ID" '.Policy | .EnableAllFolders=false | .EnabledFolders=[$m]')
curl -X POST "$URL/Users/$LID/Policy" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d "$POLICY"

# optionally serve Jellyfin under a base URL (as behind a path-based reverse proxy)
if [ -n "$BASEURL" ]; then
  NET=$(curl "$URL/System/Configuration/network" -H "Authorization: $AUTH" | jq -c --arg b "$BASEURL" '.BaseUrl=$b')
  curl -X POST "$URL/System/Configuration/network" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d "$NET"
  podman restart -t 2 "$NAME" >/dev/null 2>&1
  URL=$HOST$BASEURL
  until command curl -sf "$URL/System/Info/Public" >/dev/null; do sleep 1; done
  sleep 3
  AUTH=$(login tester jfpass)
fi

curl "$URL/Plugins" -H "Authorization: $AUTH" | jq -r '.[] | select(.Name=="Subfin") | "plugin: \(.Name) \(.Version) \(.Status)"'
link() { curl -X POST "$URL/subfin/api/devices/link" -H "Authorization: $1" -H 'Content-Type: application/json' -d '{"deviceLabel":"test"}'; }
A=$(link "$AUTH"); L=$(link "$(login limited lpass)")
{
  printf 'URL=%s\nHOST=%s\nBASEURL=%s\nJF_VERSION=%s\nJF_TOKEN=%s\n' "$URL" "$HOST" "$BASEURL" "$JF_VERSION" "$(sed -E 's/.*Token="([^"]+)".*/\1/' <<<"$AUTH")"
  printf 'AU=%s\nAP=%s\n' "$(jq -r .subsonicUsername <<<"$A")" "$(jq -r .password <<<"$A")"
  printf 'SU=%s\nSP=%s\n' "$(jq -r .subsonicUsername <<<"$L")" "$(jq -r .password <<<"$L")"
} > "$T/creds.env"
echo "subsonic users: $(jq -r .subsonicUsername <<<"$A") (admin), $(jq -r .subsonicUsername <<<"$L") (Music only)"
