# OpenSubsonic conformance suite

Runs the plugin in a throwaway Jellyfin (podman) and checks its API against the
[OpenSubsonic](https://opensubsonic.netlify.app/) spec.

```sh
tests/conformance/run.sh                         # served at /
tests/conformance/run.sh --base-url /jellyfin    # under a path prefix
tests/conformance/run.sh --ui                    # also the settings page and a share page, in a browser
tests/conformance/run.sh --image docker.io/jellyfin/jellyfin:<tag> --keep
```

Needs `podman`, `ffmpeg`, `jq`, `curl`, `git`, the .NET 10 SDK and [`uv`](https://docs.astral.sh/uv/).
Results go to `.work/report.json`. What is checked: [CHECKS.md](CHECKS.md).
