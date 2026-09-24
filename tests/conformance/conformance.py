# /// script
# requires-python = ">=3.12"
# dependencies = ["jsonschema>=4.23", "referencing>=0.35", "requests>=2.32", "rfc3339-validator>=0.1.4"]
# ///
"""OpenSubsonic conformance + behaviour checks for a Subfin-enabled Jellyfin.

usage: uv run conformance.py <creds.env> <openapi-dir> <out.json>
Every JSON response is validated against the endpoint's schema in the OpenSubsonic OpenAPI spec;
behavioural checks compare results with the generated test library (make-media.sh). Run via run.sh.
"""
import base64, hashlib, io, json, pathlib, re, secrets, subprocess, sys, time, zipfile, xml.etree.ElementTree as ET
from datetime import datetime, timezone

import requests
from jsonschema import Draft7Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT7

creds = dict(l.split("=", 1) for l in pathlib.Path(sys.argv[1]).read_text().split())
SPEC = pathlib.Path(sys.argv[2]).resolve()
URL, SU, SP, AU, AP = creds["URL"], creds["SU"], creds["SP"], creds["AU"], creds["AP"]
JF_VERSION = creds["JF_VERSION"]
API = f"{URL}/opensubsonic/rest"

registry = Registry(retrieve=lambda uri: Resource.from_contents(
    json.loads(pathlib.Path(uri.removeprefix("file://")).read_text()), default_specification=DRAFT7))
_validators: dict[str, Draft7Validator] = {}


def validator(endpoint: str) -> Draft7Validator | None:
    if endpoint not in _validators:
        f = SPEC / "endpoints" / f"{endpoint}.json"
        if not f.exists():
            return None
        op = json.loads(f.read_text())
        op = op.get("get") or op.get("post")
        resp, base = op["responses"]["200"], f.parent
        if "$ref" in resp:  # shared response object in ../responses/
            rf = (base / resp["$ref"]).resolve()
            resp, base = json.loads(rf.read_text()), rf.parent
        ref = resp["content"]["application/json"]["schema"]["$ref"]
        uri = (base / ref).resolve().as_uri()
        # formats too: clients parse date-time fields strictly (Navic's Instant needs ISO 8601 with a zone)
        _validators[endpoint] = Draft7Validator({"$ref": uri}, registry=registry, format_checker=Draft7Validator.FORMAT_CHECKER)
    return _validators[endpoint]


results: list[dict] = []
raw: dict[str, object] = {}


def record(name, ok, detail="", kind="behaviour"):
    results.append({"check": name, "ok": bool(ok), "kind": kind, "detail": str(detail)[:400]})


def jf(method, path, token=None, check=True, headers=None, **kw):
    """Jellyfin's own API, as the admin unless another access token is given."""
    r = requests.request(method, f"{URL}{path}", timeout=30, **kw,
                         headers={"Authorization": f'MediaBrowser Token="{token or creds["JF_TOKEN"]}"', **(headers or {})})
    if check:
        r.raise_for_status()
    return r


def token(password):
    s = secrets.token_hex(6)
    return {"t": hashlib.md5((password + s).encode()).hexdigest(), "s": s}


# OpenSubsonic passwords, generated as an administrator would on the plugin page
user_ids = {u["Name"]: u["Id"] for u in jf("GET", "/Users").json()}
GEN = {name: jf("POST", f"/opensubsonic/admin/users/{user_ids[name]}/password").json()["Password"] for name in (SU, AU)}


def auth(**over):
    """Token login, the way Navidrome prefers apps to sign in: md5 of the limited user's OpenSubsonic password + salt."""
    a = {"u": SU, **token(GEN[SU]), "v": "1.16.1", "c": "conformance", "f": "json"}
    a.update(over)
    return {k: v for k, v in a.items() if v is not None}


def admin(**over):
    return auth(u=AU, **{**token(GEN[AU]), **over})


def pw(user, password, **over):
    """Password ("legacy") login."""
    return auth(u=user, p=password, t=None, s=None, **over)


def call(endpoint, params=None, auth_params=None, raw_resp=False, check_schema=True, label=None):
    params = list((params or {}).items()) if isinstance(params, dict) else list(params or [])
    r = requests.get(f"{API}/{endpoint}", params=list((auth_params or auth()).items()) + params, timeout=30)
    if raw_resp:
        return r
    try:
        body = r.json()
    except ValueError:
        record(f"{label or endpoint}: JSON body", False, f"HTTP {r.status_code}, {r.headers.get('content-type')}: {r.text[:200]}", "spec")
        return None
    raw[label or endpoint] = body
    resp = body.get("subsonic-response", {})
    if check_schema:
        v = validator(endpoint)
        if v is None:
            record(f"{endpoint}: schema", False, "endpoint not in spec", "spec")
        else:
            errs = list(v.iter_errors(body))
            # oneOf(success, failure) hides the real cause; report the deepest leaf errors
            leaves = []
            def walk(e):
                if e.context:
                    for c in e.context: walk(c)
                else:
                    leaves.append(e)
            for e in errs: walk(e)
            leaves = [e for e in leaves if "failed" not in json.dumps(e.schema)[:200]]  # drop failure-branch noise
            leaves.sort(key=lambda e: -len(list(e.absolute_path)))
            detail = "; ".join(f"{'/'.join(map(str, e.absolute_path))}: {e.message[:140]}" for e in leaves[:3])
            record(f"{label or endpoint}: schema", not errs, detail, "spec")
    return resp


def ok(resp):
    return resp is not None and resp.get("status") == "ok"


def err(resp):
    return (resp or {}).get("error", {}).get("code")


# ── envelope & auth ──────────────────────────────────────────────────────────
r = call("ping")
record("ping (token from the OpenSubsonic password) ok", ok(r), r)
for k in ("type", "serverVersion", "openSubsonic"):
    record(f"envelope has OpenSubsonic field '{k}'", k in (r or {}), r, "spec")
record("ping (OpenSubsonic password as p=) ok", ok(call("ping", auth_params=pw(SU, GEN[SU]), check_schema=False, label="generated p")))
record("ping (Jellyfin password as p=) ok", ok(call("ping", auth_params=pw(SU, SP), check_schema=False, label="jellyfin p")))
record("ping (Jellyfin password as p=enc:hex) ok", ok(call("ping", auth_params=pw(SU, "enc:" + SP.encode().hex()), check_schema=False, label="jellyfin enc")))
r = call("ping", auth_params=auth(**token("wrong-password")), check_schema=False, label="token-wrongpw")
record("token from a WRONG password -> error 40", err(r) == 40, r, "spec")
r = call("ping", auth_params=pw(creds["XU"], "wrong-password"), check_schema=False, label="ping-wrongpw")
record("ping with a WRONG Jellyfin password -> error 40", err(r) == 40, r, "spec")
r = call("getLicense", auth_params=pw("nobody", "whatever"), check_schema=False, label="license-unknown")
record("getLicense as an unknown user -> error 40", err(r) == 40, r, "spec")
r = call("getAlbumList2", {"type": "newest"}, auth_params=auth(u="nobody"), check_schema=False, label="albums-unknown")
record("authenticated endpoint with wrong credentials -> error 40", err(r) == 40, r)
r = call("ping", auth_params=auth(u=creds["XU"], **token(creds["XP"])), label="token without OpenSubsonic password")
record("token login without an OpenSubsonic password -> error 41 (Jellyfin keeps only a password hash)", err(r) == 41, r, "spec")
r = call("ping", auth_params=auth(p=SP), label="p+t")
record("password and token together -> error 43", err(r) == 43, r, "spec")
r = call("ping", auth_params={"apiKey": SP, "v": "1.16.1", "c": "conformance", "f": "json"}, label="apikey")
record("apiKey -> error 42 (not offered: sign in with Jellyfin credentials)", err(r) == 42, r, "spec")
r = call("ping", auth_params=auth(u=None), label="no-u")
record("no username -> error 10", err(r) == 10, r, "spec")
r = call("getOpenSubsonicExtensions", auth_params={"f": "json"})
record("getOpenSubsonicExtensions is public", ok(r), r)
names = [e["name"] for e in (r or {}).get("openSubsonicExtensions", [])]
known = {p.stem for p in (SPEC.parent / "content/en/docs/Extensions").glob("*.md")} - {"_index", "template"}
known = {k[0].lower() + k[1:] for k in known}
record("advertised extensions are real spec extensions", set(names) <= known, f"advertised={names}", "spec")
record("formPost is advertised, apiKeyAuthentication is not", "formPost" in names and "apiKeyAuthentication" not in names, names, "spec")
for path in ("/rest/ping", "/subfin/", "/subfin/api/devices"):
    x = requests.get(f"{URL}{path}", params=auth(), timeout=30, allow_redirects=False)
    record(f"old path {path} is gone (404)", x.status_code == 404, x.status_code)

# formPost: parameters in an application/x-www-form-urlencoded body
x = requests.post(f"{API}/ping.view", data=auth(), timeout=30)
record("formPost: credentials in the form body", x.ok and x.json().get("subsonic-response", {}).get("status") == "ok", x.text[:200], "spec")
x = requests.post(f"{API}/getAlbumList2", params={"f": "json"}, data={**auth(f=None), "type": "alphabeticalByName", "size": 50}, timeout=30)
got = sorted(a["name"] for a in x.json().get("subsonic-response", {}).get("albumList2", {}).get("album", [])) if x.ok else x.text[:200]
record("formPost: query string and body combine", got == ["Album One", "Compilation", "Double Album", "Ünïcode Album"], got, "spec")
x = requests.post(f"{API}/ping", params={"u": "nobody"}, data=auth(), timeout=30)
record("formPost: a key in both takes the body's value", x.ok and x.json().get("subsonic-response", {}).get("status") == "ok", x.text[:200], "spec")

x = requests.get(f"{API}/ping", params={**auth(), "f": "xml"}, timeout=30)
try:
    root = ET.fromstring(x.content)
    record("XML ping well-formed, subsonic-response root, restapi ns",
           root.tag == "{http://subsonic.org/restapi}subsonic-response" and root.get("status") == "ok", root.tag, "spec")
except ET.ParseError as e:
    record("XML ping well-formed", False, e, "spec")

# ── browsing ────────────────────────────────────────────────────────────────
r = call("getMusicFolders")
folders = (r or {}).get("musicFolders", {}).get("musicFolder", [])
record("getMusicFolders lists only music libraries", [f["name"] for f in folders] == ["Music"], folders)

