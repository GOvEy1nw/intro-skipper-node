#!/usr/bin/env python3
"""Build the private, self-contained Windows desktop payload from local inputs only."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urlsplit


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_PAYLOAD_ROOT = REPOSITORY_ROOT / ".segmentscanner" / "desktop-payload"
DESKTOP_PROJECT = REPOSITORY_ROOT / "Standalone" / "src" / "SegmentScanner.Desktop" / "SegmentScanner.Desktop.csproj"
SENSITIVE_KEY_PARTS = ("secret", "token", "password", "credential", "apikey", "api_key", "authorization", "cookie")
BASE_RUNTIME_EXCLUDED = {"site-packages", "test", "tests", "tkinter", "idlelib", "ensurepip", "__pycache__", "turtledemo"}


class PackagingError(RuntimeError):
    """Raised when a local input cannot safely form the desktop payload."""


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python", dest="python_exe", required=True, type=Path, help="Configured venv python.exe.")
    parser.add_argument("--ffmpeg", required=True, type=Path, help="Explicit ffmpeg.exe to bundle.")
    parser.add_argument("--ffprobe", required=True, type=Path, help="Explicit ffprobe.exe to bundle.")
    parser.add_argument("--model-manifest", required=True, type=Path, help="BYO model manifest with the required SHA-256.")
    parser.add_argument("--output", required=True, type=Path, help="Private package root; receipts are written here.")
    parser.add_argument("--seed-config", type=Path, help="Optional private worker configuration; only safe metadata is copied.")
    parser.add_argument("--payload-root", type=Path, default=DEFAULT_PAYLOAD_ROOT, help="Ignored, content-addressed payload root.")
    parser.add_argument("--prepare-only", action="store_true", help="Stage and self-check the payload without publishing the desktop project.")
    return parser.parse_args()


def existing_file(path: Path, description: str) -> Path:
    resolved = path.expanduser().resolve()
    if not resolved.is_file():
        raise PackagingError(f"{description} does not exist or is not a file: {resolved}")
    return resolved


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def run_exact(arguments: list[str], description: str) -> str:
    completed = subprocess.run(arguments, cwd=REPOSITORY_ROOT, capture_output=True, text=True, check=False, encoding="utf-8", errors="replace")
    output = (completed.stdout + completed.stderr).strip()
    if completed.returncode != 0:
        raise PackagingError(f"{description} failed with exit code {completed.returncode}:\n{output}")
    return output


def python_details(python_exe: Path) -> dict[str, str]:
    code = (
        "import cv2, json, numpy, sys; "
        "print(json.dumps({'base_prefix': sys.base_prefix, 'version': sys.version.split()[0], "
        "'numpy': numpy.__version__, 'opencv': cv2.__version__}))"
    )
    output = run_exact([str(python_exe), "-I", "-c", code], "configured Python runtime probe")
    try:
        details = json.loads(output)
    except json.JSONDecodeError as error:
        raise PackagingError(f"configured Python runtime probe did not return JSON: {output}") from error
    if not isinstance(details.get("base_prefix"), str) or not Path(details["base_prefix"]).is_dir():
        raise PackagingError("configured Python runtime did not report a usable sys.base_prefix")
    if not details["version"].startswith("3.12."):
        raise PackagingError(f"desktop payload requires CPython 3.12, found {details['version']}")
    return {key: str(value) for key, value in details.items()}


def copy_file(source: Path, destination: Path) -> None:
    if source.is_symlink():
        raise PackagingError(f"refusing to follow symlinked input: {source}")
    if destination.exists():
        raise PackagingError(f"refusing to overwrite existing payload file: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def copy_tree(source: Path, destination: Path, excluded: Callable[[Path], bool]) -> None:
    for item in source.rglob("*"):
        relative = item.relative_to(source)
        if excluded(relative):
            continue
        if item.is_symlink():
            raise PackagingError(f"refusing to follow symlinked input: {item}")
        target = destination / relative
        if item.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        elif item.is_file():
            copy_file(item, target)


def runtime_excluded(relative: Path) -> bool:
    lower_parts = {part.casefold() for part in relative.parts}
    return bool(lower_parts & BASE_RUNTIME_EXCLUDED) or relative.suffix.casefold() in {".pyc", ".pyo"}


def package_excluded(relative: Path) -> bool:
    return "__pycache__" in {part.casefold() for part in relative.parts} or relative.suffix.casefold() in {".pyc", ".pyo"}


def single_match(directory: Path, pattern: str, description: str) -> Path:
    matches = sorted(directory.glob(pattern))
    if len(matches) != 1:
        raise PackagingError(f"expected one {description} matching {pattern} in {directory}, found {len(matches)}")
    return matches[0]


def copy_python_runtime(python_exe: Path, details: dict[str, str], runtime_python: Path) -> dict[str, Path]:
    base_prefix = Path(details["base_prefix"])
    python_binary = existing_file(base_prefix / "python.exe", "base Python executable")
    copy_file(python_binary, runtime_python / "python.exe")
    for pattern, description in (("python3*.dll", "Python DLL"), ("vcruntime*.dll", "Visual C++ runtime DLL")):
        matches = sorted(base_prefix.glob(pattern))
        if not matches:
            raise PackagingError(f"no {description} matching {pattern} found in {base_prefix}")
        for source in matches:
            copy_file(source, runtime_python / source.name)
    for name in ("DLLs", "Lib"):
        source = base_prefix / name
        if not source.is_dir():
            raise PackagingError(f"base Python runtime is missing {source}")
        copy_tree(source, runtime_python / name, runtime_excluded)

    python_license = next((candidate for candidate in (base_prefix / "LICENSE.txt", base_prefix / "LICENSE") if candidate.is_file()), None)
    if python_license is None:
        raise PackagingError(f"base Python runtime is missing LICENSE.txt or LICENSE in {base_prefix}")
    copy_file(python_license, runtime_python / "LICENSE.txt")

    site_packages = python_exe.parent.parent / "Lib" / "site-packages"
    if not site_packages.is_dir():
        raise PackagingError(f"configured virtual environment has no site-packages directory: {site_packages}")
    for source in (
        existing_file(site_packages / "numpy" / "__init__.py", "NumPy package").parent,
        existing_file(site_packages / "cv2" / "__init__.py", "OpenCV package").parent,
        single_match(site_packages, "numpy.libs", "NumPy native library directory"),
        single_match(site_packages, "numpy-*.dist-info", "NumPy metadata directory"),
        single_match(site_packages, "opencv_python_headless-*.dist-info", "OpenCV metadata directory"),
    ):
        copy_tree(source, runtime_python / "Lib" / "site-packages" / source.name, package_excluded)
    for source in sorted(site_packages.glob("*.pth")):
        copy_file(source, runtime_python / "Lib" / "site-packages" / source.name)
        companion = source.with_suffix(".py")
        if companion.is_file():
            copy_file(companion, runtime_python / "Lib" / "site-packages" / companion.name)

    (runtime_python / "python312._pth").write_text(".\nDLLs\nLib\nLib/site-packages\n../inference\nimport site\n", encoding="utf-8", newline="\n")
    return {"base_prefix": base_prefix, "site_packages": site_packages, "python_license": python_license}


def load_model_manifest(path: Path) -> tuple[dict[str, Any], Path, str]:
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PackagingError(f"model manifest is not valid UTF-8 JSON: {path}") from error
    if not isinstance(manifest, dict):
        raise PackagingError("model manifest must contain a JSON object")
    expected_hash = manifest.get("sha256")
    model_path = manifest.get("modelPath")
    if not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_hash):
        raise PackagingError("model manifest must contain a 64-character SHA-256 value")
    if not isinstance(model_path, str) or not model_path:
        raise PackagingError("model manifest must contain a relative modelPath")
    model = (path.parent / model_path).resolve()
    if not model.is_file():
        raise PackagingError(f"model manifest points to a missing model: {model}")
    actual_hash = sha256_file(model)
    if actual_hash.casefold() != expected_hash.casefold():
        raise PackagingError(f"model SHA-256 mismatch: manifest={expected_hash.casefold()} actual={actual_hash}")
    return manifest, model, actual_hash


def safe_value(value: Any) -> Any:
    return copy.deepcopy(value)


def pick(mapping: dict[str, Any], keys: tuple[str, ...]) -> dict[str, Any]:
    return {key: safe_value(mapping[key]) for key in keys if key in mapping}


def assert_no_sensitive_keys(value: Any, path: str = "seed") -> None:
    if isinstance(value, dict):
        for key, nested in value.items():
            lower = key.casefold()
            if key == "credentialEnvironmentVariable":
                if not isinstance(nested, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", nested):
                    raise PackagingError("seed credentialEnvironmentVariable must be an environment-variable name")
            elif any(part in lower for part in SENSITIVE_KEY_PARTS):
                raise PackagingError(f"sanitized seed still contains a sensitive field at {path}.{key}")
            assert_no_sensitive_keys(nested, f"{path}.{key}")
    elif isinstance(value, list):
        for index, nested in enumerate(value):
            assert_no_sensitive_keys(nested, f"{path}[{index}]")


def sanitize_seed(source_path: Path) -> dict[str, Any]:
    try:
        source = json.loads(source_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PackagingError(f"seed configuration is not valid UTF-8 JSON: {source_path}") from error
    if not isinstance(source, dict):
        raise PackagingError("seed configuration must contain a JSON object")

    seed = pick(source, ("schemaVersion", "mode"))
    paths = source.get("paths")
    if isinstance(paths, dict):
        selected_paths = pick(paths, ("stateDirectory", "cacheDirectory", "mappings", "allowedMediaRoots"))
        if selected_paths:
            seed["paths"] = selected_paths
    resources = source.get("resources")
    if isinstance(resources, dict):
        selected_resources = pick(resources, ("concurrentJobs", "concurrentFiles", "ffmpegThreads", "inferenceThreads", "maximumTemporaryGigabytes", "processTimeoutSeconds", "inferenceTimeoutSeconds", "maximumCacheMegabytes"))
        if selected_resources:
            seed["resources"] = selected_resources
    credits = source.get("credits")
    if isinstance(credits, dict):
        selected_credits = pick(credits, ("method", "inferenceBackend", "videoDecoder", "profileId", "sampleIntervalSeconds", "lookbackFractions", "refinementFramesPerSecond", "startOffsetSeconds", "endOffsetSeconds", "blackOnlyFallback", "decisionProfile"))
        seed["credits"] = selected_credits
    exports = source.get("exports")
    if isinstance(exports, dict):
        selected_exports = pick(exports, ("enabled", "writer", "xmlLayout", "jsonSidecar", "preserveUnknownFiles", "overwriteModifiedOwnedFiles", "mirroredRoot"))
        if selected_exports:
            seed["exports"] = selected_exports
    server = source.get("server")
    if isinstance(server, dict):
        selected_server = pick(server, ("baseUrl", "credentialEnvironmentVariable", "requireTlsValidation", "allowInsecureHttp", "workerId", "pollSeconds", "heartbeatSeconds"))
        node = server.get("node")
        if isinstance(node, dict):
            selected_node = pick(node, ("configured", "name", "libraryIds", "modes", "leaseMinutes", "automaticImports"))
            if selected_node:
                selected_server["node"] = selected_node
        if selected_server:
            seed["server"] = selected_server
    if seed.get("mode") != source.get("mode"):
        raise PackagingError("seed sanitization did not preserve the worker mode")
    if seed.get("mode") == "ConnectedWorker":
        required = ("baseUrl", "credentialEnvironmentVariable", "workerId", "node", "pollSeconds", "heartbeatSeconds")
        if not isinstance(seed.get("server"), dict) or any(key not in seed["server"] for key in required):
            raise PackagingError("connected-worker seed is missing required server metadata")
        parsed = urlsplit(seed["server"]["baseUrl"])
        if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise PackagingError("seed baseUrl must be an http(s) origin without user info, query, or fragment")
        if seed["server"]["baseUrl"] != server["baseUrl"] or seed["server"]["workerId"] != server["workerId"] or seed["server"]["node"] != server["node"]:
            raise PackagingError("seed sanitization did not preserve server node identity metadata")
    if isinstance(credits, dict) and (seed["credits"].get("inferenceBackend") != credits.get("inferenceBackend") or seed["credits"].get("videoDecoder") != credits.get("videoDecoder")):
        raise PackagingError("seed sanitization did not preserve the configured credits backends")
    assert_no_sensitive_keys(seed)
    return seed


def copy_inference(runtime: Path) -> None:
    inference = REPOSITORY_ROOT / "Standalone" / "inference"
    files = sorted(path for path in inference.glob("*.py") if path.is_file())
    if not files:
        raise PackagingError(f"no top-level inference Python files found in {inference}")
    for source in files:
        copy_file(source, runtime / "inference" / source.name)


def copy_ffmpeg(ffmpeg: Path, ffprobe: Path, runtime: Path) -> None:
    copy_file(ffmpeg, runtime / "ffmpeg" / "ffmpeg.exe")
    copy_file(ffprobe, runtime / "ffmpeg" / "ffprobe.exe")
    for dependency in sorted(ffmpeg.parent.glob("*.dll")):
        copy_file(dependency, runtime / "ffmpeg" / dependency.name)


def copy_license_tree(source: Path, destination: Path) -> list[Path]:
    copied: list[Path] = []
    if source.is_file():
        copy_file(source, destination / source.name)
        return [destination / source.name]
    for item in source.rglob("*"):
        if item.is_file() and not item.is_symlink():
            target = destination / item.relative_to(source)
            copy_file(item, target)
            copied.append(target)
    return copied


def copy_licenses(runtime: Path, python_runtime: dict[str, Path], ffmpeg: Path) -> list[Path]:
    licenses = runtime / "licenses"
    copied = [licenses / "Python-LICENSE.txt"]
    copy_file(python_runtime["python_license"], copied[0])
    site_packages = python_runtime["site_packages"]
    for metadata in (single_match(site_packages, "numpy-*.dist-info", "NumPy metadata directory"), single_match(site_packages, "opencv_python_headless-*.dist-info", "OpenCV metadata directory")):
        license_directory = metadata / "licenses"
        if license_directory.is_dir():
            copied.extend(copy_license_tree(license_directory, licenses / metadata.name / "licenses"))
    for name in ("LICENSE.txt", "LICENSE-3RD-PARTY.txt"):
        candidate = site_packages / "cv2" / name
        if candidate.is_file():
            destination = licenses / "OpenCV" / name
            copy_file(candidate, destination)
            copied.append(destination)

    ffmpeg_roots = (ffmpeg.parent, ffmpeg.parent.parent)
    candidates = [root / name for root in ffmpeg_roots for name in ("COPYING.GPLv3", "COPYING", "LICENSE", "LICENSE.txt")]
    ffmpeg_license = next((candidate for candidate in candidates if candidate.is_file()), None)
    if ffmpeg_license is None:
        raise PackagingError("configured FFmpeg distribution has no GPL/license file next to its executable")
    text = ffmpeg_license.read_text(encoding="utf-8", errors="replace")
    if "GNU GENERAL PUBLIC LICENSE" not in text:
        raise PackagingError("configured FFmpeg license file does not contain the GPL text required by this GPL build")
    ffmpeg_destination = licenses / "FFmpeg" / "COPYING.GPLv3"
    copy_file(ffmpeg_license, ffmpeg_destination)
    copied.append(ffmpeg_destination)
    return copied


def ffmpeg_details(ffmpeg: Path, ffprobe: Path) -> dict[str, str]:
    return {
        "ffmpegVersion": run_exact([str(ffmpeg), "-version"], "configured ffmpeg version probe").splitlines()[0],
        "ffprobeVersion": run_exact([str(ffprobe), "-version"], "configured ffprobe version probe").splitlines()[0],
        "ffmpegLicense": run_exact([str(ffmpeg), "-L"], "configured ffmpeg license probe"),
    }


def write_notice(runtime: Path, model_manifest: dict[str, Any], model_hash: str, python_info: dict[str, str], ffmpeg_info: dict[str, str], license_files: list[Path]) -> None:
    license_lines = [f"- {path.relative_to(runtime).as_posix()} SHA-256 {sha256_file(path)}" for path in sorted(license_files)]
    python_executable = runtime / "python" / "python.exe"
    numpy_metadata = single_match(runtime / "python" / "Lib" / "site-packages", "numpy-*.dist-info", "bundled NumPy metadata directory") / "METADATA"
    opencv_binary = runtime / "python" / "Lib" / "site-packages" / "cv2" / "cv2.pyd"
    ffmpeg = runtime / "ffmpeg" / "ffmpeg.exe"
    ffprobe = runtime / "ffmpeg" / "ffprobe.exe"
    lines = [
        "IntroSkipper-Node private local desktop artifact",
        "",
        "This payload is for local use only. The bundled model is user-supplied; its provenance and licence are unknown.",
        "Do not redistribute this artifact or the model without independently established permissions.",
        "",
        "Model",
        f"- id: {model_manifest.get('modelId', 'unknown')}",
        f"- sourceReference: {model_manifest.get('sourceReference', 'unknown')}",
        f"- distributionStatus: {model_manifest.get('distributionStatus', 'unknown')}",
        f"- SHA-256: {model_hash}",
        "",
        "Python",
        f"- CPython {python_info['version']} from the configured interpreter sys.base_prefix.",
        f"- runtime/python/python.exe SHA-256: {sha256_file(python_executable)}",
        f"- NumPy {python_info['numpy']}; METADATA SHA-256: {sha256_file(numpy_metadata)}",
        f"- OpenCV {python_info['opencv']}; cv2.pyd SHA-256: {sha256_file(opencv_binary)}",
        "",
        "FFmpeg",
        f"- {ffmpeg_info['ffmpegVersion']}",
        f"- {ffmpeg_info['ffprobeVersion']}",
        f"- runtime/ffmpeg/ffmpeg.exe SHA-256: {sha256_file(ffmpeg)}",
        f"- runtime/ffmpeg/ffprobe.exe SHA-256: {sha256_file(ffprobe)}",
        "",
        "Bundled license files",
        *license_lines,
        "",
        "ffmpeg -L output",
        ffmpeg_info["ffmpegLicense"],
        "",
    ]
    (runtime / "NOTICE.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")


def listed_files(root: Path) -> list[dict[str, Any]]:
    records = []
    for path in sorted(candidate for candidate in root.rglob("*") if candidate.is_file() and candidate.name != "payload-manifest.json"):
        records.append({"path": path.relative_to(root).as_posix(), "size": path.stat().st_size, "sha256": sha256_file(path)})
    return records


def self_check(payload: Path, expected: dict[str, str], model_hash: str) -> None:
    python = payload / "runtime" / "python" / "python.exe"
    ffmpeg = payload / "runtime" / "ffmpeg" / "ffmpeg.exe"
    ffprobe = payload / "runtime" / "ffmpeg" / "ffprobe.exe"
    model = payload / "runtime" / "models" / "model_v1.pb"
    required = (python, ffmpeg, ffprobe, model, payload / "runtime" / "models" / "manifest.json", payload / "runtime" / "NOTICE.md")
    missing = [str(path.relative_to(payload)) for path in required if not path.is_file()]
    if missing:
        raise PackagingError(f"payload self-check found missing files: {', '.join(missing)}")
    details = python_details(python)
    for key in ("version", "numpy", "opencv"):
        if details[key] != expected[key]:
            raise PackagingError(f"payload Python {key} is {details[key]}, expected {expected[key]}")
    if sha256_file(model) != model_hash:
        raise PackagingError("payload model checksum does not match the supplied manifest")
    helper = subprocess.run(
        [str(python), "-I", str(payload / "runtime" / "inference" / "worker.py"),
         "--manifest", str(model.with_name("manifest.json")), "--image-root", str(payload),
         "--threads", "1"],
        input='{"type":"shutdown"}\n', capture_output=True, text=True, timeout=60,
        encoding="utf-8", errors="replace", check=False,
    )
    if helper.returncode:
        raise PackagingError(f"bundled inference helper startup failed: {helper.stderr.strip()}")
    messages = [json.loads(line) for line in helper.stdout.splitlines()]
    if (len(messages) != 2 or messages[0].get("type") != "hello"
            or messages[0].get("modelSha256") != model_hash or messages[1].get("type") != "bye"):
        raise PackagingError("bundled inference helper did not complete its model handshake")
    ffmpeg_details(ffmpeg, ffprobe)
    if list(payload.rglob("*.pdb")):
        raise PackagingError("payload contains PDB files")


def payload_key(manifest: Path, model_hash: str, python_info: dict[str, str], ffmpeg: Path, ffprobe: Path, seed: dict[str, Any] | None) -> str:
    inference_files = {path.name: sha256_file(path) for path in sorted((REPOSITORY_ROOT / "Standalone" / "inference").glob("*.py"))}
    inputs: dict[str, Any] = {
        "modelManifestSha256": sha256_file(manifest),
        "modelSha256": model_hash,
        "python": python_info,
        "ffmpegSha256": sha256_file(ffmpeg),
        "ffprobeSha256": sha256_file(ffprobe),
        "inference": inference_files,
        "seed": seed,
    }
    return hashlib.sha256(canonical_json(inputs)).hexdigest()[:20]


def write_json_once(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    try:
        with path.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(encoded)
    except FileExistsError:
        existing = path.read_text(encoding="utf-8")
        if existing != encoded:
            raise PackagingError(f"refusing to overwrite existing receipt: {path}")


def stage_payload(arguments: argparse.Namespace) -> tuple[Path, dict[str, Any]]:
    python_exe = existing_file(arguments.python_exe, "configured Python executable")
    ffmpeg = existing_file(arguments.ffmpeg, "configured ffmpeg executable")
    ffprobe = existing_file(arguments.ffprobe, "configured ffprobe executable")
    manifest_path = existing_file(arguments.model_manifest, "model manifest")
    python_info = python_details(python_exe)
    model_manifest, model_source, model_hash = load_model_manifest(manifest_path)
    seed = sanitize_seed(existing_file(arguments.seed_config, "seed configuration")) if arguments.seed_config else None
    key = payload_key(manifest_path, model_hash, python_info, ffmpeg, ffprobe, seed)
    payload_root = arguments.payload_root.expanduser().resolve()
    payload_root.mkdir(parents=True, exist_ok=True)
    payload = payload_root / f"{key}-{uuid.uuid4().hex}"
    staging = payload_root / f".staging-{key}-{uuid.uuid4().hex}"
    try:
        runtime = staging / "runtime"
        python_runtime = copy_python_runtime(python_exe, python_info, runtime / "python")
        copy_ffmpeg(ffmpeg, ffprobe, runtime)
        copy_inference(runtime)
        copy_file(model_source, runtime / "models" / "model_v1.pb")
        rewritten_manifest = safe_value(model_manifest)
        rewritten_manifest["modelPath"] = "model_v1.pb"
        if {key: value for key, value in rewritten_manifest.items() if key != "modelPath"} != {key: value for key, value in model_manifest.items() if key != "modelPath"}:
            raise PackagingError("model manifest rewrite changed fields other than modelPath")
        (runtime / "models").mkdir(parents=True, exist_ok=True)
        (runtime / "models" / "manifest.json").write_text(json.dumps(rewritten_manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        if seed is not None:
            (runtime / "seed.json").write_text(json.dumps(seed, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        ffmpeg_info = ffmpeg_details(ffmpeg, ffprobe)
        license_files = copy_licenses(runtime, python_runtime, ffmpeg)
        write_notice(runtime, model_manifest, model_hash, python_info, ffmpeg_info, license_files)
        self_check(staging, python_info, model_hash)
        manifest = {
            "payloadKey": key,
            "modelSha256": model_hash,
            "python": pick(python_info, ("version", "numpy", "opencv")),
            "ffmpeg": {"sha256": sha256_file(ffmpeg), "version": ffmpeg_info["ffmpegVersion"]},
            "ffprobe": {"sha256": sha256_file(ffprobe), "version": ffmpeg_info["ffprobeVersion"]},
            "seedIncluded": seed is not None,
            "files": listed_files(staging),
        }
        (staging / "payload-manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        os.replace(staging, payload)
        return payload, manifest
    except Exception:
        if staging.exists():
            if staging.is_symlink() or not staging.name.startswith(".staging-") or staging.resolve().parent != payload_root.resolve():
                raise PackagingError(f"refusing to remove an unowned staging directory: {staging}")
            shutil.rmtree(staging)
        raise


def publish(arguments: argparse.Namespace, payload: Path, manifest: dict[str, Any]) -> Path:
    if not DESKTOP_PROJECT.is_file():
        raise PackagingError(f"desktop project is not ready: {DESKTOP_PROJECT}; use --prepare-only until it exists")
    output = arguments.output.expanduser().resolve()
    if output.exists() and any(output.iterdir()):
        raise PackagingError(f"refusing to overwrite existing delivery directory: {output}")
    publish_directory = output.parent / f".publish-{manifest['payloadKey']}-{uuid.uuid4().hex}"
    publish_directory.mkdir(parents=True, exist_ok=True)
    run_exact(["dotnet", "restore", str(DESKTOP_PROJECT), "--runtime", "win-x64",
        "-p:SkipWebBuild=true", "-p:StandaloneBuild=true"], "desktop runtime restore")
    command = [
        "dotnet",
        "publish",
        str(DESKTOP_PROJECT),
        "--configuration",
        "Release",
        "--runtime",
        "win-x64",
        "--self-contained",
        "true",
        "--no-restore",
        "--output",
        str(publish_directory),
        f"-p:DesktopPayloadRoot={payload / 'runtime'}",
        "-p:SkipWebBuild=true",
        "-p:StandaloneBuild=true",
    ]
    try:
        run_exact(command, "desktop publish")
        executable = publish_directory / "IntroSkipper-Node.exe"
        if not executable.is_file():
            raise PackagingError("desktop publish did not produce IntroSkipper-Node.exe")
        output.mkdir(parents=True, exist_ok=True)
        copy_file(executable, output / "IntroSkipper-Node.exe")
        if {path.name for path in output.iterdir()} != {"IntroSkipper-Node.exe"}:
            raise PackagingError("delivery directory must contain only IntroSkipper-Node.exe")
        return output
    finally:
        if publish_directory.exists():
            if publish_directory.is_symlink() or not publish_directory.name.startswith(".publish-") or publish_directory.resolve().parent != output.parent.resolve():
                raise PackagingError(f"refusing to remove an unowned publish directory: {publish_directory}")
            shutil.rmtree(publish_directory)


def main() -> int:
    arguments = parse_arguments()
    payload, manifest = stage_payload(arguments)
    output = arguments.output.expanduser().resolve()
    receipt: dict[str, Any] = {
        "payloadKey": manifest["payloadKey"],
        "payload": str(payload),
        "payloadManifestSha256": sha256_file(payload / "payload-manifest.json"),
        "modelSha256": manifest["modelSha256"],
        "python": manifest["python"],
        "ffmpeg": manifest["ffmpeg"],
        "ffprobe": manifest["ffprobe"],
        "seedIncluded": manifest["seedIncluded"],
        "privateArtifact": True,
        "modelDistribution": "unknown-license-no-redistribution",
    }
    if arguments.prepare_only:
        receipt["published"] = False
        write_json_once(output.parent / f"{output.name}-payload-{manifest['payloadKey']}.receipt.json", receipt)
        print(f"PAYLOAD_READY={payload}")
        return 0
    published = publish(arguments, payload, manifest)
    receipt["published"] = True
    receipt["publishDirectory"] = str(published)
    receipt["executable"] = "IntroSkipper-Node.exe"
    receipt["executableSha256"] = sha256_file(published / "IntroSkipper-Node.exe")
    write_json_once(output.parent / f"{output.name}-publish-{manifest['payloadKey']}.receipt.json", receipt)
    print(f"PUBLISHED={published}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except PackagingError as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2) from error
