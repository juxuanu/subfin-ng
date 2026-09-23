# /// script
# requires-python = ">=3.12"
# dependencies = ["jsonschema>=4.23", "referencing>=0.35", "requests>=2.32"]
# ///
"""OpenSubsonic conformance + behaviour checks for a Subfin-enabled Jellyfin.

usage: uv run conformance.py <creds.env> <openapi-dir> <out.json>
Every JSON response is validated against the endpoint's schema in the OpenSubsonic OpenAPI spec;
behavioural checks compare results with the generated test library (make-media.sh). Run via run.sh.
"""
import hashlib, json, pathlib, secrets, sys, xml.etree.ElementTree as ET

import requests
from jsonschema import Draft7Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT7

creds = dict(l.split("=", 1) for l in pathlib.Path(sys.argv[1]).read_text().split())
SPEC = pathlib.Path(sys.argv[2]).resolve()
URL, SU, SP, AU, AP = creds["URL"], creds["SU"], creds["SP"], creds["AU"], creds["AP"]
JF_VERSION = creds["JF_VERSION"]

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
        _validators[endpoint] = Draft7Validator({"$ref": uri}, registry=registry)
    return _validators[endpoint]


results: list[dict] = []
raw: dict[str, object] = {}


def record(name, ok, detail="", kind="behaviour"):
    results.append({"check": name, "ok": bool(ok), "kind": kind, "detail": str(detail)[:400]})


def auth(**over):
    s = secrets.token_hex(6)
    a = {"u": SU, "t": hashlib.md5((SP + s).encode()).hexdigest(), "s": s, "v": "1.16.1", "c": "conformance", "f": "json"}
    a.update(over)
    return {k: v for k, v in a.items() if v is not None}


def call(endpoint, params=None, auth_params=None, raw_resp=False, check_schema=True, label=None):
    params = list((params or {}).items()) if isinstance(params, dict) else list(params or [])
    r = requests.get(f"{URL}/rest/{endpoint}", params=list((auth_params or auth()).items()) + params, timeout=30)
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
record("ping (token auth) ok", ok(r), r)
for k in ("type", "serverVersion", "openSubsonic"):
    record(f"envelope has OpenSubsonic field '{k}'", k in (r or {}), r, "spec")
record("ping (plain p=) ok", ok(call("ping", auth_params=auth(t=None, s=None, p=SP), check_schema=False)))
record("ping (p=enc:hex) ok", ok(call("ping", auth_params=auth(t=None, s=None, p="enc:" + SP.encode().hex()), check_schema=False)))
r = call("ping", auth_params=auth(t=None, s=None, p="wrong-password"), check_schema=False, label="ping-wrongpw")
record("ping with WRONG password -> error 40", err(r) == 40, r, "spec")
r = call("getLicense", auth_params=auth(t=None, s=None, p="wrong-password"), check_schema=False, label="license-wrongpw")
record("getLicense with WRONG password -> error 40", err(r) == 40, r, "spec")
r = call("getAlbumList2", {"type": "newest"}, auth_params=auth(t=None, s=None, p="wrong-password"), check_schema=False, label="albums-wrongpw")
record("authenticated endpoint with wrong password -> error 40", err(r) == 40, r)
r = call("getOpenSubsonicExtensions", auth_params={"f": "json"})
record("getOpenSubsonicExtensions is public", ok(r), r)
names = [e["name"] for e in (r or {}).get("openSubsonicExtensions", [])]
known = {p.stem for p in (SPEC.parent / "content/en/docs/Extensions").glob("*.md")} - {"_index", "template"}
known = {k[0].lower() + k[1:] for k in known} | {"apiKeyAuthentication"}
record("advertised extensions are real spec extensions", set(names) <= known, f"advertised={names}", "spec")
r = call("ping", auth_params={"apiKey": SP, "v": "1.16.1", "c": "conformance", "f": "json"}, check_schema=False, label="apikey-only")
record("apiKey without u (apiKeyAuthentication semantics)", ok(r) or err(r) == 42, r, "spec")

