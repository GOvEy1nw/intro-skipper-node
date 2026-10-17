#!/usr/bin/env python3
"""Export existing Plex intro/credits markers as reference data; Python 3.10+."""

import argparse
from datetime import datetime, timezone
import getpass
import json
import os
from pathlib import Path
import sys
import tempfile
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


PAGE_SIZE = 200
MARKER_TYPES = {"intro", "credits", "credit"}


class ExportError(Exception):
    pass


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # Keep the Plex token on the explicitly selected server.


def integer(value, field):
    if isinstance(value, bool) or not isinstance(value, (int, str)):
        raise ExportError(f"Plex returned an invalid {field}.")
    if isinstance(value, str) and not (value.isascii() and value.isdecimal()):
        raise ExportError(f"Plex returned an invalid {field}.")
    try:
        result = int(value)
    except ValueError:
        raise ExportError(f"Plex returned an invalid {field}.") from None
    if result < 0:
        raise ExportError(f"Plex returned a negative {field}.")
    return result


def objects(container, field):
    value = container.get(field, [])
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise ExportError(f"Plex returned an invalid {field} collection.")
    return value


class PlexClient:
    def __init__(self, url, token):
        try:
            parsed = urlsplit(url)
            valid = (parsed.scheme in {"http", "https"} and parsed.hostname
                     and parsed.username is None and parsed.password is None
                     and not parsed.query and not parsed.fragment)
            parsed.port
        except ValueError:
            valid = False
        if not valid:
            raise ExportError("Use a Plex HTTP(S) server URL without credentials, query or fragment.")
        if not token.strip() or "\r" in token or "\n" in token:
            raise ExportError("A valid Plex token is required.")
        self.url = url.rstrip("/")
        self.headers = {"Accept": "application/json", "X-Plex-Token": token.strip(),
                        "X-Plex-Client-Identifier": "segment-scanner-reference-export"}
        self.opener = build_opener(NoRedirect())

    def get(self, path, params=None, start=None):
        url = self.url + path + ("?" + urlencode(params) if params else "")
        headers = dict(self.headers)
        if start is not None:
            headers.update({"X-Plex-Container-Start": str(start),
                            "X-Plex-Container-Size": str(PAGE_SIZE)})
        try:
            with self.opener.open(Request(url, headers=headers, method="GET"), timeout=30) as response:
                body = json.load(response)
        except HTTPError as error:
            code = error.code
            error.close()
            raise ExportError(f"Plex returned HTTP {code}; check the server URL and token. Redirects are refused.") from None
        except (URLError, OSError):
            raise ExportError("Could not read Plex; check the address, connection and TLS certificate.") from None
        except (ValueError, UnicodeError):
            raise ExportError("Plex did not return valid JSON; check the server URL.") from None
        if not isinstance(body, dict) or not isinstance(body.get("MediaContainer"), dict):
            raise ExportError("Plex returned an unexpected response shape.")
        return body["MediaContainer"]


def markers(video):
    result = []
    for marker in objects(video, "Marker"):
        if not isinstance(marker.get("type"), str):
            raise ExportError("Plex returned an invalid marker type.")
        if marker["type"] not in MARKER_TYPES:
            continue
        start = integer(marker.get("startTimeOffset"), "marker startTimeOffset")
        end = integer(marker.get("endTimeOffset"), "marker endTimeOffset")
        if end <= start:
            raise ExportError("Plex returned a marker whose end is not after its start.")
        final = marker.get("final")
        if final not in (None, False, True, 0, 1, "0", "1"):
            raise ExportError("Plex returned an invalid marker final flag.")
        result.append({"id": marker.get("id"), "type": marker["type"],
                       "startTimeOffset": start, "endTimeOffset": end,
                       "final": None if final is None else final in (True, 1, "1")})
    return result


