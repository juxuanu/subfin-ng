#!/usr/bin/env bash
# A large synthetic music library: ARTISTS artists with two one-song albums each, spread over GENRES genres.
# usage: [ARTISTS=1500] [GENRES=150] make-library.sh <dir>
set -euo pipefail
OUT="$1"; ARTISTS="${ARTISTS:-1500}"; GENRES="${GENRES:-150}"
rm -rf "$OUT"; mkdir -p "$OUT/music"
ffmpeg -nostdin -v error -f lavfi -i "sine=frequency=440:duration=1" "$OUT/tone.flac"
n=0
for a in $(seq -w 1 "$ARTISTS"); do
  for b in 1 2; do
    d="$OUT/music/Artist $a/Album $a-$b"; mkdir -p "$d"
    # copies the same tone with new tags: no encoding, so thousands of files take seconds
    ffmpeg -nostdin -v error -i "$OUT/tone.flac" -c copy -metadata title="Track $a-$b" -metadata artist="Artist $a" \
      -metadata album_artist="Artist $a" -metadata album="Album $a-$b" -metadata genre="Genre $((10#$a % GENRES))" \
      -metadata track=1 "$d/01.flac" &
    n=$((n + 1)); [ $((n % 32)) = 0 ] && wait
  done
done
wait
rm "$OUT/tone.flac"