r = call("getArtists")
artists = [a for i in (r or {}).get("artists", {}).get("index", []) for a in i.get("artist", [])]
anames = sorted(a["name"] for a in artists)
record("getArtists = the 3 album artists", anames == sorted(["Björk Ñandú", "Test Artist", "Various Artists"]), anames)
call("getIndexes")
test_artist = next((a for a in artists if a["name"] == "Test Artist"), None)

albums = {}
if test_artist:
    r = call("getArtist", {"id": test_artist["id"]})
    al = (r or {}).get("artist", {}).get("album", [])
    record("getArtist(Test Artist) has 2 albums", len(al) == 2, [a.get("name") for a in al])
r = call("getAlbumList2", {"type": "alphabeticalByName", "size": 50})
for a in (r or {}).get("albumList2", {}).get("album", []):
    albums[a["name"]] = a
record("getAlbumList2 alphabeticalByName returns the 4 albums",
       sorted(albums) == ["Album One", "Compilation", "Double Album", "Ünïcode Album"], sorted(albums))
# Clients keep the getArtists list and open an artist by an album's or song's artistId
artist_ids = {a["id"] for a in artists}
album_artist_ids = {a["name"]: a.get("artistId") for a in albums.values()}
record("every album's artistId is an artist from getArtists", all(i in artist_ids for i in album_artist_ids.values()),
       (album_artist_ids, sorted(artist_ids)))
if "Album One" in albums:
    one_songs = call("getAlbum", {"id": albums["Album One"]["id"]}, check_schema=False, label="artist ids of songs").get("album", {}).get("song", [])
    record("songs by an album artist carry that artist's getArtists id",
           bool(one_songs) and all(x.get("artistId") == album_artist_ids["Album One"] for x in one_songs), [x.get("artistId") for x in one_songs])
    r = call("getArtist", {"id": album_artist_ids["Album One"]}, check_schema=False, label="artist from an album")
    record("getArtist opens the artist by an album's artistId", ok(r) and r.get("artist", {}).get("name") == "Test Artist", r)

# OpenSubsonic artist lists ({id, name} per artist): what clients show under each song
if "Compilation" in albums:
    comp_album = call("getAlbum", {"id": albums["Compilation"]["id"]}, check_schema=False, label="artist lists").get("album", {})
    va = album_artist_ids["Compilation"]
    record("an album lists its artists, linked to getArtists",
           comp_album.get("artists") == [{"id": va, "name": "Various Artists"}] and comp_album.get("displayArtist") == "Various Artists",
           (comp_album.get("artists"), comp_album.get("displayArtist")))
    comp_tracks = comp_album.get("song", [])
    record("each song lists its own artists",
           sorted(tuple(a["name"] for a in x.get("artists", [])) for x in comp_tracks) == [("Artist A",), ("Artist B",)]
           and all(a.get("id") for x in comp_tracks for a in x.get("artists", [])),
           [x.get("artists") for x in comp_tracks])
    record("each song lists its album artists, linked to getArtists",
           all(x.get("albumArtists") == [{"id": va, "name": "Various Artists"}] for x in comp_tracks), [x.get("albumArtists") for x in comp_tracks])
    listed = call("getAlbumList2", {"type": "alphabeticalByName", "size": 50}, check_schema=False, label="album list artists").get("albumList2", {}).get("album", [])
    record("album lists carry each album's artists", all(x.get("artists") and all(a["id"] in artist_ids for a in x["artists"]) for x in listed),
           [(x.get("name"), x.get("artists")) for x in listed])
    x = requests.get(f"{API}/getAlbum", params={**auth(f="xml"), "id": albums["Compilation"]["id"]}, timeout=30)
    try:
        el = ET.fromstring(x.content).find("{http://subsonic.org/restapi}album")
        got = ([a.get("name") for a in el.findall("{http://subsonic.org/restapi}artists")],
               sorted(a.get("name") for sg in el.findall("{http://subsonic.org/restapi}song") for a in sg.findall("{http://subsonic.org/restapi}artists")))
    except (ET.ParseError, AttributeError) as e:
        got = repr(e)
    record("XML carries the artist lists as child elements", got == (["Various Artists"], ["Artist A", "Artist B"]), got)
for t, extra in [("newest", {}), ("alphabeticalByArtist", {}), ("random", {}), ("highest", {}), ("frequent", {}),
                 ("recent", {}), ("starred", {}), ("byYear", {"fromYear": 2000, "toYear": 2006}), ("byGenre", {"genre": "Jazz"})]:
    r = call("getAlbumList2", {"type": t, **extra}, label=f"getAlbumList2 type={t}")
    record(f"getAlbumList2 type={t} ok", ok(r), r if not ok(r) else "")
r = call("getAlbumList2", {"type": "byYear", "fromYear": 2000, "toYear": 2006}, check_schema=False)
yr = sorted(a["name"] for a in (r or {}).get("albumList2", {}).get("album", []))
record("getAlbumList2 byYear 2000-2006 = Album One, Double Album", yr == ["Album One", "Double Album"], yr)
r = call("getAlbumList2", {"type": "byGenre", "genre": "Jazz"}, check_schema=False)
gj = sorted(a["name"] for a in (r or {}).get("albumList2", {}).get("album", []))
record("getAlbumList2 byGenre Jazz = Album One, Double Album", gj == ["Album One", "Double Album"], gj)
call("getAlbumList", {"type": "newest"})

song_ids = []
if "Double Album" in albums:
    r = call("getAlbum", {"id": albums["Double Album"]["id"]})
    songs = (r or {}).get("album", {}).get("song", [])
    song_ids = [s["id"] for s in songs]
    record("getAlbum(Double Album): 4 songs on discs 1,1,2,2",
           [s.get("discNumber") for s in songs] == [1, 1, 2, 2], [(s.get("discNumber"), s.get("track"), s.get("title")) for s in songs])
    record("song duration ~3s", all(2 <= s.get("duration", 0) <= 4 for s in songs), [s.get("duration") for s in songs])
    r = call("getMusicDirectory", {"id": albums["Double Album"]["id"]}, label="getMusicDirectory(album)")
    record("getMusicDirectory(Double Album) lists its 4 songs", len((r or {}).get("directory", {}).get("child", [])) == 4,
           [c.get("title") for c in (r or {}).get("directory", {}).get("child", [])])
    r = call("getRandomSongs", {"size": 50}, check_schema=False, label="random-for-albumid")
    disc = [x for x in (r or {}).get("randomSongs", {}).get("song", []) if x.get("album") == "Double Album"]
    record("songs of a disc-folder album carry the album's id as albumId",
           disc and all(x.get("albumId") == albums["Double Album"]["id"] for x in disc), [x.get("albumId") for x in disc])
if "Album One" in albums:
    r = call("getAlbum", {"id": albums["Album One"]["id"]}, check_schema=False, label="getAlbum(Album One)")
    song_ids = [x["id"] for x in (r or {}).get("album", {}).get("song", [])]
    record("getAlbum(Album One) has 3 songs", len(song_ids) == 3, song_ids)
    if song_ids:
        call("getSong", {"id": song_ids[0]})
if "Compilation" in albums:
    r = call("getAlbum", {"id": albums["Compilation"]["id"]}, label="getAlbum(Compilation)")
    comp = (r or {}).get("album", {})
    record("compilation: album artist 'Various Artists', track artists A and B",
           comp.get("artist") == "Various Artists" and sorted(s.get("artist") for s in comp.get("song", [])) == ["Artist A", "Artist B"],
           (comp.get("artist"), [s.get("artist") for s in comp.get("song", [])]))

r = call("getGenres")
genres = {g["value"]: g for g in (r or {}).get("genres", {}).get("genre", [])}
record("getGenres = music genres only (no movie 'Thriller')", set(genres) == {"Jazz", "Pop", "Électronique"}, sorted(genres))
if "Jazz" in genres:
    record("getGenres Jazz counts songs=7 albums=2", (genres["Jazz"].get("songCount"), genres["Jazz"].get("albumCount")) == (7, 2), genres["Jazz"])
r = call("getSongsByGenre", {"genre": "Jazz", "count": 50})
record("getSongsByGenre Jazz = 7 songs", len((r or {}).get("songsByGenre", {}).get("song", [])) == 7)
r = call("getRandomSongs", {"size": 50})
record("getRandomSongs size=50 returns all 10 songs", len((r or {}).get("randomSongs", {}).get("song", [])) == 10)
r = call("getRandomSongs", {"size": 50, "genre": "Pop"}, check_schema=False)
record("getRandomSongs genre=Pop = only the 2 Pop songs", len((r or {}).get("randomSongs", {}).get("song", [])) == 2,
       [x.get("genre") for x in (r or {}).get("randomSongs", {}).get("song", [])])
r = call("getRandomSongs", {"size": 50, "fromYear": 2000, "toYear": 2006}, check_schema=False, label="random-years")
record("getRandomSongs fromYear/toYear 2000-2006 = 7 songs", len((r or {}).get("randomSongs", {}).get("song", [])) == 7,
       sorted({x.get("year") for x in (r or {}).get("randomSongs", {}).get("song", [])}))

for q, want in [("Song", (0, 0, 3)), ("Björk", (1, 0, 0)), ("Ünïcode", (0, 1, 0)), ("Ça va", (0, 0, 1))]:
    r = call("search3", {"query": q}, label=f"search3 '{q}'")
    sr = (r or {}).get("searchResult3", {})
    got = (len(sr.get("artist", [])), len(sr.get("album", [])), len(sr.get("song", [])))
    record(f"search3 '{q}' -> artists/albums/songs {want}", got == want, got)
r = call("search3", {"query": "", "songCount": 500, "albumCount": 500, "artistCount": 500}, label="search3 empty")
sr = (r or {}).get("searchResult3", {})
got = (len(sr.get("artist", [])), len(sr.get("album", [])), len(sr.get("song", [])))
record("search3 with empty query returns everything (used by clients to sync)", got == (3, 4, 10), got)
call("search2", {"query": "Song"})