x = requests.get(f"{URL}/rest/ping", params={**auth(), "f": "xml"}, timeout=30)
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
    s = requests.get(f"{URL}/rest/stream", params={**auth(), "id": song_ids[0]}, headers={"Range": "bytes=0-99"}, timeout=30)
    record("stream honours Range (206, 100 bytes) for seeking", s.status_code == 206 and len(s.content) == 100,
           (s.status_code, s.headers.get("content-range"), len(s.content)))
    s = call("stream", [("id", song_ids[0]), ("format", "mp3"), ("maxBitRate", "128")], raw_resp=True)
    record("stream transcoded format=mp3: 200 audio/mpeg", s.status_code == 200 and s.headers.get("content-type", "").startswith("audio/mpeg"),
           (s.status_code, s.headers.get("content-type"), len(s.content)))
    d = call("download", {"id": song_ids[0]}, raw_resp=True)
    record("download: 200 with body", d.status_code == 200 and len(d.content) > 1000, (d.status_code, d.headers.get("content-type"), len(d.content)))
for name in ("Album One", "Double Album"):
    if name in albums:
        c = call("getCoverArt", {"id": albums[name].get("coverArt") or albums[name]["id"], "size": 64}, raw_resp=True)
        record(f"getCoverArt({name}): 200 image/*", c.status_code == 200 and c.headers.get("content-type", "").startswith("image/"),
               (c.status_code, c.headers.get("content-type")))

