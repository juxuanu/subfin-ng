# What the conformance suite checks

`run.sh` builds the plugin, generates a small tagged music library, starts a
throwaway Jellyfin container on `127.0.0.1:18096` with the plugin installed,
and runs `conformance.py`. It exits non-zero if any check fails and writes
every check and raw response to `.work/report.json`. Online metadata providers
are disabled, so results depend only on the files.

CI (`.github/workflows/test.yml`) runs it twice on every push, pull request and
release: served at `/`, and under `/jellyfin` with `--ui`.

- **Schemas** – every JSON response, errors included, is validated against the
  OpenSubsonic OpenAPI schemas (`openapi/` of the spec repository, pinned in `run.sh`).
- **XML** – the XML of every read-only endpoint must carry what its JSON does, read
  the way Subsonic's JSON is derived from its XML (attributes and child elements
  become keys, repeated elements arrays, element text `value`). The XML is built
  separately from the JSON, and only the JSON has schemas.
- **Behaviour** – results are compared with the generated library
  (`make-media.sh`): a two-disc album in `CD 1`/`CD 2` folders with the same file
  names on both discs, a various-artists compilation, non-ASCII names, FLAC/MP3/M4A
  files, synced (LRC) and plain lyrics files, and a movie library whose genre must
  not leak into music endpoints. Transcoded streams are measured with `ffprobe`
  (the bitrate asked for, `timeOffset`).
- **Access control** – `up.sh` creates an admin and a user limited to one of two
  music libraries. The suite checks that the limited user can't reach the other
  library by id, and can't read or change other users' private playlists and shares.
- **Authentication** – most checks sign in with token login, the way Navidrome
  prefers apps to: a token from an OpenSubsonic password generated through the
  plugin page's API. Others use plain passwords (the OpenSubsonic one, the Jellyfin
  one, `enc:`, a form `POST`) and check the error codes for token login without an
  OpenSubsonic password, API keys and conflicting credentials. The password API is
  checked too: admins only, and regenerating or removing a password takes effect at
  once. So are Jellyfin's account rules: a disabled account, a password change or a
  disabled account while signed in, an access schedule and the lockout.
- **Sharing** – shares of a song, an album, a playlist, an artist and several
  items at once: what `createShare` and `getShares` report, and the player page,
  M3U, ZIP and stream/download behind each link. The owner's changes (description
  or expiry alone, `expires=0`, deletion), a wrong secret, expiry, sharing turned
  off in the plugin settings, and the owner's account: no download permission,
  disabled, or no longer allowed into the shared songs' library.
- **Browser** (`--ui`) – `ui.py` signs in to Jellyfin's web UI in a headless Firefox
  and drives the plugin settings page: generating, regenerating and removing an
  OpenSubsonic password, and saving the settings. It also opens an album share's
  public page and plays its songs. Playwright downloads the browser on first use;
  screenshots go to `.work/ui-*.png`.

## Binary compatibility

`abicheck.cs` checks a compiled plugin against a Jellyfin installation: every
type and member it references must exist there with the same signature (a
missing one fails at runtime with `MissingMethodException`).

```sh
dotnet run tests/conformance/abicheck.cs -- \
  Jellyfin.Plugin.Subsonic.dll /path/to/jellyfin /path/to/dotnet/shared/Microsoft.NETCore.App/10.0.x \
  /path/to/dotnet/shared/Microsoft.AspNetCore.App/10.0.x
```