# ── media ────────────────────────────────────────────────────────────────────
if song_ids:
    s = call("stream", {"id": song_ids[0]}, raw_resp=True)
    record("stream original: 200 audio/*", s.status_code == 200 and s.headers.get("content-type", "").startswith("audio/"),
           (s.status_code, s.headers.get("content-type"), len(s.content)))
    s = requests.get(f"{API}/stream", params={**auth(), "id": song_ids[0]}, headers={"Range": "bytes=0-99"}, timeout=30)
    record("stream honours Range (206, 100 bytes) for seeking", s.status_code == 206 and len(s.content) == 100,
           (s.status_code, s.headers.get("content-range"), len(s.content)))
    s = call("stream", [("id", song_ids[0]), ("format", "mp3"), ("maxBitRate", "128")], raw_resp=True)
    record("stream transcoded format=mp3: 200 audio/mpeg", s.status_code == 200 and s.headers.get("content-type", "").startswith("audio/mpeg"),
           (s.status_code, s.headers.get("content-type"), len(s.content)))

    def probe(body):
        """(duration in seconds, bitrate in bit/s) of an mp3, by ffprobe (from a file: a pipe has no duration)."""
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".mp3") as f:
            f.write(body)
            f.flush()
            out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration,bit_rate", "-of", "json", f.name],
                                 capture_output=True).stdout
        fmt = json.loads(out or b"{}").get("format", {})
        return float(fmt.get("duration") or 0), int(fmt.get("bit_rate") or 0)
    got = probe(s.content)
    record("... at the maxBitRate asked for (128 kbps)", 115_000 <= got[1] <= 141_000, got)
    # As a player does with one song (Song 3, 12 s): play it transcoded, seek, then lower the bitrate.
    # Each needs its own transcode, not the earlier one of the same song.
    full = probe(call("stream", [("id", song_ids[2]), ("format", "mp3")], raw_resp=True).content)
    seek = probe(call("stream", [("id", song_ids[2]), ("format", "mp3"), ("timeOffset", "4")], raw_resp=True).content)
    low = probe(call("stream", [("id", song_ids[2]), ("format", "mp3"), ("maxBitRate", "64")], raw_resp=True).content)
    record("stream timeOffset=4 starts 4 s in (the 12 s song plays for ~8 s)", 11 <= full[0] <= 13 and 7 <= seek[0] <= 9, (full, seek))
    record("stream maxBitRate=64 after a full-rate stream of the same song: 64 kbps", 56_000 <= low[1] <= 72_000, (full, low))
    d = call("download", {"id": song_ids[0]}, raw_resp=True)
    record("download: 200 with body", d.status_code == 200 and len(d.content) > 1000, (d.status_code, d.headers.get("content-type"), len(d.content)))
for name in ("Album One", "Double Album"):
    if name in albums:
        c = call("getCoverArt", {"id": albums[name].get("coverArt") or albums[name]["id"], "size": 64}, raw_resp=True)
        record(f"getCoverArt({name}): 200 image/*", c.status_code == 200 and c.headers.get("content-type", "").startswith("image/"),
               (c.status_code, c.headers.get("content-type")))

# Artists without an image of their own show one of their album covers (the newest), like Navidrome
def artist_cover(name):
    a = next((x for x in artists if x["name"] == name), None)
    if a is None:
        return None
    c = requests.get(f"{API}/getCoverArt", params={**auth(), "id": a.get("coverArt") or a["id"], "size": 64}, allow_redirects=False, timeout=30)
    if c.status_code in (301, 302, 307):
        final = requests.get(c.headers["location"] if c.headers["location"].startswith("http") else f"{creds['HOST']}{c.headers['location']}", timeout=30)
        return c.headers["location"], final.status_code, final.headers.get("content-type", "")
    return None, c.status_code, err(c.json().get("subsonic-response")) if c.headers.get("content-type", "").startswith("application/json") else c.text[:80]
if {"Double Album", "Compilation"} <= set(albums):
    got = artist_cover("Test Artist")
    record("an artist without an image shows its newest album's cover",
           got and f"/Items/{albums['Double Album']['id']}/Images/Primary" in got[0] and got[1] == 200 and got[2].startswith("image/"), got)
    got = artist_cover("Various Artists")
    record("... for a compilation's artist too", got and f"/Items/{albums['Compilation']['id']}/Images/Primary" in got[0] and got[1] == 200, got)
    got = artist_cover("Björk Ñandú")
    record("an artist with no image anywhere -> error 70", got and got[1] == 200 and got[2] == 70, got)
    ta = next((x for x in artists if x["name"] == "Test Artist"), None)
    if ta:
        jpg =subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=yellow:s=200x200", "-frames:v", "1",
                              "-f", "image2", "-c:v", "mjpeg", "-"], capture_output=True).stdout
        jf("POST", f"/Items/{ta['id']}/Images/Primary", headers={"Content-Type": "image/jpeg"}, data=base64.b64encode(jpg))
        got = artist_cover("Test Artist")
        record("an artist with its own image shows it", got and f"/Items/{ta['id']}/Images/Primary" in got[0] and got[1] == 200, got)