# ── user data ────────────────────────────────────────────────────────────────
if song_ids and "Album One" in albums:
    record("star song+album", ok(call("star", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"])])))
    r = call("getStarred2")
    st = (r or {}).get("starred2", {})
    record("getStarred2 shows starred song and album", len(st.get("song", [])) == 1 and len(st.get("album", [])) == 1,
           (len(st.get("song", [])), len(st.get("album", []))))
    call("getStarred")
    record("unstar", ok(call("unstar", [("id", song_ids[0]), ("albumId", albums["Album One"]["id"])])))
    record("setRating 4", ok(call("setRating", {"id": song_ids[0], "rating": 4})))
    r = call("getSong", {"id": song_ids[0]}, check_schema=False)
    record("getSong reflects userRating 4", (r or {}).get("song", {}).get("userRating") == 4, (r or {}).get("song", {}).get("userRating"))
    record("scrobble submission=true", ok(call("scrobble", {"id": song_ids[0], "submission": "true"})))
    r = call("getSong", {"id": song_ids[0]}, check_schema=False)
    record("getSong playCount incremented to 1", (r or {}).get("song", {}).get("playCount") == 1, (r or {}).get("song", {}).get("playCount"))
    call("scrobble", {"id": song_ids[1], "submission": "false"}, check_schema=False, label="now playing")
    r = call("getNowPlaying", label="getNowPlaying while playing")
    np = (r or {}).get("nowPlaying", {}).get("entry", [])
    record("getNowPlaying lists the song being played, with its user and player",
           any(e.get("id") == song_ids[1] and e.get("username") == SU and isinstance(e.get("playerId"), int) for e in np), np)

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

# ── share links must only reach the shared items ────────────────────────────
if song_ids:
    r = call("createShare", {"id": song_ids[0], "description": "conformance"}, check_schema=False, label="createShare")
    share = ((r or {}).get("shares", {}).get("share") or [{}])[0]
    secret = share.get("url", "").partition("secret=")[2]
    if secret:
        from urllib.parse import unquote
        sa = {"u": f"share_{share['id']}", "p": unquote(secret), "v": "1.16.1", "c": "conformance", "f": "json"}
        s = requests.get(f"{URL}/rest/stream", params={**sa, "id": song_ids[0]}, timeout=30)
        record("share link: stream shared song -> 200", s.status_code == 200, s.status_code)
        s = requests.get(f"{URL}/rest/download", params={**sa, "id": song_ids[1]}, timeout=30)
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
    else:
        record("createShare returned a share URL with secret", False, r)

# ── API keys (apiKeyAuthentication) ─────────────────────────────────────────
KEY = {"apiKey": SP, "v": "1.16.1", "c": "conformance", "f": "json"}
r = call("ping", auth_params=KEY, label="apikey-ping")
record("apiKey alone authenticates", ok(r), r, "spec")
r = call("getUser", {"username": SU}, auth_params=KEY, check_schema=False, label="apikey-user")
record("apiKey resolves to its device's user", (r or {}).get("user", {}).get("username") == SU, r)
r = call("ping", auth_params={**KEY, "u": SU}, label="apikey+u")
record("apiKey together with u -> error 43", err(r) == 43, r, "spec")
r = call("ping", auth_params={**KEY, "apiKey": "not-a-key"}, label="apikey-bad")
record("invalid apiKey -> error 44 with a helpUrl", err(r) == 44 and (r or {}).get("error", {}).get("helpUrl", "").endswith("/subfin/"), r, "spec")
r = call("ping", auth_params=auth(p=SP), label="p+t")
record("password and token together -> error 43", err(r) == 43, r, "spec")
r = call("getAlbum", {"id": secrets.token_hex(16)}, label="error envelope")  # schema-checks a failure response

# ── users & roles ───────────────────────────────────────────────────────────
def admin(**over):
    s = secrets.token_hex(6)
    a = {"u": AU, "t": hashlib.md5((AP + s).encode()).hexdigest(), "s": s, "v": "1.16.1", "c": "conformance", "f": "json"}
    a.update(over)
    return {k: v for k, v in a.items() if v is not None}

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
record("getUsers as admin lists every user", sorted(x.get("username") for x in us) == sorted([AU, SU])
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
        s = requests.get(f"{URL}/rest/{ep}", params={**auth(), "id": rid}, timeout=30, allow_redirects=False)
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

# ── data details ────────────────────────────────────────────────────────────
if song_ids:
    import time
    from datetime import datetime, timezone
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

# ── base URL (Jellyfin behind a path prefix) ────────────────────────────────
if creds.get("BASEURL") and song_ids and "Album One" in albums:
    B = creds["BASEURL"]
    c = requests.get(f"{URL}/rest/getCoverArt", params={**auth(), "id": albums["Album One"]["id"], "size": 64}, allow_redirects=False, timeout=30)
    loc = c.headers.get("location", "")
    record("getCoverArt redirect keeps the base URL and the size", c.status_code in (301, 302, 307) and loc.startswith(f"{B}/Items/") and "maxWidth=64" in loc, (c.status_code, loc))
    r = call("createShare", {"id": song_ids[0]}, check_schema=False, label="base share")
    surl = ((r or {}).get("shares", {}).get("share") or [{}])[0].get("url", "")
    record("share URL includes the base URL", f"{B}/subfin/share/" in surl, surl)
    if surl:
        page = requests.get(surl, timeout=30).text
        record("share page links include the base URL", f'href="{B}/subfin/share/' in page and f"{B}/rest/stream" in page, page[:200])
    idx = requests.get(f"{URL}/subfin/", timeout=30).text.replace("\\u002F", "/")
    record("device page is told the base URL", f"const BASE = '{B}'" in idx, idx[:120])

# ── misc ─────────────────────────────────────────────────────────────────────
call("getUser", {"username": SU})
call("getScanStatus")
call("getNowPlaying")
if test_artist:
    call("getArtistInfo2", {"id": test_artist["id"]})
    call("getTopSongs", {"artist": "Test Artist"})
if song_ids:
    call("getSimilarSongs2", {"id": test_artist["id"] if test_artist else song_ids[0]})
    call("getLyricsBySongId", {"id": song_ids[0]})
if "Album One" in albums:
    call("getAlbumInfo2", {"id": albums["Album One"]["id"]})
r = call("getAlbum", {"id": secrets.token_hex(16)}, check_schema=False, label="getAlbum-missing")
record("getAlbum unknown id -> error 70", err(r) == 70, r)
r = call("getAlbum", check_schema=False, label="getAlbum-noid")
record("getAlbum without id -> error 10", err(r) == 10, r)

# ── report ───────────────────────────────────────────────────────────────────
pathlib.Path(sys.argv[3]).write_text(json.dumps({"results": results, "raw": raw}, indent=1, ensure_ascii=False))
fails = [r for r in results if not r["ok"]]
print(f"{len(results)} checks, {len(results) - len(fails)} passed, {len(fails)} failed")
for r in fails:
    print(f"  FAIL [{r['kind']}] {r['check']}\n        {r['detail']}")
sys.exit(1 if fails else 0)
