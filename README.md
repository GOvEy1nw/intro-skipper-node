# IntroSkipper-Node

IntroSkipper-Node is the remote analysis node for [the Intro Skipper plugin feature branch](https://github.com/GOvEy1nw/intro-skipper/tree/codex/scan-21-remote-analysis-nodes). The `plugin/` submodule pins the canonical plugin and Contracts sources to commit `fca5e8f6893ccc9a1e9ee43313f822be3371f1c1`. This integration follows stable Intro Skipper **12.0.6.0** and keeps Jellyfin **12.2.0** as the minimum server version. Update the plugin and node together.

Clone recursively so the shared plugin source is present:

```powershell
git clone --recurse-submodules https://github.com/GOvEy1nw/intro-skipper-node.git
cd intro-skipper-node
git submodule update --init --recursive
```

Build and test from the repository root:

```powershell
dotnet restore Standalone/SegmentScanner.sln --locked-mode -p:StandaloneBuild=true
dotnet build Standalone/SegmentScanner.sln -c Release --no-restore -p:StandaloneBuild=true -p:SkipWebBuild=true
dotnet test Standalone/tests/SegmentScanner.Tests/SegmentScanner.Tests.csproj -c Release --no-restore -p:StandaloneBuild=true -p:SkipWebBuild=true
python -m unittest Standalone/scripts/tests/test_export_plex_markers.py
```

On Windows, also build the tray application:

```powershell
dotnet build Standalone/src/SegmentScanner.Desktop/SegmentScanner.Desktop.csproj -c Release -p:RestoreLockedMode=true -p:StandaloneBuild=true -p:SkipWebBuild=true
```

The node scans introductions, credits, recaps, previews and commercials. Credits can use the Original Intro Skipper or Text-aware method. The local settings page contains only connection details and Start/Stop; configure libraries, scan types, credits method, GPU, workers and path mappings centrally in the plugin's **Nodes** settings.

No model weights, media, credentials, private packages or runtime bundles are included. Private all-in-one packaging requires builder-supplied model weights, manifest, FFmpeg/FFprobe and Python/OpenCV inputs.