# ── user data ────────────────────────────────────────────────────────────────
if song_ids and "Album One" in albums:
    record("star song+album", ok(call("star", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"])])))
    r = call("getStarred2")
    st = (r or {}).get("starred2", {})
    record("getStarred2 shows starred song and album", len(st.get("song", [])) == 1 and len(st.get("album", [])) == 1,
           (len(st.get("song", [])), len(st.get("album", []))))
    call("getStarred")
    # An album with the user's own data (starred, rated): those fields come after its song list
    call("setRating", {"id": albums["Album One"]["id"], "rating": 3}, check_schema=False, label="rate album")
    r = call("getAlbum", {"id": albums["Album One"]["id"]}, label="getAlbum starred+rated")
    a = (r or {}).get("album", {})
    record("getAlbum of a starred, rated album (JSON)", ok(r) and a.get("starred") and a.get("userRating") == 3 and len(a.get("song", [])) == 3, r)
    x = requests.get(f"{API}/getAlbum", params={**auth(f="xml"), "id": albums["Album One"]["id"]}, timeout=30)
    try:
        el = ET.fromstring(x.content).find("{http://subsonic.org/restapi}album")
        got = (el.get("starred") is not None, el.get("userRating"), len(el.findall("{http://subsonic.org/restapi}song")))
    except (ET.ParseError, AttributeError) as e:
        got = (x.status_code, x.text[:200], repr(e))
    record("getAlbum of a starred, rated album (XML)", got == (True, "3", 3), got)
    call("setRating", {"id": albums["Album One"]["id"], "rating": 0}, check_schema=False, label="unrate album")
    record("unstar", ok(call("unstar", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"])])))
    record("setRating 4", ok(call("setRating", {"id": song_ids[0], "rating": 4})))
    r = call("getSong", {"id": song_ids[0]}, check_schema=False)
    record("getSong reflects userRating 4", (r or {}).get("song", {}).get("userRating") == 4, (r or {}).get("song", {}).get("userRating"))
    record("scrobble submission=true", ok(call("scrobble", {"id": song_ids[0], "submission": "true"})))
    r = call("getSong", {"id": song_ids[0]}, check_schema=False)
    record("getSong playCount incremented to 1", (r or {}).get("song", {}).get("playCount") == 1, (r or {}).get("song", {}).get("playCount"))
    call("scrobble", {"id": song_ids[1], "submission": "false"}, check_schema=False, label="now playing")
    sess = [x for x in jf("GET", "/Sessions").json() if x.get("UserName") == SU]
    record("now playing shows in Jellyfin as a session of the client (c=) on its own device",
           any(x.get("Client") == "conformance" and x.get("DeviceId", "").startswith("opensubsonic-") for x in sess),
           [(x.get("Client"), x.get("DeviceId"), x.get("DeviceName")) for x in sess])
    r = call("getNowPlaying", label="getNowPlaying while playing")
    np = (r or {}).get("nowPlaying", {}).get("entry", [])
    record("getNowPlaying lists the song being played, with its user and player",
           any(e.get("id") == song_ids[1] and e.get("username") == SU and isinstance(e.get("playerId"), int) for e in np), np)

    # Jellyfin's dashboard shows how a song is played: the file as is, unless the plugin transcoded it.
    # The compilation's songs aren't played anywhere else in this suite.
    comp_songs = [x["id"] for x in call("getAlbum", {"id": albums["Compilation"]["id"]}, check_schema=False, label="compilation songs").get("album", {}).get("song", [])]
    def play_method(song):
        sess = [x for x in jf("GET", "/Sessions").json() if x.get("UserName") == SU and x.get("Client") == "conformance"]
        return [x.get("PlayState", {}).get("PlayMethod") for x in sess if (x.get("NowPlayingItem") or {}).get("Id", "").replace("-", "") == song]
    def play_count(song):
        return call("getSong", {"id": song}, check_schema=False, label="play count").get("song", {}).get("playCount", 0)
    if len(comp_songs) >= 2:
        call("stream", {"id": comp_songs[0]}, raw_resp=True)
        call("scrobble", {"id": comp_songs[0], "submission": "false"}, check_schema=False, label="now playing direct")
        got = play_method(comp_songs[0])
        record("a song streamed as is shows as direct play in Jellyfin", got == ["DirectPlay"], got)
        # A client repeats "now playing" and then submits the finished song: one play
        call("scrobble", {"id": comp_songs[0], "submission": "false"}, check_schema=False, label="now playing again")
        call("scrobble", {"id": comp_songs[0], "submission": "true"}, check_schema=False, label="finished")
        record("now playing, then finished, counts one play", play_count(comp_songs[0]) == 1, play_count(comp_songs[0]))
        call("stream", [("id", comp_songs[1]), ("format", "mp3"), ("maxBitRate", "128")], raw_resp=True)
        call("scrobble", {"id": comp_songs[1], "submission": "false"}, check_schema=False, label="now playing transcoded")
        got = play_method(comp_songs[1])
        record("a transcoded song shows as transcoding in Jellyfin", got == ["Transcode"], got)
    else:
        record("compilation has 2 songs for the playback checks", False, comp_songs)

    # playlists
    r = call("createPlaylist", [("name", "Conformance"), ("songId", song_ids[0]), ("songId", song_ids[1])])
    pl = (r or {}).get("playlist", {})
    record("createPlaylist with 2 songs", ok(r) and pl.get("songCount") == 2, r)
    if pl.get("id"):
        call("getPlaylists")
        r = call("updatePlaylist", [("playlistId", pl["id"]), ("name", "Renamed"), ("songIdToAdd", song_ids[2]), ("songIndexToRemove", "0")])
        record("updatePlaylist (rename + add + remove) ok", ok(r), r)
        r = call("getPlaylist", {"id": pl["id"]})
        p2 = (r or {}).get("playlist", {})
        record("playlist after update: name Renamed, songs [2nd, 3rd]",
               p2.get("name") == "Renamed" and [s["id"] for s in p2.get("entry", [])] == song_ids[1:3],
               (p2.get("name"), [s.get("title") for s in p2.get("entry", [])]))
        record("deletePlaylist", ok(call("deletePlaylist", {"id": pl["id"]})))
        r = call("getPlaylists", check_schema=False)
        record("playlist gone after delete", not (r or {}).get("playlists", {}).get("playlist"), r)

    record("savePlayQueue", ok(call("savePlayQueue", [("id", song_ids[0]), ("id", song_ids[1]), ("current", song_ids[1]), ("position", "1500")])))
    r = call("getPlayQueue")
    q = (r or {}).get("playQueue", {})
    record("getPlayQueue round-trips entries/current/position",
           [e["id"] for e in q.get("entry", [])] == song_ids[:2] and q.get("current") == song_ids[1] and q.get("position") == 1500,
           (q.get("current"), q.get("position"), len(q.get("entry", []))))
    record("getPlayQueue's changed is an ISO 8601 date-time, a moment ago",
           bool(re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)", q.get("changed", "")))
           and abs(datetime.fromisoformat(q["changed"].replace("Z", "+00:00")).timestamp() - time.time()) < 120, q.get("changed"))

# ── starred = a Jellyfin favourite, both ways, artists included ─────────────
if song_ids and test_artist and "Album One" in albums:
    aid, alid, sid = test_artist["id"], albums["Album One"]["id"], song_ids[0]

    def starred_where(label):
        """Where Test Artist, Album One and Song 1 come out starred."""
        def artists_of(endpoint, key):
            r = call(endpoint, check_schema=False, label=f"{label}: {endpoint}") or {}
            return [a for i in r.get(key, {}).get("index", []) for a in i.get("artist", [])]
        starred = lambda items: any(x.get("id") == aid and x.get("starred") for x in items)
        found = (call("search3", {"query": "Test Artist"}, check_schema=False, label=f"{label}: search3") or {}).get("searchResult3", {})
        st = (call("getStarred2", check_schema=False, label=f"{label}: getStarred2") or {}).get("starred2", {})
        get = lambda endpoint, key, i: (call(endpoint, {"id": i}, check_schema=False, label=f"{label}: {endpoint}") or {}).get(key, {})
        return {
            "getArtists": starred(artists_of("getArtists", "artists")), "getIndexes": starred(artists_of("getIndexes", "indexes")),
            "search3": starred(found.get("artist", [])), "getArtist": bool(get("getArtist", "artist", aid).get("starred")),
            "getAlbum": bool(get("getAlbum", "album", alid).get("starred")), "getSong": bool(get("getSong", "song", sid).get("starred")),
            "getStarred2": sorted(k for k, i in (("artist", aid), ("album", alid), ("song", sid)) if any(x.get("id") == i for x in st.get(k, []))),
        }

    everywhere = {k: True for k in ("getArtists", "getIndexes", "search3", "getArtist", "getAlbum", "getSong")} | {"getStarred2": ["album", "artist", "song"]}
    nowhere = {k: False for k in everywhere} | {"getStarred2": []}
    stars = [("artistId", aid), ("albumId", alid), ("id", sid)]
    call("star", stars, check_schema=False, label="star all three")
    got = starred_where("starred")
    record("starred (an artist, an album, a song) shows in every endpoint, artists included", got == everywhere, got)
    favourite = lambda i: (jf("GET", f"/Items/{i}", params={"userId": user_ids[SU]}).json().get("UserData") or {}).get("IsFavorite")
    record("... and is a favourite in Jellyfin", [favourite(i) for _, i in stars] == [True] * 3, [favourite(i) for _, i in stars])
    call("unstar", stars, check_schema=False, label="unstar all three")
    got = starred_where("unstarred")
    record("unstarring clears it everywhere", got == nowhere, got)

    # favourites set in Jellyfin itself: the artist as Jellyfin's own artist list has it
    jf_artist = next((a["Id"] for a in jf("GET", "/Artists/AlbumArtists", params={"searchTerm": "Test Artist", "userId": user_ids[SU]}).json()
                      .get("Items", []) if a.get("Name") == "Test Artist"), None)
    record("Jellyfin's album artist is the artist getArtists lists", (jf_artist or "").replace("-", "") == aid, (jf_artist, aid))
    marked = [jf("POST", f"/UserFavoriteItems/{i}", params={"userId": user_ids[SU]}, check=False).status_code for i in (jf_artist or aid, alid, sid)]
    got = starred_where("Jellyfin favourite")
    record("a favourite set in Jellyfin is starred in every endpoint", marked == [200] * 3 and got == everywhere, (marked, got))
    for i in (jf_artist or aid, alid, sid):
        jf("DELETE", f"/UserFavoriteItems/{i}", params={"userId": user_ids[SU]}, check=False)
    got = starred_where("Jellyfin favourite removed")
    record("... and removing it in Jellyfin unstars it", got == nowhere, got)

# ── share links must only reach the shared items ────────────────────────────
if song_ids:
    r = call("createShare", {"id": song_ids[0], "description": "conformance"}, check_schema=False, label="createShare")
    share = ((r or {}).get("shares", {}).get("share") or [{}])[0]
    secret = share.get("url", "").partition("secret=")[2]
    if secret:
        from urllib.parse import unquote
        sa = {"u": f"share_{share['id']}", "p": unquote(secret), "v": "1.16.1", "c": "conformance", "f": "json"}
        s = requests.get(f"{API}/stream", params={**sa, "id": song_ids[0]}, timeout=30)
        record("share link: stream shared song -> 200", s.status_code == 200, s.status_code)
        s = requests.get(f"{API}/download", params={**sa, "id": song_ids[1]}, timeout=30)
        try:
            code = s.json()["subsonic-response"]["error"]["code"]
        except Exception:
            code = None
        record("share link: download NOT-shared song -> error 50", code == 50, (s.status_code, s.text[:120]), "security")
        r = call("getAlbumList2", {"type": "newest"}, auth_params=sa, check_schema=False, label="share-browse")
        record("share link: browsing the library -> error 50", err(r) == 50, r, "security")
        r = call("createPlaylist", {"name": "via-share", "songId": song_ids[1]}, auth_params=sa, check_schema=False, label="share-write")
        record("share link: createPlaylist -> error 50", err(r) == 50, r, "security")
        if ok(r):  # clean up if the server wrongly allowed it
            call("deletePlaylist", {"id": r["playlist"]["id"]}, check_schema=False, label="share-cleanup")

        # the public share page, its playlist and ZIP download
        page =requests.get(share["url"], timeout=30)
        record("share page: 200 HTML streaming through /opensubsonic/rest",
               page.status_code == 200 and "/opensubsonic/rest/stream.view" in page.text and f"/opensubsonic/share/{share['id']}/m3u" in page.text,
               (page.status_code, page.text[:200]))
        bad = requests.get(share["url"].replace("secret=", "secret=x"), timeout=30)
        record("share page with a wrong secret -> 404", bad.status_code == 404, bad.status_code, "security")
        m3u = requests.get(share["url"].replace(f"/{share['id']}?", f"/{share['id']}/m3u?"), timeout=30)
        record("share M3U lists the shared song's stream URL", m3u.status_code == 200 and m3u.text.count("/opensubsonic/rest/stream.view?id=") == 1,
               (m3u.status_code, m3u.text[:300]))
        try:
            z = requests.get(share["url"].replace(f"/{share['id']}?", f"/{share['id']}/download?"), timeout=30)
            zn = (z.status_code, sorted(zipfile.ZipFile(io.BytesIO(z.content)).namelist()))
        except (requests.RequestException, zipfile.BadZipFile) as e:
            zn = repr(e)
        record("share ZIP holds the shared song and a playlist", zn == (200, ["01 Song 1.flac", "playlist.m3u8"]), zn)
        call("updateShare", {"id": share["id"], "expires": 1000}, check_schema=False, label="expire share")
        gone = requests.get(share["url"], timeout=30)
        record("expired share page -> 410", gone.status_code == 410, gone.status_code, "security")
        r = call("ping", auth_params=sa, check_schema=False, label="expired share login")
        record("expired share link can't sign in -> error 40", err(r) == 40, r, "security")
    else:
        record("createShare returned a share URL with secret", False, r)

# ── shares of albums, playlists and artists; getShares; the owner's changes ─
PLUGIN_ID = "4a3b2c1d-e5f6-7890-abcd-ef1234567890"


def create_share(ids, auth_params=None, label="createShare", check_schema=True, **params):
    r = call("createShare", [("id", i) for i in ids] + list(params.items()), auth_params=auth_params, check_schema=check_schema, label=label)
    return ((r or {}).get("shares", {}).get("share") or [None])[0]


def shares(auth_params=None, label="getShares"):
    return {x["id"]: x for x in (call("getShares", auth_params=auth_params, check_schema=False, label=label) or {}).get("shares", {}).get("share", [])}


def share_link(sh):
    """The credentials a share link signs in with, as its page and M3U use them."""
    from urllib.parse import unquote
    return {"u": f"share_{sh['id']}", "p": unquote(sh["url"].partition("secret=")[2]), "v": "1.16.1", "c": "conformance", "f": "json"}


def share_page(sh, sub=""):
    """The public share page, or its "/m3u" or "/download"."""
    return requests.get(sh["url"].replace(f"/{sh['id']}?", f"/{sh['id']}{sub}?"), timeout=30)


def page_tracks(html):
    """Titles of the tracks a share page plays."""
    m = re.search(r"const tracks = (\[.*?\]);", html)
    return [t["title"] for t in json.loads(m.group(1))] if m else None


def m3u_ids(text):
    return re.findall(r"stream\.view\?id=([0-9a-f]+)", text)


def zip_contents(resp):
    """(file names, the files its playlist.m3u8 lists) of a share ZIP."""
    try:
        z = zipfile.ZipFile(io.BytesIO(resp.content))
        return z.namelist(), [l for l in z.read("playlist.m3u8").decode().splitlines() if l and not l.startswith("#")]
    except (zipfile.BadZipFile, KeyError) as e:
        return repr(e), None


def iso_ms(ms):
    return datetime.fromtimestamp(ms / 1000, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def seconds(iso_time):
    try:
        return datetime.strptime(iso_time[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc).timestamp()
    except (TypeError, ValueError):
        return 0


def plugin_config(**changes):
    cfg = jf("GET", f"/Plugins/{PLUGIN_ID}/Configuration").json()
    jf("POST", f"/Plugins/{PLUGIN_ID}/Configuration", json={**cfg, **changes})


if song_ids and test_artist and {"Album One", "Double Album", "Compilation"} <= set(albums):
    double = [x["id"] for x in call("getAlbum", {"id": albums["Double Album"]["id"]}, check_schema=False, label="double songs").get("album", {}).get("song", [])]
    comp_first = (comp.get("song") or [{}])[0].get("id")

    # an album: what createShare answers, what getShares lists, and what the link serves
    started, expires = time.time(), (int(time.time()) + 3600) * 1000
    sh = create_share([albums["Double Album"]["id"]], label="createShare album", description="The double album", expires=expires)
    if sh:
        record("an album share holds its songs, in disc order", [e["id"] for e in sh.get("entry", [])] == double,
               [e.get("title") for e in sh.get("entry", [])])
        record("createShare answers with the share: its link, owner, description, dates and no visits yet",
               sh.get("url", "").startswith(f"{URL}/opensubsonic/share/{sh['id']}?secret=") and sh.get("username") == SU
               and sh.get("description") == "The double album" and abs(seconds(sh.get("created")) - started) < 120
               and sh.get("expires") == iso_ms(expires) and sh.get("visitCount") == 0, sh)
        listed = {x["id"]: x for x in (call("getShares") or {}).get("shares", {}).get("share", [])}
        record("getShares lists it just as createShare described it", listed.get(sh["id"]) == sh, listed.get(sh["id"]))
        record("getShares lists only the user's own shares", all(x.get("username") == SU for x in listed.values()),
               [x.get("username") for x in listed.values()], "security")

        link = share_link(sh)
        page = share_page(sh)
        record("the album's share page lists its 4 songs", page.status_code == 200 and page_tracks(page.text) ==
               ["Disc 1 Track 1", "Disc 1 Track 2", "Disc 2 Track 1", "Disc 2 Track 2"], (page.status_code, page_tracks(page.text)))
        m3u = share_page(sh, "/m3u")
        record("... its M3U lists them in order", m3u.status_code == 200 and m3u_ids(m3u.text) == double, m3u_ids(m3u.text))
        names, listing = zip_contents(share_page(sh, "/download"))
        want = ["01 Track.flac", "02 Track.flac", "01 Track (1).flac", "02 Track (1).flac"]
        record("... its ZIP keeps songs with the same file name apart (both discs have 01 Track.flac)",
               isinstance(names, list) and sorted(names) == sorted(want + ["playlist.m3u8"]), names)
        record("... and its playlist lists them in order", listing == want, listing)
        record("the link plays every song of the album",
               all(requests.get(f"{API}/stream", params={**link, "id": i}, timeout=30).status_code == 200 for i in double))
        s = requests.get(f"{API}/stream", params={**link, "id": double[0], "format": "mp3"}, timeout=30)
        record("... transcoded too", s.status_code == 200 and s.headers.get("content-type", "").startswith("audio/mpeg"),
               (s.status_code, s.headers.get("content-type")))
        d = requests.get(f"{API}/download", params={**link, "id": double[0]}, timeout=30)
        record("... and downloads them", d.status_code == 200 and d.headers.get("content-type", "").startswith("audio/") and len(d.content) > 1000,
               (d.status_code, d.headers.get("content-type")))
        visits = shares(label="share visits").get(sh["id"], {}).get("visitCount")
        record("opening the page and its M3U count as visits (the ZIP and playing don't)", visits == 2, visits)

        # the owner's changes: each field alone, expiry and its removal, deletion
        call("updateShare", {"id": sh["id"], "description": "Renamed"}, label="updateShare description")
        now = shares(label="after description").get(sh["id"], {})
        record("updateShare with only a description keeps the expiry", (now.get("description"), now.get("expires")) == ("Renamed", sh["expires"]),
               (now.get("description"), now.get("expires")))
        later = expires + 86_400_000
        call("updateShare", {"id": sh["id"], "expires": later}, label="updateShare expires")
        now = shares(label="after expires").get(sh["id"], {})
        record("updateShare with only an expiry keeps the description", (now.get("description"), now.get("expires")) == ("Renamed", iso_ms(later)),
               (now.get("description"), now.get("expires")))
        call("updateShare", {"id": sh["id"], "expires": 1000}, check_schema=False, label="updateShare to the past")
        record("a share updated to expire in the past: its page -> 410", share_page(sh).status_code == 410)
        call("updateShare", {"id": sh["id"], "expires": 0}, check_schema=False, label="updateShare expires=0")
        record("expires=0 removes the expiry: the page and link work again",
               share_page(sh).status_code == 200 and ok(call("ping", auth_params=link, check_schema=False, label="unexpired link")))
        record("deleteShare", ok(call("deleteShare", {"id": sh["id"]})))
        record("a deleted share is gone: not listed, its page 404 and its link can't sign in",
               sh["id"] not in shares(label="after delete") and share_page(sh).status_code == 404
               and err(call("ping", auth_params=link, check_schema=False, label="deleted link")) == 40, kind="security")

    # a playlist, an artist, several items at once
    r = call("createPlaylist", [("name", "to share"), ("songId", song_ids[2]), ("songId", song_ids[0])], check_schema=False, label="playlist to share")
    plid = (r or {}).get("playlist", {}).get("id")
    if plid:
        psh = create_share([plid], check_schema=False, label="createShare playlist")
        record("a playlist share holds the playlist's songs, in its order",
               [e["id"] for e in (psh or {}).get("entry", [])] == [song_ids[2], song_ids[0]], psh)
        if psh:
            call("deleteShare", {"id": psh["id"]}, check_schema=False, label="playlist share cleanup")
        call("deletePlaylist", {"id": plid}, check_schema=False, label="playlist to share cleanup")
    artsh = create_share([test_artist["id"]], check_schema=False, label="createShare artist")
    record("an artist share holds every song of the artist's albums",
           sorted(e["id"] for e in (artsh or {}).get("entry", [])) == sorted(song_ids + double), artsh)
    if artsh:
        call("deleteShare", {"id": artsh["id"]}, check_schema=False, label="artist share cleanup")
    msh = create_share([albums["Album One"]["id"], song_ids[1], comp_first], check_schema=False, label="createShare several")
    record("a share of several items holds each song once, in the order given",
           [e["id"] for e in (msh or {}).get("entry", [])] == song_ids + [comp_first], msh)
    r = call("createShare", check_schema=False, label="createShare without id")
    record("createShare without id -> error 10", err(r) == 10, r, "spec")
    r = call("createShare", {"id": secrets.token_hex(16)}, check_schema=False, label="createShare unknown id")
    record("createShare of an unknown id -> error 70", err(r) == 70, r, "spec")

    # sharing turned off on the plugin page
    if msh:
        plugin_config(SharingEnabled=False)
        try:
            r = call("getShares", check_schema=False, label="sharing off: getShares")
            record("with sharing turned off, getShares -> error 50", err(r) == 50, r)
            r = call("createShare", {"id": song_ids[0]}, check_schema=False, label="sharing off: createShare")
            record("... createShare -> error 50", err(r) == 50, r)
            u = (call("getUser", {"username": SU}, check_schema=False, label="sharing off: getUser") or {}).get("user", {})
            record("... getUser says shareRole false", u.get("shareRole") is False, u)
            record("... share pages are gone (404)", share_page(msh).status_code == 404, kind="security")
            r = call("ping", auth_params=share_link(msh), check_schema=False, label="sharing off: link")
            record("... and links can't sign in -> error 40", err(r) == 40, r, "security")
        finally:
            plugin_config(SharingEnabled=True)
        record("turning sharing back on brings the links back", share_page(msh).status_code == 200)
        call("deleteShare", {"id": msh["id"]}, check_schema=False, label="several share cleanup")

r = call("getAlbum", {"id": secrets.token_hex(16)}, label="error envelope")  # schema-checks a failure response

# ── OpenSubsonic passwords (the plugin page's API, administrators only) ─────
x = requests.get(f"{URL}/opensubsonic/admin/users", timeout=30)
record("password admin API without a Jellyfin login -> 401", x.status_code == 401, x.status_code, "security")
limited_token = requests.post(f"{URL}/Users/AuthenticateByName", json={"Username": SU, "Pw": SP}, timeout=30, headers={
    "Authorization": 'MediaBrowser Client="conformance", Device="cli", DeviceId="conformance-limited", Version="1.0"'}).json()["AccessToken"]
for method, path in (("GET", "users"), ("POST", f"users/{user_ids[SU]}/password"), ("DELETE", f"users/{user_ids[SU]}/password")):
    x = jf(method, f"/opensubsonic/admin/{path}", token=limited_token, check=False)
    record(f"password admin API as a non-admin ({method} {path.split('/')[0]}) -> 403", x.status_code == 403, x.status_code, "security")
record("... and the limited user's password still works", ok(call("ping", label="after non-admin attempts")))
listed = {u["Name"]: u for u in jf("GET", "/opensubsonic/admin/users").json()}
record("the admin list shows every Jellyfin user and who has a password",
       set(listed) == set(user_ids) and all(bool(listed[n].get("PasswordCreated")) == (n in GEN) for n in listed)
       and listed[AU]["IsAdministrator"] and listed[creds["OU"]]["IsDisabled"], listed)
record("the list never includes passwords", not any(g in json.dumps(list(listed.values())) for g in GEN.values())
       and all("Password" not in u for u in listed.values()), listed, "security")
x = jf("POST", f"/opensubsonic/admin/users/{secrets.token_hex(16)}/password", check=False)
record("generating for an unknown user -> 404", x.status_code == 404, x.status_code)
XU, XP = creds["XU"], creds["XP"]
first = jf("POST", f"/opensubsonic/admin/users/{user_ids[XU]}/password")
record("generating answers with the username and a no-store password",
       first.json().get("Username") == XU and "no-store" in first.headers.get("cache-control", ""), (first.json().get("Username"), first.headers.get("cache-control")))
first = first.json()["Password"]
record("a generated password signs in by token", ok(call("ping", auth_params=auth(u=XU, **token(first)), label="extra token")))
second = jf("POST", f"/opensubsonic/admin/users/{user_ids[XU]}/password").json()["Password"]
r = call("ping", auth_params=auth(u=XU, **token(first)), label="regenerated: old")
record("regenerating retires the old password at once", err(r) == 40, r, "security")
record("... and the new one works", ok(call("ping", auth_params=auth(u=XU, **token(second)), label="regenerated: new")))
jf("DELETE", f"/opensubsonic/admin/users/{user_ids[XU]}/password")
r = call("ping", auth_params=auth(u=XU, **token(second)), label="removed")
record("removing it ends token logins (error 41)", err(r) == 41, r, "security")
offline = jf("POST", f"/opensubsonic/admin/users/{user_ids[creds['OU']]}/password").json()["Password"]
r = call("ping", auth_params=auth(u=creds["OU"], **token(offline)), label="disabled token")
record("a disabled account can't sign in with its OpenSubsonic password either -> error 50", err(r) == 50, r, "security")

# ── Jellyfin's account rules apply ──────────────────────────────────────────
def set_policy(name, **changes):
    policy = jf("GET", f"/Users/{user_ids[name]}").json()["Policy"]
    jf("POST", f"/Users/{user_ids[name]}/Policy", json={**policy, **changes})

KU, KP = creds["KU"], creds["KP"]
r = call("ping", auth_params=pw(creds["OU"], creds["OP"]), label="disabled account")
record("a disabled Jellyfin account -> error 50", err(r) == 50, r, "security")
record("extra signs in", ok(call("ping", auth_params=pw(XU, XP), label="extra signs in")))
jf("POST", f"/Users/Password?userId={user_ids[XU]}", json={"NewPw": XP + "2"})
r = call("ping", auth_params=pw(XU, XP), label="old password")
record("a changed password stops working at once (not when the login cache expires)", err(r) == 40, r, "security")
record("... and the new password works", ok(call("ping", auth_params=pw(XU, XP + "2"), label="new password")))
set_policy(XU, IsDisabled=True)
r = call("ping", auth_params=pw(XU, XP + "2"), label="disabled while signed in")
record("disabling a signed-in account takes effect at once -> error 50", err(r) == 50, r, "security")
set_policy(XU, IsDisabled=False)
set_policy(XU, AccessSchedules=[{"DayOfWeek": "Everyday", "StartHour": 0, "EndHour": 0.5, "UserId": user_ids[XU]}])
if datetime.now(timezone.utc).hour >= 1:  # allowed 00:00-00:30 server time (the container runs on UTC)
    r = call("ping", auth_params=pw(XU, XP + "2"), label="outside schedule")
    record("outside the account's access schedule -> error 50", err(r) == 50, r, "security")
for i in range(3):
    call("ping", auth_params=pw(KU, "wrong"), check_schema=False, label=f"lockout attempt {i + 1}")
r = call("ping", auth_params=pw(KU, KP), label="locked out")
record("Jellyfin's lockout applies: 3 wrong passwords disable the account", err(r) == 50, r, "security")

# ── users & roles ───────────────────────────────────────────────────────────
r = call("getUser", {"username": SU}, label="getUser self")
u = (r or {}).get("user", {})
record("getUser roles follow Jellyfin permissions (non-admin)",
       u.get("adminRole") is False and u.get("streamRole") is True and u.get("downloadRole") is True
       and u.get("shareRole") is True and u.get("scrobblingEnabled") is True, u)
record("getUser folder lists only the accessible music library", len(u.get("folder", [])) == 1, u.get("folder"))
r = call("getUser", {"username": AU}, label="getUser other")
record("getUser for another user as non-admin -> error 50", err(r) == 50, r, "security")
r = call("getUsers", label="getUsers non-admin")
record("getUsers as non-admin -> error 50", err(r) == 50, r, "security")
r = call("getUsers", auth_params=admin(), label="getUsers admin")
us = (r or {}).get("users", {}).get("user", [])
record("getUsers as admin lists every Jellyfin user", sorted(x.get("username") for x in us) == sorted(user_ids)
       and any(x.get("adminRole") for x in us), [(x.get("username"), x.get("adminRole")) for x in us])
r = call("getMusicFolders", auth_params=admin(), check_schema=False, label="admin folders")
admin_folders = (r or {}).get("musicFolders", {}).get("musicFolder", [])
record("the admin sees both music libraries", sorted(f["name"] for f in admin_folders) == ["Music", "Restricted"], admin_folders)
restricted_folder = next((f["id"] for f in admin_folders if f["name"] == "Restricted"), None)

# ── library permissions: the limited user must not reach the Restricted library ──
r = call("search3", {"query": "Secret"}, auth_params=admin(), check_schema=False, label="admin secret search")
sr = (r or {}).get("searchResult3", {})
secret_song = (sr.get("song") or [{}])[0].get("id")
secret_album = (sr.get("album") or [{}])[0].get("id")
record("the admin finds the restricted song and album", bool(secret_song and secret_album), sr)
if secret_song and secret_album and song_ids:
    r = call("search3", {"query": "Secret"}, check_schema=False, label="limited secret search")
    record("restricted items stay out of the limited user's search",
           not any((r or {}).get("searchResult3", {}).get(k) for k in ("song", "album")), r, "security")
    for ep, params in [("getSong", {"id": secret_song}), ("getAlbum", {"id": secret_album}),
                       ("getMusicDirectory", {"id": secret_album}), ("getLyricsBySongId", {"id": secret_song}),
                       ("getSimilarSongs", {"id": secret_song}), ("getAlbumInfo2", {"id": secret_album})]:
        r = call(ep, params, check_schema=False, label=f"restricted {ep}")
        record(f"{ep} of a restricted item -> error 70", err(r) == 70, r, "security")
    for ep, rid in (("stream", secret_song), ("download", secret_song), ("getCoverArt", secret_album)):
        s = requests.get(f"{API}/{ep}", params={**auth(), "id": rid}, timeout=30, allow_redirects=False)
        try:
            code = s.json()["subsonic-response"]["error"]["code"]
        except Exception:
            code = None
        record(f"{ep} of a restricted item -> Subsonic error 70", code == 70, (s.status_code, s.headers.get("content-type"), s.text[:120]), "security")
    call("star", {"id": secret_song}, check_schema=False, label="star restricted")
    r = call("getStarred2", check_schema=False, label="starred after restricted star")
    record("starring a restricted item has no effect",
           not any(x.get("id") == secret_song for x in (r or {}).get("starred2", {}).get("song", [])), r, "security")
    r = call("createPlaylist", [("name", "sneaky"), ("songId", secret_song), ("songId", song_ids[0])], check_schema=False, label="playlist with restricted")
    ents = [x["id"] for x in (r or {}).get("playlist", {}).get("entry", [])]
    record("createPlaylist leaves out restricted songs", ents == [song_ids[0]], ents, "security")
    if ok(r):
        call("deletePlaylist", {"id": r["playlist"]["id"]}, check_schema=False, label="cleanup sneaky")
    r = call("createShare", {"id": secret_song}, check_schema=False, label="share restricted")
    record("createShare of a restricted song -> error 70", err(r) == 70, r, "security")
    if restricted_folder is not None:
        r = call("getAlbumList2", {"type": "newest", "musicFolderId": restricted_folder}, check_schema=False, label="inaccessible folder")
        record("musicFolderId of an inaccessible library returns nothing",
               not (r or {}).get("albumList2", {}).get("album"), r, "security")

# ── playlists across users ──────────────────────────────────────────────────
if song_ids:
    r = call("createPlaylist", [("name", "admins"), ("songId", song_ids[0])], auth_params=admin(), check_schema=False, label="admin playlist")
    apl = (r or {}).get("playlist", {}).get("id")
    if apl:
        for ep, params in (("getPlaylist", {"id": apl}), ("updatePlaylist", {"playlistId": apl, "name": "hijacked"}),
                           ("deletePlaylist", {"id": apl})):
            r = call(ep, params, check_schema=False, label=f"private {ep}")
            record(f"{ep} on another user's private playlist -> error 70", err(r) == 70, r, "security")
        call("updatePlaylist", {"playlistId": apl, "public": "true", "comment": "shared"}, auth_params=admin(), check_schema=False, label="make public")
        r = call("getPlaylists", label="getPlaylists with public")
        pub = next((x for x in (r or {}).get("playlists", {}).get("playlist", []) if x.get("id") == apl), {})
        record("a public playlist is listed with its real owner, read-only for others",
               pub.get("owner") == AU and pub.get("readonly") is True and pub.get("public") is True and pub.get("comment") == "shared", pub)
        for ep, params in (("updatePlaylist", {"playlistId": apl, "name": "hijacked"}), ("deletePlaylist", {"id": apl})):
            r = call(ep, params, check_schema=False, label=f"public {ep}")
            record(f"{ep} on someone else's public playlist -> error 50", err(r) == 50, r, "security")
        call("deletePlaylist", {"id": apl}, auth_params=admin(), check_schema=False, label="admin cleanup")

    # replace-by-playlistId and duplicate entries
    r = call("createPlaylist", [("name", "sem"), ("songId", song_ids[0]), ("songId", song_ids[1])], check_schema=False, label="sem create")
    pl = (r or {}).get("playlist", {}).get("id")
    if pl:
        call("createPlaylist", [("playlistId", pl), ("songId", song_ids[2]), ("songId", song_ids[0])], check_schema=False, label="sem replace")
        got = [x["id"] for x in call("getPlaylist", {"id": pl}, check_schema=False, label="sem after replace").get("playlist", {}).get("entry", [])]
        record("createPlaylist with playlistId replaces the playlist's songs", got == [song_ids[2], song_ids[0]], got)
        call("updatePlaylist", {"playlistId": pl, "songIdToAdd": song_ids[2]}, check_schema=False, label="sem add dup")
        got = [x["id"] for x in call("getPlaylist", {"id": pl}, check_schema=False, label="sem after dup").get("playlist", {}).get("entry", [])]
        dup = tuple(int(x) for x in JF_VERSION.split(".")[:2]) >= (12, 1)
        want = [song_ids[2], song_ids[0], song_ids[2]] if dup else [song_ids[2], song_ids[0]]
        record(f"duplicate playlist entries {'kept (Jellyfin 12.1+)' if dup else 'dropped (Jellyfin 10.11 has none)'}", got == want, got)
        call("deletePlaylist", {"id": pl}, check_schema=False, label="sem cleanup")

# ── shares across users ─────────────────────────────────────────────────────
if song_ids:
    r = call("createShare", {"id": song_ids[0]}, auth_params=admin(), check_schema=False, label="admin share")
    ash = ((r or {}).get("shares", {}).get("share") or [{}])[0].get("id")
    if ash:
        for ep, params in (("updateShare", {"id": ash, "description": "mine now"}), ("deleteShare", {"id": ash})):
            r = call(ep, params, check_schema=False, label=f"foreign {ep}")
            record(f"{ep} on another user's share -> error 70", err(r) == 70, r, "security")
        r = call("getShares", auth_params=admin(), check_schema=False, label="admin shares after")
        mine = next((x for x in (r or {}).get("shares", {}).get("share", []) if x.get("id") == ash), None)
        record("the owner's share is untouched", mine is not None and mine.get("description") != "mine now", mine)
        call("deleteShare", {"id": ash}, auth_params=admin(), check_schema=False, label="admin share cleanup")

# ── a share follows its owner's account ─────────────────────────────────────
HU, HP = creds["HU"], creds["HP"]
hsh = create_share([song_ids[0]], auth_params=pw(HU, HP), check_schema=False, label="createShare as sharer") if song_ids else None
if hsh:
    def through_link(ep, **params):
        """The Subsonic error code of a request made with the share link, or its HTTP status."""
        s = requests.get(f"{API}/{ep}", params={**share_link(hsh), **params}, timeout=30)
        try:
            return s.json()["subsonic-response"]["error"]["code"]
        except (ValueError, KeyError):
            return s.status_code

    set_policy(HU, EnableContentDownloading=False)
    page = share_page(hsh)
    record("owner may not download: the share page offers no ZIP", page.status_code == 200 and "/download?" not in page.text, page.status_code, "security")
    record("... the ZIP -> 403", share_page(hsh, "/download").status_code == 403, kind="security")
    record("... downloading through the link -> error 50", through_link("download", id=song_ids[0]) == 50, kind="security")
    record("... but the link still plays", through_link("stream", id=song_ids[0]) == 200)
    set_policy(HU, EnableContentDownloading=True)

    set_policy(HU, IsDisabled=True)
    record("owner disabled: the share page -> 403", share_page(hsh).status_code == 403, kind="security")
    record("... its M3U -> 403", share_page(hsh, "/m3u").status_code == 403, kind="security")
    record("... and the link can't play -> error 50", through_link("stream", id=song_ids[0]) == 50, kind="security")
    set_policy(HU, IsDisabled=False)

    restricted_lib = next(f["ItemId"] for f in jf("GET", "/Library/VirtualFolders").json() if f["Name"] == "Restricted")
    set_policy(HU, EnableAllFolders=False, EnabledFolders=[restricted_lib])
    page = share_page(hsh)
    record("owner lost access to the shared song's library: the page lists nothing", page.status_code == 200 and page_tracks(page.text) == [],
           (page.status_code, page_tracks(page.text)), "security")
    record("... the M3U lists nothing", m3u_ids(share_page(hsh, "/m3u").text) == [], kind="security")
    record("... the link can't play it -> error 70", through_link("stream", id=song_ids[0]) == 70, kind="security")
    record("... and getShares shows no songs", not shares(auth_params=pw(HU, HP), label="sharer shares").get(hsh["id"], {}).get("entry"), kind="security")
    set_policy(HU, EnableAllFolders=True, EnabledFolders=[])
    record("with access back, the page lists the song again", page_tracks(share_page(hsh).text) == ["Song 1"], page_tracks(share_page(hsh).text))
    call("deleteShare", {"id": hsh["id"]}, auth_params=pw(HU, HP), check_schema=False, label="sharer share cleanup")

# ── data details ────────────────────────────────────────────────────────────
if song_ids:
    iso = lambda s: datetime.strptime(s[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc).timestamp()
    r = call("getSong", {"id": song_ids[0]}, check_schema=False, label="song path")
    record("song path is relative to its library", (r or {}).get("song", {}).get("path") == "Test Artist/Album One (2001)/01 Song 1.flac",
           (r or {}).get("song", {}).get("path"))
    before = time.time()
    call("star", {"id": song_ids[1]}, check_schema=False, label="star for date")
    st = call("getSong", {"id": song_ids[1]}, check_schema=False, label="starred date").get("song", {}).get("starred")
    record("starred is the time it was starred", bool(st) and abs(iso(st) - before) < 120, st)
    call("unstar", {"id": song_ids[1]}, check_schema=False, label="unstar after date")
    call("scrobble", {"id": song_ids[2], "submission": "true", "time": 1700000000000}, check_schema=False, label="scrobble with time")
    s = call("getSong", {"id": song_ids[2]}, check_schema=False, label="after timed scrobble").get("song", {})
    record("scrobble 'time' sets when it was played; counted once", s.get("playCount") == 1 and s.get("played", "").startswith("2023-11-14T22:13:20"),
           (s.get("playCount"), s.get("played")))
    # a batch of plays queued offline: every id counted once, each at its own time
    two = [x["id"] for x in call("getAlbum", {"id": albums["Double Album"]["id"]}, check_schema=False, label="batch songs").get("album", {}).get("song", [])][:2]
    if len(two) < 2:
        two = [x["id"] for x in call("search3", {"query": "Disc"}, check_schema=False, label="batch songs fallback").get("searchResult3", {}).get("song", [])][:2]
    call("scrobble", [("id", two[0]), ("time", "1700000000000"), ("id", two[1]), ("time", "1700000300000"), ("submission", "true")], check_schema=False, label="batch scrobble")
    got = [call("getSong", {"id": i}, check_schema=False, label=f"batch {i}").get("song", {}) for i in two]
    record("a batch scrobble counts every song once at its own time",
           [g.get("playCount") for g in got] == [1, 1] and got[0].get("played", "").startswith("2023-11-14T22:13:20") and got[1].get("played", "").startswith("2023-11-14T22:18:20"),
           [(g.get("playCount"), g.get("played")) for g in got])

# ── lyrics: 01 Song 1.lrc (synced), 02 Song 2.txt (plain), 01 Secret.lrc (Restricted library) ──
if song_ids:
    r = call("getLyricsBySongId", {"id": song_ids[0]}, label="synced lyrics")
    sl = ((r or {}).get("lyricsList", {}).get("structuredLyrics") or [{}])[0]
    record("synced lyrics: the LRC's lines and start times",
           sl.get("synced") is True and [(l.get("start"), l.get("value")) for l in sl.get("line", [])] == [(500, "First line"), (1500, "Second line")], sl)
    record("... with the song's artist and title", (sl.get("displayArtist"), sl.get("displayTitle")) == ("Test Artist", "Song 1"), sl)
    r = call("getLyricsBySongId", {"id": song_ids[1]}, label="plain lyrics")
    sl = ((r or {}).get("lyricsList", {}).get("structuredLyrics") or [{}])[0]
    lines = [l.get("value") for l in sl.get("line", [])]
    while lines and not lines[-1]:  # Jellyfin keeps the file's final newline as an empty last line
        lines.pop()
    record("plain lyrics: unsynced, the file's lines", sl.get("synced") is False and lines == ["Plain first line", "Plain second line"], sl)
    r = call("getLyricsBySongId", {"id": song_ids[2]}, check_schema=False, label="no lyrics")
    record("a song without lyrics has none", ok(r) and not r.get("lyricsList", {}).get("structuredLyrics"), r)
    r = call("getLyrics", {"artist": "Test Artist", "title": "Song 1"})
    record("getLyrics finds a song's lyrics by artist and title",
           (r or {}).get("lyrics") == {"artist": "Test Artist", "title": "Song 1", "value": "First line\nSecond line"}, r)
    r = call("getLyrics", {"artist": "Hidden Artist", "title": "Secret"}, check_schema=False, label="getLyrics restricted")
    record("getLyrics doesn't search libraries the user can't access", ok(r) and not r.get("lyrics", {}).get("value"), r, "security")
    r = call("getLyrics", {"artist": "Hidden Artist", "title": "Secret"}, auth_params=admin(), check_schema=False, label="getLyrics admin")
    record("... while the admin finds them", (r or {}).get("lyrics", {}).get("value") == "Secret words", r)

# ── avatars: the user's Jellyfin picture ────────────────────────────────────
def avatar(**params):
    """(HTTP status, content type, Subsonic error code) of getAvatar, following its redirect."""
    a = requests.get(f"{API}/getAvatar", params={**auth(), **params}, timeout=30)
    try:
        code = a.json()["subsonic-response"]["error"]["code"]
    except (ValueError, KeyError):
        code = None
    return a.status_code, a.headers.get("content-type", ""), code


got = avatar(username=SU)
record("getAvatar of a user without a picture -> error 70", got[2] == 70, got)
picture = subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=orange:s=64x64", "-frames:v", "1",
                          "-f", "image2", "-c:v", "mjpeg", "-"], capture_output=True).stdout
up = jf("POST", "/UserImage", params={"userId": user_ids[SU]}, headers={"Content-Type": "image/jpeg"}, data=base64.b64encode(picture), check=False)
if up.ok:
    got = avatar(username=SU)
    record("getAvatar returns the user's Jellyfin picture", got[0] == 200 and got[1].startswith("image/"), got)
else:
    record("giving the user a Jellyfin picture (for getAvatar)", False, (up.status_code, up.text[:200]))
got = avatar(username="nobody")
record("getAvatar of an unknown user -> error 70", got[2] == 70, got)

# ── base URL (Jellyfin behind a path prefix) ────────────────────────────────
if creds.get("BASEURL") and song_ids and "Album One" in albums:
    B = creds["BASEURL"]
    c = requests.get(f"{API}/getCoverArt", params={**auth(), "id": albums["Album One"]["id"], "size": 64}, allow_redirects=False, timeout=30)
    loc = c.headers.get("location", "")
    record("getCoverArt redirect keeps the base URL and the size", c.status_code in (301, 302, 307) and loc.startswith(f"{B}/Items/") and "maxWidth=64" in loc, (c.status_code, loc))
    r = call("createShare", {"id": song_ids[0]}, check_schema=False, label="base share")
    surl = ((r or {}).get("shares", {}).get("share") or [{}])[0].get("url", "")
    record("share URL includes the base URL", f"{B}/opensubsonic/share/" in surl, surl)
    if surl:
        page = requests.get(surl, timeout=30).text
        record("share page links include the base URL", f'href="{B}/opensubsonic/share/' in page and f"{B}/opensubsonic/rest/stream" in page, page[:200])
    a = requests.get(f"{API}/getAvatar", params={**auth(), "username": SU}, allow_redirects=False, timeout=30)
    record("getAvatar redirect keeps the base URL", a.status_code in (301, 302, 307) and a.headers.get("location", "").startswith(f"{B}/"),
           (a.status_code, a.headers.get("location")))

# ── misc ─────────────────────────────────────────────────────────────────────
call("getUser", {"username": SU})
call("getScanStatus")
call("getNowPlaying")
if test_artist:
    call("getArtistInfo", {"id": test_artist["id"]})
    call("getArtistInfo2", {"id": test_artist["id"]})
    call("getTopSongs", {"artist": "Test Artist"})
if song_ids:
    call("getSimilarSongs", {"id": test_artist["id"] if test_artist else song_ids[0]})
    call("getSimilarSongs2", {"id": test_artist["id"] if test_artist else song_ids[0]})
    call("getLyricsBySongId", {"id": song_ids[0]})
if "Album One" in albums:
    call("getAlbumInfo", {"id": albums["Album One"]["id"]})
    call("getAlbumInfo2", {"id": albums["Album One"]["id"]})

# Artist and album details come from Jellyfin's own metadata (no Last.fm)
if test_artist and "Album One" in albums:
    def set_overview(item_id, text):
        item = jf("GET", f"/Items/{item_id}", params={"userId": user_ids[AU]}).json()
        jf("POST", f"/Items/{item_id}", json={**item, "Overview": text})
    set_overview(test_artist["id"], "Biography from Jellyfin.")
    set_overview(albums["Album One"]["id"], "Notes from Jellyfin.")
    r = call("getArtistInfo2", {"id": test_artist["id"]}, label="artist info from Jellyfin")
    info = (r or {}).get("artistInfo2", {})
    record("getArtistInfo2 biography is the artist's Jellyfin overview", info.get("biography") == "Biography from Jellyfin.", info)
    record("similar artists are artists from getArtists", all(x.get("id") in artist_ids for x in info.get("similarArtist", [])), info.get("similarArtist"))
    record("artist info has no Last.fm link", "lastFmUrl" not in info, info)
    x = requests.get(f"{API}/getArtistInfo", params={**auth(f="xml"), "id": test_artist["id"]}, timeout=30)
    try:
        bio = ET.fromstring(x.content).find("{http://subsonic.org/restapi}artistInfo/{http://subsonic.org/restapi}biography").text
    except (ET.ParseError, AttributeError) as e:
        bio = repr(e)
    record("getArtistInfo (XML) has the biography", bio == "Biography from Jellyfin.", bio)
    r = call("getAlbumInfo2", {"id": albums["Album One"]["id"]}, label="album info from Jellyfin")
    record("getAlbumInfo2 notes are the album's Jellyfin overview", (r or {}).get("albumInfo", {}).get("notes") == "Notes from Jellyfin.", r)
r = call("getAlbum", {"id": secrets.token_hex(16)}, check_schema=False, label="getAlbum-missing")
record("getAlbum unknown id -> error 70", err(r) == 70, r)
r = call("getAlbum", check_schema=False, label="getAlbum-noid")
record("getAlbum without id -> error 10", err(r) == 10, r)

# ── XML carries what JSON does ──────────────────────────────────────────────
# Subsonic's JSON is its XML read this way: attributes and child elements become keys, repeated
# elements arrays, and an element's text "value" (an element with nothing but text is that text).
# Only the JSON is checked against the spec's schemas, so the XML (built separately) must match it.
NS = "{http://subsonic.org/restapi}"
VOLATILE = {"lastModified", "minutesAgo", "similarArtist"}  # change between the two requests (Jellyfin's similar items do too)


def from_xml(el):
    d = {("@value" if k == "value" else k): v for k, v in el.attrib.items()}  # text goes in the element, not a value attribute
    for child in el:
        tag = child.tag.removeprefix(NS)
        if tag in d and not isinstance(d[tag], list):  # an attribute and elements of the same name: JSON can't have both
            d["@" + tag] = d.pop(tag)
        d.setdefault(tag, []).append(from_xml(child))
    if not d:
        return el.text or ""
    if el.text:
        d["value"] = el.text
    return d


def as_text(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, dict):
        return {k: as_text(x) for k, x in v.items()}
    if isinstance(v, list):
        return [as_text(x) for x in v]
    return str(v)


def differences(j, x, path=""):
    if isinstance(x, list) and not isinstance(j, list):  # XML can't tell one child from a list of one
        j = [j]
    if isinstance(j, dict) and isinstance(x, dict):
        out = []
        for k in sorted((set(j) | set(x)) - VOLATILE):
            a, b = j.get(k), x.get(k)
            if a in (None, "", [], {}) and b in (None, "", [], {}):
                continue
            if a is None or b is None:
                out.append(f"{path}/{k} only in {'XML' if a is None else 'JSON'}: {str(b if a is None else a)[:80]}")
            else:
                out += differences(a, b, f"{path}/{k}")
        return out
    if isinstance(j, list) and isinstance(x, list):
        if len(j) != len(x):
            return [f"{path}: {len(j)} in JSON, {len(x)} in XML"]
        return [d for i, (a, b) in enumerate(zip(j, x)) for d in differences(a, b, f"{path}[{i}]")]
    try:
        if j == x or float(j) == float(x):
            return []
    except (TypeError, ValueError):
        pass
    return [f"{path}: JSON {str(j)[:60]!r}, XML {str(x)[:60]!r}"]


def xml_matches_json(label, endpoint, params=None, auth_params=None):
    base = list((auth_params or auth()).items())
    params = list((params or {}).items())
    j = requests.get(f"{API}/{endpoint}", params=base + params, timeout=30)
    x = requests.get(f"{API}/{endpoint}", params=[(k, "xml" if k == "f" else v) for k, v in base] + params, timeout=30)
    try:
        d = differences(as_text(j.json()["subsonic-response"]), from_xml(ET.fromstring(x.content)))
    except Exception as e:  # a response that isn't JSON or XML fails this check, not the suite
        d = [f"{e!r}: {x.text[:120]}"]
    record(f"XML matches JSON: {label}", not d, "; ".join(d[:4]), "spec")


if song_ids and test_artist and {"Album One", "Double Album"} <= set(albums):
    # starred items, a playlist and a share for the lists to show
    call("star", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"]), ("artistId", test_artist["id"])], check_schema=False, label="xml: star")
    xpl = (call("createPlaylist", [("name", "XML"), ("songId", song_ids[0]), ("songId", song_ids[1])], check_schema=False, label="xml: playlist")
           or {}).get("playlist", {}).get("id")
    xsh = create_share([albums["Album One"]["id"]], check_schema=False, label="xml: share", description="for XML")
    for label, endpoint, params, *who in [
        ("ping", "ping", {}), ("getLicense", "getLicense", {}), ("getOpenSubsonicExtensions", "getOpenSubsonicExtensions", {}),
        ("getMusicFolders", "getMusicFolders", {}), ("getIndexes", "getIndexes", {}), ("getArtists", "getArtists", {}),
        ("getArtist", "getArtist", {"id": test_artist["id"]}), ("getMusicDirectory of an artist", "getMusicDirectory", {"id": test_artist["id"]}),
        *[(f"getAlbum({name})", "getAlbum", {"id": a["id"]}) for name, a in albums.items()],
        ("getMusicDirectory of an album", "getMusicDirectory", {"id": albums["Double Album"]["id"]}),
        ("getSong", "getSong", {"id": song_ids[0]}),
        ("getAlbumList", "getAlbumList", {"type": "alphabeticalByName"}), ("getAlbumList2", "getAlbumList2", {"type": "alphabeticalByName"}),
        ("getGenres", "getGenres", {}), ("getSongsByGenre", "getSongsByGenre", {"genre": "Jazz"}),
        ("search2", "search2", {"query": "Song"}), ("search3", "search3", {"query": ""}),
        ("getStarred", "getStarred", {}), ("getStarred2", "getStarred2", {}),
        ("getPlaylists", "getPlaylists", {}), ("getPlaylist", "getPlaylist", {"id": xpl}), ("getPlayQueue", "getPlayQueue", {}),
        ("getShares", "getShares", {}), ("getUser", "getUser", {"username": SU}), ("getUsers", "getUsers", {}, admin()),
        ("getScanStatus", "getScanStatus", {}), ("getNowPlaying", "getNowPlaying", {}),
        ("getArtistInfo", "getArtistInfo", {"id": test_artist["id"]}), ("getArtistInfo2", "getArtistInfo2", {"id": test_artist["id"]}),
        ("getAlbumInfo", "getAlbumInfo", {"id": albums["Album One"]["id"]}), ("getAlbumInfo2", "getAlbumInfo2", {"id": albums["Album One"]["id"]}),
        ("getTopSongs", "getTopSongs", {"artist": "Test Artist"}),
        ("getLyrics", "getLyrics", {"artist": "Test Artist", "title": "Song 1"}),
        ("getLyricsBySongId, synced", "getLyricsBySongId", {"id": song_ids[0]}),
        ("getLyricsBySongId, plain", "getLyricsBySongId", {"id": song_ids[1]}),
        ("an error", "getAlbum", {"id": secrets.token_hex(16)}),
    ]:
        xml_matches_json(label, endpoint, params, *who)
    call("unstar", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"]), ("artistId", test_artist["id"])], check_schema=False, label="xml: unstar")
    if xpl:
        call("deletePlaylist", {"id": xpl}, check_schema=False, label="xml: playlist cleanup")
    if xsh:
        call("deleteShare", {"id": xsh["id"]}, check_schema=False, label="xml: share cleanup")

# ── report ───────────────────────────────────────────────────────────────────
pathlib.Path(sys.argv[3]).write_text(json.dumps({"results": results, "raw": raw}, indent=1, ensure_ascii=False))
fails = [r for r in results if not r["ok"]]
print(f"{len(results)} checks, {len(results) - len(fails)} passed, {len(fails)} failed")
for r in fails:
    print(f"  FAIL [{r['kind']}] {r['check']}\n        {r['detail']}")
sys.exit(1 if fails else 0)
