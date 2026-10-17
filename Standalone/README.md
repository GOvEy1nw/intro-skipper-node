# IntroSkipper-Node

Introduction, credits, recap, preview and commercial analysis through a Windows tray app or command-line worker. Built-in methods reuse the Intro Skipper engine; text-aware credits use a persistent EAST helper. CPU processing is the default. Optional GPU use accelerates video decoding and, for text-aware credits, OpenCL inference. Resizing and colour conversion remain on CPU. Connected operation uses the paired protocol-3 bridge included in this source tree.

The EAST manifest is experimental. Credits proposals require review by default; a connected node can explicitly opt into automatic imports from its pinned profile. This source export includes no model weights, media, credentials, state, reports or labels. Private all-in-one packaging requires builder-supplied weights, manifest, FFmpeg/FFprobe and Python/OpenCV inputs.

This repository ships source only. A private all-in-one `IntroSkipper-Node.exe` can be produced with builder-supplied tools and weights. Its connection page and tray offer only name, server address, API key, **Start node** and **Stop node**; Intro Skipper's **Nodes** tab centrally manages libraries, all scan types, credits method, GPU, workers and path variables.

Desktop settings default to `%LOCALAPPDATA%\SegmentScanner\worker.json`; `--config FILE` selects a different settings file. The tools use .NET's versioned extraction cache, separate from settings and results. See [desktop operations](docs/operations.md#windows-tray-app) for state locations, status colours and local build instructions. The existing CLI commands and packages remain supported.

AV1 analysis skips generated film grain on CPU and GPU without changing media files. The v3 decoder policy changes credits profile pins and AV1 cache identity. Stop the worker, save and register after upgrading, then queue fresh work. Existing non-AV1 CPU evidence remains reusable.

Credits use the reported ordered group, text-position and length gates, with a square-stretched EAST input. A scanner safeguard rejects brief, sparse scene text before joining groups. Gaps up to ten seconds join; longer gaps require measured flatness or sufficient text. Flat final tails can reach the visual EOF. Update the worker, manifest and Python helper together: the `bgr-byte-square-v3` capability invalidates earlier evidence caches. Offline proposals require review. Connected automatic imports accept only complete, recognized results after server admission; detector calibration status remains unchanged. See [evaluation](docs/evaluation.md) for the experimental rules and their limits.

From the repository root, build a Windows package:

```powershell
dotnet restore Standalone/src/SegmentScanner.Cli/SegmentScanner.Cli.csproj --locked-mode -p:StandaloneBuild=true
dotnet publish Standalone/src/SegmentScanner.Cli/SegmentScanner.Cli.csproj -c Release --no-restore -p:StandaloneBuild=true -p:SkipWebBuild=true -o .segmentscanner/packages/windows
```

This package requires the .NET 10 ASP.NET Core runtime, FFmpeg with Chromaprint, FFprobe, and a Python environment installed from `inference/requirements.lock`. Configure their paths in a copy of `samples/config.windows.json`, including a model manifest that resolves to your own weights. Model paths are relative to their manifest; use an absolute weight path when moving a manifest into a package or state directory.

```powershell
segmentscanner doctor --config worker.json
segmentscanner scan --config worker.json --manifest media.json --modes intro,credits --dry-run --out results.json
segmentscanner report --results results.json --out review.html
segmentscanner review --config worker.json --results results.json --decisions review.decisions.json --apply --out accepted.json
segmentscanner export --config worker.json --results accepted.json --format json,matroska-xml --dry-run
```

Review the HTML and edit its decisions companion before applying acceptance. Enable exports deliberately and use `export --apply` after inspecting the dry run. Existing unfamiliar or edited files are preserved; media bytes are never rewritten. A connected proposal requires server admission before export.

Only one process may use a state directory at a time. Stop a running worker before using `scan`, `review`, `export`, or `sync` against that same directory. `report` reads a result file independently.

See [operations](docs/operations.md), [compatibility and rollback](docs/compatibility-and-rollback.md), [evaluation](docs/evaluation.md), [Plex marker reference export](docs/plex-reference-export.md), and [validation evidence](docs/VALIDATION.md). Linux container configuration is in [packaging/linux](packaging/linux/compose.cpu.example.yml). The bridge must be built and installed separately; an unmodified Intro Skipper installation does not expose these endpoints.

For connected setup, follow [Jellyfin setup](docs/jellyfin-validation.md). Connect the node, then configure and enable it in Intro Skipper's **Nodes** tab. The default local node can be disabled to leave all analysis to the remote node. Shared analyzer settings are sent after saves and recovered on reconnect, while running jobs keep immutable settings. New remote policies default to 2 workers and CPU; existing saved identities, credentials and selections are migrated. This delivery targets the local node plus one remote node. Connected bundles remain available for review when needed, and automatic imports are a server-owned per-node choice.
