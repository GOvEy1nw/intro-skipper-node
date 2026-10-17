"""Persistent JSON-lines helper for a bring-your-own EAST graph."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation
from importlib.metadata import PackageNotFoundError, version
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from features import StretchFrame, image_features, raw_image_features, score_features, stretch_image

PROTOCOL_VERSION = 1
IMAGE_FEATURE_VERSION = "bgr-byte-square-v3"
MAX_LINE_BYTES = 64 * 1024
MAX_MANIFEST_BYTES = 64 * 1024
MAX_BATCH_SIZE = 32
MAX_PATH_LENGTH = 4096
MAX_IMAGE_BYTES = 64 * 1024 * 1024
MAX_IMAGE_DIMENSION = 8192
MAX_SCORE_MAP_SIDE = 256
MAX_RESPONSE_BYTES = 1024 * 1024
TICKS_PATTERN = re.compile(r"^[0-9]+$")


def _log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


@dataclass(frozen=True)
class Manifest:
    model_path: Path
    sha256: str
    input_width: int
    input_height: int
    color_order: str
    scale: float
    mean: tuple[float, float, float]
    resize_policy: str
    output_name: str
    output_layout: str
    output_shape: tuple[int, int, int, int]
    threshold: float
    feature_version: str
    backend: str

    @classmethod
    def load(cls, path: Path) -> Manifest:
        try:
            with path.open("rb") as stream:
                raw = stream.read(MAX_MANIFEST_BYTES + 1)
            if len(raw) > MAX_MANIFEST_BYTES:
                raise ValueError(f"manifest exceeds {MAX_MANIFEST_BYTES}-byte limit")
            data = json.loads(raw.decode("utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError(f"manifest is unreadable: {exc}") from exc
        if not isinstance(data, dict):
            raise ValueError("manifest must be an object")
        required = {
            "schemaVersion",
            "modelId",
            "sourceReference",
            "sha256",
            "distributionStatus",
            "modelPath",
            "format",
            "inputWidth",
            "inputHeight",
            "colorOrder",
            "scale",
            "mean",
            "resizePolicy",
            "outputName",
            "outputLayout",
            "outputShape",
            "textThreshold",
            "featureVersion",
            "backend",
            "runtime",
        }
        missing = sorted(required.difference(data))
        if missing:
            raise ValueError(f"manifest is missing required fields: {', '.join(missing)}")
        unknown = sorted(data.keys() - required - {"productionApproval", "geometryOutputSupported"})
        if unknown:
            raise ValueError(f"manifest has unknown fields: {', '.join(unknown)}")
        if data["schemaVersion"] != 1 or data["format"] != "TensorFlowFrozenGraph":
            raise ValueError("manifest must be schema version 1 for a TensorFlow frozen graph")
        if data["resizePolicy"] != "Stretch" or data["colorOrder"] != "RGB":
            raise ValueError("manifest must use RGB Stretch preprocessing")
        if data["outputLayout"] != "NCHW" or data["backend"] != "Cpu":
            raise ValueError("manifest must use the NCHW Cpu baseline")
        runtime = data["runtime"]
        if not isinstance(runtime, dict) or set(runtime) != {"python", "opencv", "numpy"}:
            raise ValueError("manifest runtime must specify only python, opencv and numpy")
        try:
            actual_runtime = {
                "python": sys.version.split()[0],
                "opencv": f"opencv-python-headless=={version('opencv-python-headless')}",
                "numpy": f"numpy=={np.__version__}",
            }
        except PackageNotFoundError as exc:
            raise ValueError(f"manifest runtime dependency is not installed: {exc}") from exc
        for name, actual in actual_runtime.items():
            if runtime[name] != actual:
                raise ValueError(f"manifest runtime {name} mismatch: expected {runtime[name]!r}, actual {actual!r}")
        model_reference = data["modelPath"]
        if not isinstance(model_reference, str) or not model_reference or "://" in model_reference:
            raise ValueError("manifest modelPath must be a non-empty local path")
        model_path = Path(model_reference)
        if not model_path.is_absolute():
            model_path = path.parent / model_path
        model_path = model_path.resolve(strict=False)
        if len(str(model_path)) > MAX_PATH_LENGTH or not model_path.is_file():
            raise ValueError("manifest modelPath does not identify a readable local model file")
        if _sha256(model_path) != data["sha256"].lower():
            raise ValueError("model SHA-256 does not match manifest")
        mean = data["mean"]
        shape = data["outputShape"]
        if (
            not isinstance(mean, list)
            or len(mean) != 3
            or not all(isinstance(value, (int, float)) and math.isfinite(value) for value in mean)
            or not isinstance(shape, list)
            or len(shape) != 4
            or not all(isinstance(value, int) and value > 0 for value in shape)
        ):
            raise ValueError("manifest mean or outputShape is invalid")
        width, height = data["inputWidth"], data["inputHeight"]
        if not all(isinstance(value, int) and 0 < value <= MAX_IMAGE_DIMENSION for value in (width, height)):
            raise ValueError("manifest input dimensions are invalid")
        threshold = data["textThreshold"]
        if not isinstance(threshold, (int, float)) or not math.isfinite(threshold) or not 0 <= threshold <= 1:
            raise ValueError("manifest textThreshold must be finite and between zero and one")
        return cls(
            model_path=model_path,
            sha256=data["sha256"].lower(),
            input_width=width,
            input_height=height,
            color_order=data["colorOrder"],
            scale=float(data["scale"]),
            mean=tuple(float(value) for value in mean),
            resize_policy=data["resizePolicy"],
            output_name=data["outputName"],
            output_layout=data["outputLayout"],
            output_shape=tuple(shape),
            threshold=float(threshold),
            feature_version=data["featureVersion"],
            backend=data["backend"],
        )


class ModelHelper:
    """Owns one loaded network and validates its score-map contract before serving."""

    def __init__(self, manifest: Manifest, image_root: Path, backend: str = "Cpu") -> None:
        self.manifest = manifest
        self.image_root = image_root.resolve(strict=True)
        if not self.image_root.is_dir():
            raise ValueError("image-root must be an existing directory")
        if backend not in ("Cpu", "OpenClGpu"):
            raise ValueError(f"unsupported inference backend {backend!r}")
        self.backend = backend
        self.gpu_device = self._enable_opencl_gpu() if backend == "OpenClGpu" else None
        self.net = cv2.dnn.readNetFromTensorflow(str(manifest.model_path))
        self.net.setPreferableBackend(cv2.dnn.DNN_BACKEND_OPENCV)
        self.net.setPreferableTarget(cv2.dnn.DNN_TARGET_OPENCL if backend == "OpenClGpu" else cv2.dnn.DNN_TARGET_CPU)
        self.output_shape = self._validate_output_contract()
        if backend == "OpenClGpu":
            self._validate_opencl_execution()

    @staticmethod
    def _enable_opencl_gpu() -> dict[str, str]:
        if not cv2.ocl.haveOpenCL():
            raise ValueError("OpenCL GPU inference was requested, but OpenCV has no OpenCL support")
        cv2.ocl.setUseOpenCL(True)
        if not cv2.ocl.useOpenCL():
            raise ValueError("OpenCL GPU inference was requested, but OpenCV could not enable OpenCL")
        if not hasattr(cv2.dnn, "DNN_TARGET_OPENCL"):
            raise ValueError("OpenCL GPU inference was requested, but this OpenCV DNN build has no OpenCL target")
        device = cv2.ocl.Device_getDefault()
        if not device.available():
            raise ValueError("OpenCL GPU inference was requested, but no OpenCL device is available")
        if not (device.type() & cv2.ocl.Device_TYPE_GPU):
            raise ValueError("OpenCL GPU inference was requested, but the selected OpenCL device is not a GPU")
        metadata = {"name": device.name(), "vendor": device.vendorName(), "driver": device.driverVersion(), "version": device.version()}
        if any(not isinstance(value, str) or not value.strip() for value in metadata.values()):
            raise ValueError("OpenCL GPU inference was requested, but the selected GPU did not report complete device metadata")
        return metadata

    def _validate_opencl_execution(self) -> None:
        try:
            graph = self.net.dump()
        except cv2.error as exc:
            raise ValueError(f"OpenCL GPU inference ran, but OpenCV could not inspect the executed DNN graph: {exc}") from exc
        if "OCV/OCL" not in graph:
            raise ValueError("OpenCL GPU inference was requested, but the executed DNN graph is not OCV/OCL")
        if "OCV/CPU" in graph:
            raise ValueError("OpenCL GPU inference was requested, but the executed DNN graph contains CPU fallback layers")

    def _validate_output_contract(self) -> tuple[int, int, int, int]:
        blank = np.zeros((self.manifest.input_height, self.manifest.input_width, 3), dtype=np.uint8)
        scores = self._forward(stretch_image(blank, self.manifest.input_width, self.manifest.input_height))
        actual = tuple(int(value) for value in scores.shape)
        if actual != self.manifest.output_shape:
            raise ValueError(f"model score-map shape {actual} does not match manifest {self.manifest.output_shape}")
        return actual

    def _forward(self, frame: StretchFrame) -> np.ndarray:
        blob = cv2.dnn.blobFromImage(
            frame.image,
            scalefactor=self.manifest.scale,
            size=(self.manifest.input_width, self.manifest.input_height),
            mean=self.manifest.mean,
            swapRB=self.manifest.color_order == "RGB",
            crop=False,
        )
        self.net.setInput(blob)
        scores = self.net.forward(self.manifest.output_name)
        if (
            not isinstance(scores, np.ndarray)
            or scores.ndim != 4
            or scores.shape[0] != 1
            or scores.shape[1] != 1
            or not 0 < scores.shape[2] <= MAX_SCORE_MAP_SIDE
            or not 0 < scores.shape[3] <= MAX_SCORE_MAP_SIDE
            or not np.isfinite(scores).all()
        ):
            raise ValueError(f"model returned an invalid NCHW score map with shape {getattr(scores, 'shape', None)}")
        return scores

    def infer_frame(
        self,
        request: dict[str, Any],
        previous: StretchFrame | None,
        previous_timestamp: Decimal | None,
    ) -> tuple[dict[str, Any], StretchFrame | None, Decimal | None]:
        token = request.get("token")
        timestamp = request.get("timestampTicks")
        try:
            if not isinstance(token, str) or not token or len(token) > 512:
                raise ValueError("token must be a non-empty string up to 512 characters")
            if not isinstance(timestamp, str) or not TICKS_PATTERN.fullmatch(timestamp):
                raise ValueError("timestampTicks must be a non-negative decimal string")
            timestamp_ticks = Decimal(timestamp)
            if previous_timestamp is not None and timestamp_ticks <= previous_timestamp:
                previous = None
            image_path = self._validate_image_path(request.get("path"))
            image = self._read_image(image_path)
            frame = stretch_image(image, self.manifest.input_width, self.manifest.input_height)
            scores = self._forward(frame)[0, 0]
            features = score_features(scores, frame, self.manifest.threshold)
            features.update(image_features(frame, previous))
            features.update(raw_image_features(image))
            _require_finite(features)
            return {"token": token, "timestampTicks": timestamp, "features": features, "error": None}, frame, timestamp_ticks
        except (OSError, ValueError, InvalidOperation, cv2.error) as exc:
            return {"token": token if isinstance(token, str) else "", "timestampTicks": timestamp if isinstance(timestamp, str) else "", "features": None, "error": str(exc)}, None, None

    def _validate_image_path(self, value: Any) -> Path:
        if not isinstance(value, str) or not value or len(value) > MAX_PATH_LENGTH:
            raise ValueError("path must be a bounded absolute image path")
        path = Path(value)
        if not path.is_absolute():
            raise ValueError("path must be absolute")
        resolved = path.resolve(strict=True)
        try:
            resolved.relative_to(self.image_root)
        except ValueError as exc:
            raise ValueError("path is outside image-root") from exc
        if not resolved.is_file() or resolved.stat().st_size > MAX_IMAGE_BYTES:
            raise ValueError("image file is missing or exceeds the size limit")
        return resolved

    @staticmethod
    def _read_image(path: Path) -> np.ndarray:
        image = cv2.imread(str(path), cv2.IMREAD_COLOR)
        if image is None or image.ndim != 3 or image.shape[2] != 3:
            raise ValueError("image cannot be decoded as a color frame")
        height, width = image.shape[:2]
        if not 0 < width <= MAX_IMAGE_DIMENSION or not 0 < height <= MAX_IMAGE_DIMENSION:
            raise ValueError("image dimensions exceed the limit")
        return image


def _require_finite(value: Any) -> None:
    if isinstance(value, dict):
        for nested in value.values():
            _require_finite(nested)
    elif isinstance(value, list):
        for nested in value:
            _require_finite(nested)
    elif isinstance(value, float) and not math.isfinite(value):
        raise ValueError("feature calculation produced a non-finite value")


def _emit(payload: dict[str, Any]) -> None:
    encoded = json.dumps(payload, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAX_RESPONSE_BYTES:
        encoded = b'{"type":"error","error":"response exceeds size limit"}'
    sys.stdout.buffer.write(encoded + b"\n")
    sys.stdout.buffer.flush()


def _request_error(request: Any, message: str) -> dict[str, Any]:
    request_id = request.get("requestId") if isinstance(request, dict) else None
    if isinstance(request_id, str):
        return {"type": "result", "requestId": request_id, "frames": [], "error": message}
    return {"type": "error", "error": message}


def serve(helper: ModelHelper, inference_threads: int) -> int:
    """Keep temporal history until resetTemporal, non-increasing time or a frame failure."""
    hello = {
        "protocolVersion": PROTOCOL_VERSION,
        "type": "hello",
        "pythonVersion": sys.version.split()[0],
        "opencvVersion": cv2.__version__,
        "inferenceThreads": inference_threads,
        "modelSha256": helper.manifest.sha256,
        "backend": helper.backend,
        "featureVersion": helper.manifest.feature_version,
        "imageFeatureVersion": IMAGE_FEATURE_VERSION,
        "outputName": helper.manifest.output_name,
        "outputShape": list(helper.output_shape),
    }
    if helper.gpu_device is not None:
        hello["gpuDevice"] = helper.gpu_device
    _emit(hello)
    previous: StretchFrame | None = None
    previous_timestamp: Decimal | None = None
    while True:
        raw = sys.stdin.buffer.readline(MAX_LINE_BYTES + 1)
        if not raw:
            return 0
        if len(raw) > MAX_LINE_BYTES or not raw.endswith(b"\n"):
            _emit(_request_error(None, "request line exceeds size limit"))
            while raw and not raw.endswith(b"\n"):
                raw = sys.stdin.buffer.readline(MAX_LINE_BYTES + 1)
            continue
        try:
            request = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            _emit(_request_error(None, f"invalid JSON request: {exc}"))
            continue
        if not isinstance(request, dict):
            _emit(_request_error(request, "request must be an object"))
            continue
        if request.get("type") == "shutdown":
            _emit({"type": "bye"})
            return 0
        if request.get("type") != "infer":
            _emit(_request_error(request, "request type must be infer or shutdown"))
            continue
        request_id = request.get("requestId")
        frames = request.get("frames")
        if not isinstance(request_id, str) or not request_id or len(request_id) > 512:
            _emit(_request_error(request, "requestId must be a non-empty string up to 512 characters"))
            continue
        if not isinstance(frames, list) or not frames or len(frames) > MAX_BATCH_SIZE:
            _emit(_request_error(request, f"frames must contain between one and {MAX_BATCH_SIZE} entries"))
            continue
        reset_temporal = request.get("resetTemporal", False)
        if not isinstance(reset_temporal, bool):
            _emit(_request_error(request, "resetTemporal must be a boolean"))
            continue
        if reset_temporal:
            previous = None
            previous_timestamp = None
        results: list[dict[str, Any]] = []
        for frame_request in frames:
            if not isinstance(frame_request, dict):
                results.append({"token": "", "timestampTicks": "", "features": None, "error": "frame must be an object"})
                previous = None
                previous_timestamp = None
                continue
            result, previous, previous_timestamp = helper.infer_frame(frame_request, previous, previous_timestamp)
            results.append(result)
        _emit({"type": "result", "requestId": request_id, "frames": results, "error": None})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--image-root", required=True, type=Path)
    parser.add_argument("--threads", type=int, choices=range(1, 33))
    parser.add_argument("--backend", choices=("Cpu", "OpenClGpu"), default="Cpu")
    arguments = parser.parse_args()
    try:
        if arguments.threads is not None:
            cv2.setNumThreads(arguments.threads)
            inference_threads = cv2.getNumThreads()
            if not 1 <= inference_threads <= arguments.threads:
                raise ValueError(f"OpenCV backend cannot enforce requested thread limit {arguments.threads}; reported {inference_threads}")
        else:
            inference_threads = cv2.getNumThreads()
        return serve(ModelHelper(Manifest.load(arguments.manifest.resolve(strict=True)), arguments.image_root, arguments.backend), inference_threads)
    except (OSError, ValueError, cv2.error) as exc:
        _log(f"startup validation failed: {exc}")
        _emit({"type": "error", "error": f"startup validation failed: {exc}"})
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
