# Plex marker reference export

`scripts/export_plex_markers.py` copies the intro and credits markers already visible to Plex into a local reference file. It is for comparison work, not for creating scanner labels or qualifying scanner output for production.

## Run it

The exporter needs Python 3.10 or later and uses only the standard library. For the repository/trial environment, run this from the repository root:

```powershell
& .\Standalone\.venv\Scripts\python.exe .\Standalone\scripts\export_plex_markers.py --url http://localhost:32400
```

On another host with Python 3.10 or later installed, `python` or `py` may be used instead of the repository virtual-environment interpreter.

The default server URL is `PLEX_URL` when that environment variable is set, otherwise `http://localhost:32400`. The default output is `.segmentscanner/plex-reference.json`.

Set `PLEX_TOKEN` before running to authenticate non-interactively. Otherwise, the script prompts for the token in an interactive terminal without echoing it. Do not put a Plex token on the command line.

By default, the export includes every movie and show library the token can see; music libraries are ignored. Repeat `--library` to restrict it by its Plex library name:

```powershell
& .\Standalone\.venv\Scripts\python.exe .\Standalone\scripts\export_plex_markers.py --url http://plex.local:32400 --library Movies --library "TV Shows"
& .\Standalone\.venv\Scripts\python.exe .\Standalone\scripts\export_plex_markers.py --library Movies --out D:\reference\plex-markers.json
```

## What the file means

The JSON declares `referenceSource: "Plex"`, `humanConfirmed: false`, and `timestampUnit: "milliseconds"`. Each marker preserves Plex's exact `startTimeOffset` and `endTimeOffset` values in milliseconds, and its `final` value. `final` is `null` when Plex did not provide it. An item can have multiple intro or credits spans, or an empty `markers` array.

Each item retains Plex server `ratingKey` identity and available metadata. Its `media` entries preserve every visible version and every visible part file path, with the related Plex duration values. The export also records selected libraries, Plex server version and machine identifier, and a summary of items and markers.

`ratingKey` is a Plex identity, not a Jellyfin item ID. For evaluation, first join the exact corresponding media and match the Jellyfin media source and file-version token. Map NAS and server path prefixes when needed. With multiple Plex versions or parts, confirm that the selected files have the same timeline and duration before comparing their markers. An empty `markers` array means Plex did not export a marker for that item; it does not prove that no marker exists. The exporter only reads existing Plex data and does not trigger Plex marker analysis.

## Completeness and failure safety

The script inventories the selected movie/show libraries with paginated requests, then requests metadata with `includeMarkers=1` for every movie or episode. It writes progress after every 100 items. It uses authenticated `GET` requests only, sends the token in the `X-Plex-Token` header, refuses redirects, and keeps TLS certificate verification enabled.

The destination is atomically replaced only after the entire inventory and marker export succeeds. An error, cancellation, pagination change, inaccessible library, invalid response, or failed write leaves an existing destination file in place and does not produce a partial successful export.

## References

- [Plex Media Server API documentation](https://developer.plex.tv/pms/)
- [Plex guide: finding an authentication token](https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/)
