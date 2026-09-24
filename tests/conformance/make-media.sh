#!/usr/bin/env bash
# Generate a small, fully tagged test library for Jellyfin + Subfin.
set -euo pipefail
M="$1/music"; V="$1/movies"
rm -rf "$1"; mkdir -p "$M" "$V"

tone() {  # [DUR=seconds] tone <out> <freq> <ffmpeg metadata args...>
  local out="$1" freq="$2"; shift 2
  mkdir -p "$(dirname "$out")"
  ffmpeg -nostdin -v error -f lavfi -i "sine=frequency=$freq:duration=${DUR:-3}" "$@" "$out"
}
cover() { ffmpeg -nostdin -v error -f lavfi -i "color=c=$2:s=300x300" -frames:v 1 "$1/folder.jpg"; }

# 1. plain album, FLAC. Song 3 lasts 12 s: Jellyfin won't start a transcode later than 5 s before the end.
A="$M/Test Artist/Album One (2001)"
for i in 1 2 3; do
  DUR=$([ $i = 3 ] && echo 12 || echo 3) tone "$A/0$i Song $i.flac" $((300 + i * 50)) -metadata title="Song $i" -metadata artist="Test Artist" \
    -metadata album_artist="Test Artist" -metadata album="Album One" -metadata date=2001 \
    -metadata track=$i/3 -metadata genre=Jazz
done
cover "$A" red
# lyrics files next to the songs: synced (LRC) for Song 1, plain text for Song 2
printf '[ar:Test Artist]\n[ti:Song 1]\n[00:00.50]First line\n[00:01.50]Second line\n' > "$A/01 Song 1.lrc"
printf 'Plain first line\nPlain second line\n' > "$A/02 Song 2.txt"

# 2. two-disc album in CD subfolders, cover in album root. Both discs use the same file names
# (as rips often do), which a ZIP of the album must keep apart.
A="$M/Test Artist/Double Album (2005)"
for d in 1 2; do for i in 1 2; do
  tone "$A/CD $d/0$i Track.flac" $((500 + d * 100 + i * 10)) -metadata title="Disc $d Track $i" \
    -metadata artist="Test Artist" -metadata album_artist="Test Artist" -metadata album="Double Album" \
    -metadata date=2005 -metadata track=$i/2 -metadata disc=$d/2 -metadata genre=Jazz
done; done
cover "$A" blue

# 3. various-artists compilation, M4A
A="$M/Various Artists/Compilation (2010)"
n=1; for who in "Artist A" "Artist B"; do
  tone "$A/0$n $who Tune.m4a" $((700 + n * 20)) -c:a aac -metadata title="$who Tune" -metadata artist="$who" \
    -metadata album_artist="Various Artists" -metadata album="Compilation" -metadata date=2010 \
    -metadata track=$n/2 -metadata genre=Pop -metadata compilation=1
  n=$((n + 1))
done
cover "$A" green

# 4. non-ASCII names, MP3
A="$M/Björk Ñandú/Ünïcode Album (2015)"
tone "$A/01 Ça va.mp3" 880 -c:a libmp3lame -q:a 6 -id3v2_version 3 -metadata title="Ça va" \
  -metadata artist="Björk Ñandú" -metadata album_artist="Björk Ñandú" -metadata album="Ünïcode Album" \
  -metadata date=2015 -metadata track=1/1 -metadata genre="Électronique"

# 5. a second music library that only the admin may access
tone "$1/restricted/Hidden Artist/Secret Album (2020)/01 Secret.flac" 990 -metadata title="Secret" \
  -metadata artist="Hidden Artist" -metadata album_artist="Hidden Artist" -metadata album="Secret Album" \
  -metadata date=2020 -metadata track=1/1 -metadata genre=Jazz
cover "$1/restricted/Hidden Artist/Secret Album (2020)" purple
printf '[00:00.50]Secret words\n' > "$1/restricted/Hidden Artist/Secret Album (2020)/01 Secret.lrc"

# 6. a movie with its own genre, to catch movie genres leaking into getGenres
mkdir -p "$V/Test Movie (2020)"
ffmpeg -nostdin -v error -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" -c:v mpeg4 "$V/Test Movie (2020)/Test Movie (2020).mkv"
cat > "$V/Test Movie (2020)/movie.nfo" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<movie><title>Test Movie</title><year>2020</year><genre>Thriller</genre></movie>
EOF

find "$1" -type f | sed "s|^$1/||" | sort
