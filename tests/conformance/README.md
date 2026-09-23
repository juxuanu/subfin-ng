# OpenSubsonic conformance suite

End-to-end checks of the plugin against a real Jellyfin server and the
[OpenSubsonic](https://opensubsonic.netlify.app/) specification.

```sh
tests/conformance/run.sh                         # Jellyfin 12.1, served at /
tests/conformance/run.sh --base-url /jellyfin    # served under a path prefix (reverse proxy)
tests/conformance/run.sh --image docker.io/jellyfin/jellyfin:<tag> --keep
```

`run.sh` builds the plugin, generates a small tagged music library, starts a
throwaway Jellyfin container on `127.0.0.1:18096` with the plugin installed,
and runs `conformance.py`. It exits non-zero if any check fails and writes
every check and raw response to `.work/report.json`.

Requirements: `podman`, `ffmpeg`, `jq`, `curl`, `git`, the .NET 10 SDK and
[`uv`](https://docs.astral.sh/uv/) (it installs the Python dependencies).

## What is checked

- **Schemas** – every JSON response, errors included, is validated against the
  OpenSubsonic OpenAPI schemas (`openapi/` of the spec repository, pinned in `run.sh`).
- **Behaviour** – results are compared with the generated library
  (`make-media.sh`): a two-disc album in `CD 1`/`CD 2` folders, a various-artists
  compilation, non-ASCII names, FLAC/MP3/M4A files, and a movie library whose
  genre must not leak into music endpoints.
- **Access control** – `up.sh` creates an admin and a user limited to one of two
  music libraries. The suite checks that the limited user can't reach the other
  library by id, can't read or change other users' private playlists and shares,
  and that share links only reach the shared songs.
- **Authentication** – token, password, `enc:` password and API-key logins,
  including the error codes for conflicting or invalid credentials.

Online metadata providers are disabled, so results depend only on the files.

## Binary compatibility

`abicheck.cs` checks a compiled plugin against a Jellyfin installation: every
type and member it references must exist there with the same signature (a
missing one fails at runtime with `MissingMethodException`).

```sh
dotnet run tests/conformance/abicheck.cs -- \
  Jellyfin.Plugin.Subsonic.dll /path/to/jellyfin /path/to/dotnet/shared/Microsoft.NETCore.App/10.0.x \
  /path/to/dotnet/shared/Microsoft.AspNetCore.App/10.0.x
```
