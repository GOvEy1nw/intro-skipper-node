from contextlib import contextmanager, redirect_stderr, redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import export_plex_markers as exporter

TOKEN = "fixture-token-not-a-real-credential"


@contextmanager
def plex_fixture(mode="ok"):
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            parsed = urlsplit(self.path)
            query = parse_qs(parsed.query)
            requests.append((parsed.path, query, self.headers.get("X-Plex-Token")))
            if mode == "redirect" and parsed.path == "/":
                self.send_response(302)
                self.send_header("Location", "/unexpected-destination")
                self.end_headers()
                return
            if mode == "unauthorized" and parsed.path == "/library/metadata/102":
                self.send_response(401)
                self.end_headers()
                self.wfile.write(TOKEN.encode())
                return
            if parsed.path == "/":
                data = {"machineIdentifier": "test-server", "version": "test-version"}
            elif parsed.path == "/library/sections":
                data = {"Directory": [{"key": "1", "title": "TV Shows", "type": "show"},
                                      {"key": "2", "title": "Movies", "type": "movie"},
                                      {"key": "3", "title": "Music", "type": "artist"}]}
            elif parsed.path.endswith("/all"):
                is_tv = parsed.path == "/library/sections/1/all"
                if query.get("type") != ["4" if is_tv else "1"]:
                    self.send_error(400)
                    return
                offset = int(self.headers["X-Plex-Container-Start"])
                keys = [101, 102, 103] if is_tv else [201]
                page = keys[offset:offset + 2]
                if mode == "stalled" and offset:
                    page = []
                data = {"offset": offset, "size": len(page), "totalSize": len(keys),
                        "Metadata": [{"ratingKey": str(k)} for k in page]}
            elif parsed.path.startswith("/library/metadata/"):
                if query.get("includeMarkers") != ["1"]:
                    self.send_error(400)
                    return
                key = parsed.path.rsplit("/", 1)[-1]
                video = {"ratingKey": key, "title": "An episode" if key != "201" else "A movie",
                         "type": "episode" if key != "201" else "movie",
                         "parentIndex": 5, "index": int(key) - 100,
                         "grandparentTitle": "Dexter", "duration": 3272832,
                         "Media": [{"id": "version-1", "Part": [{"file": f"/nas/{key}.mkv"}]}]}
                if key == "101":
                    video["Media"] = [{"id": "original", "Part": [{"file": "/nas/Dexter é.part1.mkv"},
                                                                  {"file": "/nas/Dexter é.part2.mkv"}]},
                                      {"id": "alternate", "Part": [{"file": "I:\\media\\Dexter alternate.mkv"}]}]
                    video["Marker"] = [{"id": 1, "type": "intro", "startTimeOffset": 176167, "endTimeOffset": 285696},
                                       {"id": 2, "type": "credits", "startTimeOffset": 3211548, "endTimeOffset": 3230000, "final": 0},
                                       {"id": 3, "type": "credits", "startTimeOffset": 3240000, "endTimeOffset": 3272832, "final": 1}]
                elif key == "103":
                    video["Marker"] = [{"type": "intro", "startTimeOffset": "101144", "endTimeOffset": "208693"},
                                       {"type": "commercial", "startTimeOffset": 3, "endTimeOffset": 5}]
                elif key == "201":
                    video["Marker"] = [{"type": "credits", "startTimeOffset": 6000, "endTimeOffset": 7200, "final": True}]
                if mode == "malformed":
                    video["Marker"] = None
                data = {"Metadata": [video]}
            else:
                self.send_error(404)
                return
            body = json.dumps({"MediaContainer": data}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", requests
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class PlexExportTests(unittest.TestCase):
    def test_complete_export_and_named_library_selection(self):
        with plex_fixture() as (url, requests), tempfile.TemporaryDirectory() as directory:
            result = exporter.export_reference(exporter.PlexClient(url, TOKEN))
            output = Path(directory) / "reference.json"
            exporter.write_atomic(output, result)
            result = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(result["summary"], {"items": 4, "itemsWithMarkers": 3,
                             "introMarkers": 2, "creditsMarkers": 3, "itemsWithoutFilePaths": 0})
            self.assertEqual({i["ratingKey"] for i in result["items"]}, {"101", "102", "103", "201"})
            episode = next(i for i in result["items"] if i["ratingKey"] == "101")
            self.assertEqual([(m["startTimeOffset"], m["endTimeOffset"]) for m in episode["markers"]],
                             [(176167, 285696), (3211548, 3230000), (3240000, 3272832)])
            self.assertEqual([m["final"] for m in episode["markers"]], [None, False, True])
            self.assertEqual([p["file"] for v in episode["media"] for p in v["parts"]],
                             ["/nas/Dexter é.part1.mkv", "/nas/Dexter é.part2.mkv", "I:\\media\\Dexter alternate.mkv"])
            self.assertFalse(result["humanConfirmed"])
            self.assertEqual(result["referenceSource"], "Plex")
            self.assertEqual(result["timestampUnit"], "milliseconds")
            self.assertTrue(all(token == TOKEN and "X-Plex-Token" not in query for _, query, token in requests))
            self.assertNotIn(TOKEN, output.read_text(encoding="utf-8"))
            filtered = exporter.export_reference(exporter.PlexClient(url, TOKEN), ["movies"])
            self.assertEqual([i["ratingKey"] for i in filtered["items"]], ["201"])

    def test_failures_preserve_output_and_do_not_expose_credentials(self):
        for mode in ("unauthorized", "stalled", "write-error", "malformed"):
            with self.subTest(mode=mode), plex_fixture(mode) as (url, _), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "reference.json"
                output.write_text("previous export", encoding="utf-8")
                capture = io.StringIO()
                failure = patch.object(exporter.os, "replace", side_effect=PermissionError(TOKEN))
                with patch.dict(os.environ, {"PLEX_TOKEN": TOKEN}), redirect_stdout(capture), redirect_stderr(capture):
                    if mode == "write-error":
                        with failure:
                            exit_code = exporter.main(["--url", url, "--out", str(output)])
                    else:
                        exit_code = exporter.main(["--url", url, "--out", str(output)])
                self.assertEqual(exit_code, 1)
                self.assertEqual(output.read_text(encoding="utf-8"), "previous export")
                self.assertNotIn(TOKEN, capture.getvalue())
                self.assertEqual(list(Path(directory).glob("*.tmp")), [])

    def test_redirects_cannot_forward_token_and_url_credentials_are_rejected(self):
        with plex_fixture("redirect") as (url, requests):
            with self.assertRaises(exporter.ExportError):
                exporter.export_reference(exporter.PlexClient(url, TOKEN))
            self.assertNotIn("/unexpected-destination", [p for p, _, _ in requests])
        for url in ("http://user:secret@localhost:32400", "http://localhost:32400?X-Plex-Token=secret"):
            with self.subTest(url=url), self.assertRaises(exporter.ExportError):
                exporter.PlexClient(url, TOKEN)


if __name__ == "__main__":
    unittest.main()
