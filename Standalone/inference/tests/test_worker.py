from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path

import cv2
import numpy as np

INFERENCE_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = INFERENCE_ROOT.parents[1]
sys.path.insert(0, str(INFERENCE_ROOT))

from features import raw_image_features, score_features, stretch_image
import worker


class FeatureTests(unittest.TestCase):
    def test_worker_rejects_out_of_range_threads_before_model_load(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            for threads in (0, 33):
                completed = subprocess.run(
                    [sys.executable, str(INFERENCE_ROOT / "worker.py"), "--manifest", str(Path(directory) / "missing.pb"), "--image-root", directory, "--threads", str(threads)],
                    capture_output=True,
                    text=True,
                    timeout=30,
                )
                self.assertEqual(2, completed.returncode)
                self.assertEqual("", completed.stdout)
                self.assertIn("invalid choice", completed.stderr)

    def test_opencl_gpu_rejection_precedes_model_load(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manifest = worker.Manifest(Path(directory) / "model.pb", "0" * 64, 320, 320, "RGB", 1.0, (0.0, 0.0, 0.0), "Stretch", "feature_fusion/Conv_7/Sigmoid", "NCHW", (1, 1, 80, 80), 0.5, "1", "Cpu")
            with mock.patch.object(worker.cv2.ocl, "haveOpenCL", return_value=False), mock.patch.object(worker.cv2.dnn, "readNetFromTensorflow") as load:
                with self.assertRaisesRegex(ValueError, "OpenCV has no OpenCL support"):
                    worker.ModelHelper(manifest, Path(directory), "OpenClGpu")
            load.assert_not_called()

    def test_opencl_gpu_rejects_partial_cpu_fallback(self) -> None:
        helper = worker.ModelHelper.__new__(worker.ModelHelper)
        helper.net = mock.Mock()
        helper.net.dump.return_value = 'digraph { gpu [label="OCV/OCL"]; fallback [label="OCV/CPU"]; }'
        with self.assertRaisesRegex(ValueError, "CPU fallback layers"):
            helper._validate_opencl_execution()

    def test_empty_text_mask_has_no_centre(self) -> None:
        frame = stretch_image(np.zeros((100, 200, 3), dtype=np.uint8), 320, 320)
        result = score_features(np.zeros((80, 80), dtype=np.float32), frame, 0.999)

        self.assertEqual(0, result["textCellCount"])
        self.assertEqual(0, result["scoreMapTextCellCount"])
        self.assertIsNone(result["scoreMapTextSourceCenterX"])
        self.assertIsNone(result["scoreMapTextSourceCenterY"])
        self.assertIsNone(result["textCenterX"])
        self.assertIsNone(result["textCenterY"])
        self.assertEqual(0, result["textRegions"])

    def test_non_square_stretch_has_no_padding(self) -> None:
        frame = stretch_image(np.full((100, 300, 3), 17, dtype=np.uint8), 320, 320)
        scores = np.zeros((80, 80), dtype=np.float32)
        scores[0, 0] = 1.0
        scores[40, 40] = 1.0

        result = score_features(scores, frame, 0.999)

        self.assertEqual((320, 320, 3), frame.image.shape)
        self.assertTrue(np.all(frame.image == 17))
        self.assertEqual(2, result["textCellCount"])
        self.assertEqual(2, result["scoreMapTextCellCount"])
        self.assertEqual(0.25, result["scoreMapTextSourceCenterX"])
        self.assertEqual(0.25, result["scoreMapTextSourceCenterY"])
        self.assertAlmostEqual(0.25625, result["textCenterX"])
        self.assertAlmostEqual(0.25625, result["textCenterY"])

    def test_edge_text_stays_in_source_coordinates(self) -> None:
        frame = stretch_image(np.zeros((320, 320, 3), dtype=np.uint8), 320, 320)
        scores = np.zeros((80, 80), dtype=np.float32)
        scores[0, 0] = 1.0

        result = score_features(scores, frame, 0.999)

        self.assertGreaterEqual(result["textCenterX"], 0.0)
        self.assertGreaterEqual(result["textCenterY"], 0.0)
        self.assertLess(result["textCenterX"], 0.02)
        self.assertLess(result["textCenterY"], 0.02)
        self.assertEqual(0.0, result["scoreMapTextSourceCenterX"])
        self.assertEqual(0.0, result["scoreMapTextSourceCenterY"])

    def test_raw_bgr_metrics_use_pooled_entropy_and_six_value_modal_bands(self) -> None:
        distinct_channels = np.array([[[0, 127, 255]]], dtype=np.uint8)
        result = raw_image_features(distinct_channels)

        self.assertAlmostEqual(np.log(3) / np.log(256), result["normalizedByteEntropy"])
        self.assertAlmostEqual(1 / 3, result["flatnessScore"])
        for values, expected in (
            ([0], 0.0),
            ([255], 1.0),
            ([0, 1, 2, 3, 3, 3, 4, 5], 0.0),
            ([249, 250, 251, 252, 252, 252, 253, 254], 0.0),
            ([3, 3, 3, 3, 3, 3, 3, 6, 7, 8], 0.30),
        ):
            image = np.repeat(np.array(values, dtype=np.uint8)[None, :, None], 3, axis=2)
            score = raw_image_features(image)["flatnessScore"]
            self.assertAlmostEqual(expected, score)
            self.assertGreaterEqual(score, 0.0)
            self.assertLessEqual(score, 1.0)
        self.assertEqual(
            0.30,
            raw_image_features(
                np.repeat(np.array([3, 3, 3, 3, 3, 3, 3, 6, 7, 8], dtype=np.uint8)[None, :, None], 3, axis=2)
            )["flatnessScore"],
        )


@unittest.skipUnless((REPOSITORY_ROOT / "model_v1.pb").is_file(), "requires the user-supplied BYO model")
class PersistentWorkerTests(unittest.TestCase):
    def test_temporal_evidence_is_independent_of_request_partition(self) -> None:
        manifest = REPOSITORY_ROOT / "Standalone" / "models" / "manifests" / "model-v1.byo.json"
        with tempfile.TemporaryDirectory() as directory:
            image_root = Path(directory)
            frames = []
            for index, luminance in enumerate((0, 64, 64, 128)):
                path = image_root / f"frame-{index}.png"
                self.assertTrue(cv2.imwrite(str(path), np.full((120, 320, 3), luminance, dtype=np.uint8)))
                frames.append({"token": str(index), "timestampTicks": str(index + 1), "path": str(path)})
            resets = [
                {**frames[-1], "timestampTicks": "10"},
                {**frames[-1], "timestampTicks": "10"},
                {**frames[-1], "timestampTicks": "9"},
                {**frames[-1], "timestampTicks": "11", "path": str(image_root / "missing.png")},
                {**frames[-1], "timestampTicks": "12"},
                {**frames[-1], "timestampTicks": "13"},
            ]
            requests = [
                {"type": "infer", "requestId": "whole", "resetTemporal": True, "frames": frames},
                {"type": "infer", "requestId": "first", "resetTemporal": True, "frames": frames[:2]},
                {"type": "infer", "requestId": "second", "frames": frames[2:]},
                {"type": "infer", "requestId": "resets", "resetTemporal": True, "frames": resets},
                {"type": "shutdown"},
            ]
            completed = subprocess.run(
                [sys.executable, str(INFERENCE_ROOT / "worker.py"), "--manifest", str(manifest), "--image-root", str(image_root)],
                input="".join(json.dumps(request) + "\n" for request in requests),
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            output = [json.loads(line) for line in completed.stdout.splitlines()]
            self.assertEqual("hello", output[0]["type"])
            self.assertEqual({"type": "bye"}, output[-1])
            results = {result["requestId"]: result["frames"] for result in output[1:-1]}
            for result in output[1:-1]:
                self.assertIsNone(result["error"])
            whole = results["whole"]
            split = results["first"] + results["second"]
            for frame in whole + split:
                self.assertIsNone(frame["error"])
            self.assertEqual(
                [frame["features"]["temporalDifference"] for frame in whole],
                [frame["features"]["temporalDifference"] for frame in split],
            )
            self.assertEqual(0.0, results["second"][0]["features"]["temporalDifference"])
            for index in (0, 1, 2, 4):
                self.assertIsNone(results["resets"][index]["features"]["temporalDifference"])
            self.assertIsNotNone(results["resets"][3]["error"])
            self.assertIsNone(results["resets"][3]["features"])
            self.assertEqual(0.0, results["resets"][5]["features"]["temporalDifference"])

    def test_real_model_protocol_handles_invalid_image_and_stays_alive(self) -> None:
        manifest = REPOSITORY_ROOT / "Standalone" / "models" / "manifests" / "model-v1.byo.json"
        worker = INFERENCE_ROOT / "worker.py"
        with tempfile.TemporaryDirectory() as directory:
            image_root = Path(directory)
            invalid = image_root / "not-an-image.bin"
            invalid.write_bytes(b"not an image")
            valid = image_root / "frame.png"
            self.assertTrue(cv2.imwrite(str(valid), np.full((120, 320, 3), 255, dtype=np.uint8)))
            with subprocess.Popen(
                [sys.executable, str(worker), "--manifest", str(manifest), "--image-root", str(image_root), "--threads", "3"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            ) as process:
                self.assertIsNotNone(process.stdout)
                self.assertIsNotNone(process.stdin)
                hello = json.loads(process.stdout.readline())
                self.assertEqual(
                    {"protocolVersion", "type", "pythonVersion", "opencvVersion", "inferenceThreads", "modelSha256", "backend", "featureVersion", "imageFeatureVersion", "outputName", "outputShape"},
                    set(hello),
                )
                self.assertEqual("hello", hello["type"])
                self.assertEqual("Cpu", hello["backend"])
                self.assertGreaterEqual(hello["inferenceThreads"], 1)
                self.assertLessEqual(hello["inferenceThreads"], 3)
                self.assertEqual("bgr-byte-square-v3", hello["imageFeatureVersion"])
                self.assertEqual([1, 1, 80, 80], hello["outputShape"])

                process.stdin.write(json.dumps({"type": "infer", "requestId": "bad", "frames": [{"token": "bad", "timestampTicks": "0", "path": str(invalid)}]}) + "\n")
                process.stdin.flush()
                invalid_result = json.loads(process.stdout.readline())
                self.assertIsNotNone(invalid_result["frames"][0]["error"])
                self.assertIsNone(invalid_result["frames"][0]["features"])
                self.assertIsNone(process.poll())

                process.stdin.write(json.dumps({"type": "infer", "requestId": "good", "frames": [{"token": "good", "timestampTicks": "1", "path": str(valid)}]}) + "\n")
                process.stdin.flush()
                valid_result = json.loads(process.stdout.readline())
                self.assertEqual({"type", "requestId", "frames", "error"}, set(valid_result))
                self.assertIsNone(valid_result["error"])
                self.assertIsNone(valid_result["frames"][0]["error"])
                self.assertIn("meanLuminance", valid_result["frames"][0]["features"])

                process.stdin.write('{"type":"shutdown"}\n')
                process.stdin.flush()
                self.assertEqual({"type": "bye"}, json.loads(process.stdout.readline()))
                self.assertEqual(0, process.wait(timeout=10))

    @unittest.skipUnless(os.environ.get("SEGMENTSCANNER_TEST_OPENCL") == "1", "requires SEGMENTSCANNER_TEST_OPENCL=1")
    def test_real_model_opencl_gpu_handshake_reports_actual_device(self) -> None:
        manifest = REPOSITORY_ROOT / "Standalone" / "models" / "manifests" / "model-v1.byo.json"
        with tempfile.TemporaryDirectory() as directory:
            completed = subprocess.run(
                [sys.executable, str(INFERENCE_ROOT / "worker.py"), "--manifest", str(manifest), "--image-root", directory, "--backend", "OpenClGpu"],
                input='{"type":"shutdown"}\n',
                capture_output=True,
                text=True,
                timeout=30,
            )
        self.assertEqual(0, completed.returncode, completed.stderr)
        hello, bye = (json.loads(line) for line in completed.stdout.splitlines())
        self.assertEqual("OpenClGpu", hello["backend"])
        self.assertTrue(all(isinstance(hello["gpuDevice"][name], str) and hello["gpuDevice"][name] for name in ("name", "vendor", "driver", "version")))
        self.assertEqual({"type": "bye"}, bye)


if __name__ == "__main__":
    unittest.main()
