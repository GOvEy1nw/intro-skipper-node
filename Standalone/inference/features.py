"""Square-stretched frame and EAST score-map features for protocol version 1."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import cv2
import numpy as np

NEAR_BLACK_LUMINANCE = 16.0
HISTOGRAM_BINS = 256


@dataclass(frozen=True)
class StretchFrame:
    """A whole-frame image stretched to the model input dimensions."""

    image: np.ndarray


def stretch_image(image: np.ndarray, width: int, height: int) -> StretchFrame:
    """Resize an image to the model input dimensions without preserving aspect ratio."""
    source_height, source_width = image.shape[:2]
    if source_width <= 0 or source_height <= 0:
        raise ValueError("image dimensions must be positive")

    return StretchFrame(image=cv2.resize(image, (width, height), interpolation=cv2.INTER_LINEAR))


def score_features(
    scores: np.ndarray,
    frame: StretchFrame,
    threshold: float,
) -> dict[str, Any]:
    """Return score-map features in the square-stretched source coordinates."""
    score_height, score_width = scores.shape
    score_text = scores >= threshold
    text_count = int(score_text.sum())
    result: dict[str, Any] = {
        "textFraction": float(text_count / scores.size),
        "textCellCount": text_count,
        "scoreMapTextCellCount": text_count,
        "maxScore": float(scores.max()),
        "scoreP95": float(np.percentile(scores, 95)),
        "rowOccupancy": np.mean(score_text, axis=1).tolist(),
        "columnOccupancy": np.mean(score_text, axis=0).tolist(),
    }
    score_rows, score_columns = np.nonzero(score_text)
    if score_columns.size == 0:
        result.update(scoreMapTextSourceCenterX=None, scoreMapTextSourceCenterY=None)
    else:
        result.update(
            scoreMapTextSourceCenterX=float(np.mean(score_columns) / score_width),
            scoreMapTextSourceCenterY=float(np.mean(score_rows) / score_height),
        )
    if text_count == 0:
        result.update(
            textCenterX=None,
            textCenterY=None,
            textWidth=0.0,
            textHeight=0.0,
            textRegions=0,
            spatialDispersion=0.0,
        )
        return result

    rows, columns = np.nonzero(score_text)
    x = (columns + 0.5) / score_width
    y = (rows + 0.5) / score_height
    x_edges = [columns.min() / score_width, (columns.max() + 1) / score_width]
    y_edges = [rows.min() / score_height, (rows.max() + 1) / score_height]
    labels_count, _ = cv2.connectedComponents(score_text.astype(np.uint8), connectivity=8)
    centre_x = float(np.mean(x))
    centre_y = float(np.mean(y))
    dispersion = float(np.sqrt(np.mean((x - centre_x) ** 2 + (y - centre_y) ** 2)))
    result.update(
        textCenterX=centre_x,
        textCenterY=centre_y,
        textWidth=float(x_edges[1] - x_edges[0]),
        textHeight=float(y_edges[1] - y_edges[0]),
        textRegions=int(labels_count - 1),
        spatialDispersion=dispersion,
    )
    return result


def raw_image_features(image: np.ndarray) -> dict[str, float]:
    """Compute BGR byte-distribution metrics before image preprocessing."""
    pixel_count = image.shape[0] * image.shape[1]
    if pixel_count <= 0:
        raise ValueError("image has no pixels")

    pooled_histogram = np.bincount(image.reshape(-1), minlength=HISTOGRAM_BINS)
    probabilities = pooled_histogram[pooled_histogram > 0] / (pixel_count * 3)
    flatness_scores = []
    for channel in cv2.split(image):
        histogram = np.bincount(channel.reshape(-1), minlength=HISTOGRAM_BINS)
        peak = int(np.argmax(histogram))
        inside_band = histogram[max(0, peak - 3) : min(255, peak + 3)].sum()
        flatness_scores.append((pixel_count - inside_band) / pixel_count)

    return {
        "normalizedByteEntropy": float(-np.sum(probabilities * np.log(probabilities)) / np.log(HISTOGRAM_BINS)),
        "flatnessScore": float(np.mean(flatness_scores)),
    }


def image_features(frame: StretchFrame, previous: StretchFrame | None) -> dict[str, float | None]:
    """Compute fixed-bin BT.709 luminance features over the square-stretched image."""
    blue, green, red = cv2.split(frame.image.astype(np.float32))
    luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue
    if luminance.size == 0:
        raise ValueError("stretched frame has no pixels")

    histogram, _ = np.histogram(luminance, bins=HISTOGRAM_BINS, range=(0, 256))
    probabilities = histogram[histogram > 0] / luminance.size
    temporal_difference: float | None = None
    if previous is not None:
        previous_blue, previous_green, previous_red = cv2.split(previous.image.astype(np.float32))
        previous_luminance = 0.2126 * previous_red + 0.7152 * previous_green + 0.0722 * previous_blue
        temporal_difference = float(np.mean(np.abs(luminance - previous_luminance)))

    return {
        "meanLuminance": float(np.mean(luminance)),
        "entropy": float(-np.sum(probabilities * np.log2(probabilities))),
        "histogramPeakRatio": float(histogram.max() / luminance.size),
        "nearBlackRatio": float(np.mean(luminance <= NEAR_BLACK_LUMINANCE)),
        "temporalDifference": temporal_difference,
    }
