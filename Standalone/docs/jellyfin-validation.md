# Connected Jellyfin setup

Use the matching IntroSkipper-Node bridge and worker for Jellyfin 12.2.0 or newer. The current upstream-integrated bridge cannot load on 12.1; keep its earlier matching build until the server is upgraded. New work uses protocol 3 at `/IntroSkipper/Remote/v3`; upgrade both components together. Existing accepted markers and CPU detector identities are retained.

## Upgrade the bridge

If a manual scan never reaches the connected worker and Jellyfin logs `Invalid file-version token or runtime`, install the matching signed-file-version fix on both the bridge and worker. Media with subtitle sidecars can have a negative file-version hash; the fixed contract accepts that identity without changing revision counters or existing markers. Stop the old settings/worker process, install both bridge DLLs and restart Jellyfin, then launch the updated worker with the same configuration and retry the manual scan. No cache reset or node reconfiguration is needed.

Stop the worker and finish any outstanding reviews before upgrading. Stop the Jellyfin container and make a consistent backup of its configuration and plugin database, including SQLite journals/WAL. Replace both `IntroSkipper.dll` and `SegmentScanner.Contracts.dll` in the active Intro Skipper folder, then restart the container. The normal embedded dashboard is included; the new migration adds node records and job ownership without changing accepted markers.

After restart, `/api-docs/openapi.json` must include `/IntroSkipper/Remote/v3/capabilities`, `/libraries`, `/nodes` and `/nodes/{nodeId}/policy`. Keep the old package and consistent database backup together for rollback. The migration adds separate nullable policy/runtime columns and preserves legacy declarations and receipts. Do not downgrade binaries against a newer database without checking compatibility.

## Configure the standalone node

Open the worker's local settings page with your existing configuration:

```powershell
& $scannerExe settings --config $workerConfig
```

The browser opens on this PC. `--no-browser` prints the same local link for manual opening. The page binds only to `127.0.0.1` on a temporary port, checks the request host/origin and requires a fresh session token for its API. It cannot be used from another PC. Keep the terminal open while using the page.

1. Enter the Jellyfin server address and an elevated Dashboard API key. A key already in the configured worker environment variable can also be used. On Windows, a successfully connected key is encrypted for the current Windows account and remembered in local state until **Remove remembered key** is used; reopening the app or CLI does not require entering it again. It is never returned by the page API or written into the settings JSON. Other platforms retain session-only entry and environment credentials. HTTP requires an explicit checkbox; HTTPS certificate validation remains enabled.
2. Give the node a friendly name, connect and start the scanner. It reports its hardware and appears in the plugin's Nodes tab, initially disabled. The local page retains only connection controls and Start/Stop.
3. Open Intro Skipper → **Nodes**. The default local node is enabled and supports all five built-in scan types. Disable it to prevent any analysis on the Jellyfin server. It cannot use the remote app's text detector.
4. Configure the named remote node: tick libraries by name and choose Introduction, Credits, Recap, Preview and/or Commercial. Empty libraries or types leave the node idle. Add path variables for the same media, for example `/data/media/tv` → `<node-media-root>\tv`. The PC/NAS directory must be accessible to the node's Windows account.
5. Choose Original Intro Skipper or Text-aware credits, optional **Use GPU**, and 1, 2 or 4 workers. Original uses GPU only for video decoding; Text-aware also uses OpenCL text inference. Unsupported GPU recipes are unavailable. Resizing and colour conversion stay on CPU. Review hardware and online status on the node card, then save and enable it.
6. Start the node from its page or tray. Manual, scheduled and new-media analysis all use the same server queue. Plugin settings saves notify the connected node; reconnect fetches authoritative settings. Existing jobs keep their original snapshot and changed policy or inputs are rechecked on import.

Existing node identities and credentials remain in `%LOCALAPPDATA%\SegmentScanner`; the renamed EXE reuses them. Legacy selections are adopted once, then server policy is authoritative. Reconnecting never overwrites it. The CLI `work --config FILE` remains available and can use the configured environment variable or remembered Windows credential. This source-only repository includes no all-in-one EXE or model; private packaging requires builder-supplied weights and tool inputs.

## Scoped trial and result review

For a season trial, queue `Timestamps > series > season > Manage > Scan Season` with **Full series** unchecked. Manual Scan retains its existing erase-then-analyze behavior: it clears the selected season's timestamps, analysis state and detection cache before queueing. Choose a test subset intentionally. The bridge still supplies immutable media/version, effective introduction settings and mode revisions; the worker scans mapped original files.

An enabled node keeps ownership when its PC is offline. Overlapping enabled scopes use one deterministic node ID per item/mode. Disabling a node or changing its scope/profile increments its revision, making its previous pending/leased jobs unclaimable and rejecting stale heartbeat, submission and review. Current accepted markers are retained. Finish held reviews before changing settings; incompatible work requires a fresh scoped queue.

To use complete results automatically, tick **Use complete scan results automatically** on that node in the plugin and save. Queue a fresh scoped scan. Complete recognized results pass server admission without per-result approval; failed, incomplete or unknown evidence stays protected. This does not mark the text profile calibrated or retrospectively promote held results. Clear the option to stop future automatic imports, or disable the node to stop new work. Imported markers remain editable.

Raw bundles are written under `state/results/JOB-ATTEMPT.json`. For results still requiring explicit review, stop the worker, generate a report and its decisions template, then accept or reject each candidate in an entire mode. Accepted timings need no manual re-entry:

```powershell
& $scannerExe report --results $resultFile --out $reportFile
& $scannerExe review --config $workerConfig --results $resultFile --decisions $decisionsFile --apply --out $acceptedFile
```

The review CLI needs its elevated API key in the configured environment variable. The bridge anchors review to the exact held payload and rechecks current scope, node revision, media, detector profile, chapter inputs, mode revision and admission. Promotion remains atomic per analysis, including the canonical receipt and Jellyfin projection journal. Identical retry returns current canonical state; changed retry conflicts. Existing legacy held proposals retain their explicit-review path under the saved legacy policy. No new legacy jobs are claimed; drain them before upgrade or queue fresh node work. Failed analyses cannot be accepted, and complete searched coverage is required for accepted negatives.

The Dexter S05 trial returned twelve server-authoritative accepted credits results with approved timings and unchanged media/profile. Fresh credits analysis took 4m15.6s with 1,870 new inferences. Playback testing was waived by the user. The node-configuration upgrade has separate local verification and still needs installation on the user's server. Keep sidecar exports disabled for this bridge trial. Broader held-out accuracy, model provenance and unattended deployment remain separate production gates.