def export_reference(client, library_names=()):
    server = client.get("/")
    available = objects(client.get("/library/sections"), "Directory")
    sections = []
    for section in available:
        if not isinstance(section.get("type"), str):
            raise ExportError("Plex returned an invalid library type.")
        if section["type"] in {"movie", "show"}:
            integer(section.get("key"), "library ID")
            if not isinstance(section.get("title"), str) or not section["title"].strip():
                raise ExportError("Plex returned a missing library name.")
            sections.append(section)
    requested = {name.casefold() for name in library_names}
    if requested - {s["title"].casefold() for s in sections}:
        raise ExportError("A selected library was not found among the token's movie/TV libraries.")
    sections = [s for s in sections if not requested or s["title"].casefold() in requested]
    if not sections:
        raise ExportError("The Plex token has no selected movie or TV libraries.")
    result = {"schemaVersion": 1, "referenceSource": "Plex", "humanConfirmed": False,
              "timestampUnit": "milliseconds", "exportedUtc": datetime.now(timezone.utc).isoformat(),
              "server": {"url": client.url, "machineIdentifier": server.get("machineIdentifier"),
                         "version": server.get("version")}, "libraries": [], "items": []}
    for section in sections:
        library = {"id": str(section["key"]), "name": section["title"], "type": section["type"],
                   "itemCount": 0}
        print(f"Reading library: {library['name']}", file=sys.stderr)
        start, total, seen = 0, None, set()
        while True:
            page = client.get(f"/library/sections/{quote(library['id'], safe='')}/all",
                              {"type": 4 if library["type"] == "show" else 1}, start=start)
            videos = objects(page, "Metadata")
            offset = integer(page.get("offset", start), "page offset")
            size = integer(page.get("size"), "page size")
            page_total = integer(page.get("totalSize"), "page totalSize")
            if (offset != start or size != len(videos) or start + size > page_total
                    or (total is not None and total != page_total) or (not size and start < page_total)):
                raise ExportError("Plex pagination changed or made no progress; rerun when the library is stable.")
            total = page_total
            for video in videos:
                key = str(video.get("ratingKey", ""))
                if not key.isascii() or not key.isdecimal() or key in seen:
                    raise ExportError("Plex returned a missing/duplicate item ID; the export is incomplete.")
                seen.add(key)
                # Marker inclusion is documented on metadata, not on section /all responses.
                detailed = objects(client.get(f"/library/metadata/{key}", {"includeMarkers": 1}), "Metadata")
                if len(detailed) != 1 or str(detailed[0].get("ratingKey")) != key:
                    raise ExportError("Plex metadata did not match the requested item.")
                item = detailed[0]
                if item.get("type") != ("episode" if library["type"] == "show" else "movie"):
                    raise ExportError("Plex returned an unexpected media type.")
                fields = ("ratingKey", "guid", "type", "title", "grandparentTitle", "parentTitle",
                          "parentIndex", "index", "year", "duration", "editionTitle")
                record = {field: item[field] for field in fields if field in item}
                record.update({"libraryId": library["id"], "libraryName": library["name"],
                               "media": [], "markers": markers(item)})
                for media in objects(item, "Media"):
                    version = {k: media[k] for k in ("id", "duration", "videoCodec", "audioCodec",
                                                    "container", "videoResolution") if k in media}
                    version["parts"] = [{k: part[k] for k in ("id", "file", "duration", "size") if k in part}
                                        for part in objects(media, "Part")]
                    record["media"].append(version)
                result["items"].append(record)
                library["itemCount"] += 1
                if library["itemCount"] % 100 == 0:
                    print(f"  {library['itemCount']}/{total} items", file=sys.stderr)
            start += size
            if start == total:
                break
        result["libraries"].append(library)
        print(f"  Complete: {library['itemCount']} items", file=sys.stderr)
    items = result["items"]
    result["summary"] = {"items": len(items), "itemsWithMarkers": sum(bool(i["markers"]) for i in items),
                         "introMarkers": sum(m["type"] == "intro" for i in items for m in i["markers"]),
                         "creditsMarkers": sum(m["type"] in {"credits", "credit"} for i in items for m in i["markers"]),
                         "itemsWithoutFilePaths": sum(not any(p.get("file") for v in i["media"] for p in v["parts"]) for i in items)}
    return result


def write_atomic(output, result):
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", delete=False,
                                         dir=output.parent, prefix=output.name + ".", suffix=".tmp") as stream:
            temporary = Path(stream.name)
            json.dump(result, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, output)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default=os.environ.get("PLEX_URL", "http://localhost:32400"),
                        help="Plex server URL (default: PLEX_URL or http://localhost:32400)")
    parser.add_argument("--library", action="append", default=[], metavar="NAME",
                        help="Library name; repeat to select several (default: all movie/TV libraries)")
    parser.add_argument("--out", type=Path, default=Path(".segmentscanner/plex-reference.json"))
    args = parser.parse_args(argv)
    try:
        token = os.environ.get("PLEX_TOKEN")
        if not token:
            if not sys.stdin.isatty():
                raise ExportError("Set PLEX_TOKEN or run in an interactive terminal for hidden token entry.")
            token = getpass.getpass("Plex token (hidden): ")
        result = export_reference(PlexClient(args.url, token), args.library)
        write_atomic(args.out, result)
        summary = result["summary"]
        print(f"Saved {summary['items']} items, {summary['introMarkers']} intro and "
              f"{summary['creditsMarkers']} credits markers to {args.out}")
        if summary["itemsWithoutFilePaths"]:
            print(f"Warning: {summary['itemsWithoutFilePaths']} items have no visible file paths; "
                  "use a server-owner token for exact file matching.", file=sys.stderr)
        return 0
    except (ExportError, OSError, EOFError) as error:
        message = str(error) if isinstance(error, ExportError) else "Could not read input or write the output file."
        print(f"Export failed: {message} No completed export was written.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nExport cancelled.", file=sys.stderr)
        sys.exit(130)
