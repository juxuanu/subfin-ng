# /// script
# requires-python = ">=3.12"
# dependencies = ["requests>=2.32"]
# ///
"""Times the Subsonic endpoints clients call most on the large library of make-library.sh.

usage: uv run bench.py <jellyfin url> <artists> <out.json>   (run.sh starts Jellyfin and runs it)
Each endpoint is called once to warm up, then RUNS times; the median must stay within its budget.
Budgets are 3-4 times what these take on a laptop, so a slower CI runner passes and real regressions
don't. Per-item lookups are what these catch: one per artist made the artist list take ~5 s, loading
each album's songs to count them made 500-album lists take ~2 s.
"""
import json, pathlib, statistics, sys, time

import requests

URL, ARTISTS, OUT = sys.argv[1], int(sys.argv[2]), pathlib.Path(sys.argv[3])
API = f"{URL}/opensubsonic/rest"
AUTH = {"u": "tester", "p": "jfpass", "v": "1.16.1", "c": "bench", "f": "json"}
RUNS = 5


def call(endpoint, **params):
    r = requests.get(f"{API}/{endpoint}", params={**AUTH, **params}, timeout=120)
    body = r.json()["subsonic-response"]
    if body["status"] != "ok":
        raise SystemExit(f"{endpoint} failed: {body}")
    return body


# Starred items and plays, so the starred and recently played lists have something to show
albums = call("getAlbumList2", type="alphabeticalByName", size=500)["albumList2"]["album"]
artist_id = albums[0]["artistId"]
songs = [s["id"] for s in call("getRandomSongs", size=100)["randomSongs"]["song"]]
artists = [a["id"] for i in call("getArtists")["artists"]["index"] for a in i["artist"]]
call("star", albumId=[a["id"] for a in albums[:100]], id=songs[:50], artistId=artists[:50])
for i, song in enumerate(songs):
    call("scrobble", id=song, submission="true", time=1_700_000_000_000 + i * 60_000)

# (endpoint, params, budget in seconds)
CASES = [
    ("getArtists", {}, 1.0),
    ("getIndexes", {}, 1.0),
    ("getArtist", {"id": artist_id}, 0.5),
    ("getArtistInfo2", {"id": artist_id}, 1.0),
    ("getMusicDirectory", {"id": artist_id}, 0.5),
    ("getAlbum", {"id": albums[0]["id"]}, 0.5),
    ("getAlbumList2", {"type": "alphabeticalByName", "size": 500}, 1.0),
    ("getAlbumList2", {"type": "newest", "size": 500}, 1.0),
    ("getAlbumList2", {"type": "random", "size": 500}, 1.0),
    ("getAlbumList2", {"type": "recent", "size": 50}, 0.5),
    ("getAlbumList2", {"type": "starred", "size": 500}, 0.5),
    ("getStarred2", {}, 0.5),
    ("getGenres", {}, 2.5),  # Jellyfin's own query (~4 ms per genre), not ours
    ("getSongsByGenre", {"genre": "Genre 1", "count": 500}, 0.5),
    ("getRandomSongs", {"size": 500}, 0.5),
    ("search3", {"query": "", "artistCount": 500, "albumCount": 500, "songCount": 500}, 2.0),  # clients' full sync
    ("search3", {"query": "Artist 01"}, 1.0),
    ("getScanStatus", {}, 0.2),  # clients poll it without pause while a scan runs
]

results, over = [], []
print(f"{'endpoint':<48} {'median':>8} {'budget':>8}")
for endpoint, params, budget in CASES:
    label = endpoint + "".join(f" {k}={v}" for k, v in params.items() if k != "id")
    call(endpoint, **params)
    times = []
    for _ in range(RUNS):
        start = time.perf_counter()
        call(endpoint, **params)
        times.append(time.perf_counter() - start)
    median = statistics.median(times)
    results.append({"endpoint": label, "median": round(median, 4), "budget": budget, "times": [round(t, 4) for t in times]})
    flag = "" if median <= budget else "  OVER BUDGET"
    print(f"{label:<48} {median:>7.3f}s {budget:>7.1f}s{flag}")
    if median > budget:
        over.append(label)

OUT.write_text(json.dumps({"artists": ARTISTS, "results": results}, indent=1))
print(f"{len(CASES)} endpoints, {len(over)} over budget" + (f": {', '.join(over)}" if over else ""))
sys.exit(1 if over else 0)
