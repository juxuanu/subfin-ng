# Performance budgets

Times the most used endpoints on a large synthetic library (1500 artists, 3000 albums) in a throwaway
Jellyfin (podman), and fails when one is over its budget in `bench.py`.

```sh
tests/bench/run.sh
```

Needs `podman`, `ffmpeg`, `jq`, `curl`, the .NET 10 SDK and `uv`. Timings go to `.work/bench.json`.
