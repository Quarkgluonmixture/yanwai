#!/usr/bin/env python3
"""Private recognition-input audit for pre-detected WeChat bubble crops."""

from __future__ import annotations

import argparse
from collections import deque
import contextlib
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
from statistics import median
import statistics
import sys
import time
from typing import Any, Iterable

from PIL import Image

try:
    from scripts.paddle_ocr_benchmark import (
        character_error_rate,
        normalize_text,
    )
except ModuleNotFoundError:  # direct Windows script execution
    from paddle_ocr_benchmark import character_error_rate, normalize_text


@dataclass(frozen=True)
class TextRoiEvidence:
    text_bounds: tuple[int, int, int, int]
    padded_bounds: tuple[int, int, int, int]
    text_band_height: int
    background_rgb: tuple[int, int, int]


@dataclass(frozen=True)
class AuditVariant:
    name: str
    image: Image.Image
    target_text_height: int | None
    interpolation: str
    background_normalization: str


def luminance(color: tuple[int, int, int]) -> float:
    return (0.2126 * color[0]) + (0.7152 * color[1]) + (0.0722 * color[2])


def dominant_color(image: Image.Image) -> tuple[int, int, int]:
    rgb = image.convert("RGB")
    colors = rgb.getcolors(maxcolors=rgb.width * rgb.height)
    if colors is None:
        raise ValueError("could not determine the dominant image color")
    return max(colors, key=lambda item: item[0])[1]


def detect_text_roi(
    image: Image.Image,
    padding: int = 4,
    minimum_luminance_contrast: float = 35,
) -> TextRoiEvidence:
    if padding < 0:
        raise ValueError("padding must be non-negative")

    rgb = image.convert("RGB")
    width, height = rgb.size
    if width == 0 or height == 0:
        raise ValueError("image must not be empty")

    background = dominant_color(rgb)
    background_luminance = luminance(background)
    contrasting = {
        (x, y)
        for y in range(height)
        for x in range(width)
        if abs(luminance(rgb.getpixel((x, y))) - background_luminance)
        >= minimum_luminance_contrast
    }
    components: list[list[tuple[int, int]]] = []
    while contrasting:
        start = contrasting.pop()
        queue = deque([start])
        component = [start]
        while queue:
            x, y = queue.popleft()
            for neighbor in _neighbors(x, y, width, height):
                if neighbor in contrasting:
                    contrasting.remove(neighbor)
                    queue.append(neighbor)
                    component.append(neighbor)
        components.append(component)

    text_pixels = [
        pixel
        for component in components
        if len(component) >= 2 and not _touches_border(component, width, height)
        for pixel in component
    ]
    if not text_pixels:
        raise ValueError("no conservative text-content ROI could be detected")

    left = min(x for x, _ in text_pixels)
    top = min(y for _, y in text_pixels)
    right = max(x for x, _ in text_pixels) + 1
    bottom = max(y for _, y in text_pixels) + 1
    padded = (
        max(0, left - padding),
        max(0, top - padding),
        min(width, right + padding),
        min(height, bottom + padding),
    )
    return TextRoiEvidence(
        text_bounds=(left, top, right, bottom),
        padded_bounds=padded,
        text_band_height=bottom - top,
        background_rgb=background,
    )


def generate_variants(
    image: Image.Image,
    evidence: TextRoiEvidence,
    target_text_heights: Iterable[int] = (32, 40, 48),
) -> list[AuditVariant]:
    rgb = image.convert("RGB")
    text_roi = rgb.crop(evidence.padded_bounds)
    variants = [
        AuditVariant("raw_whole", rgb.copy(), None, "none", "none"),
        AuditVariant("text_roi", text_roi.copy(), None, "none", "none"),
    ]
    for target_height in target_text_heights:
        if target_height <= 0:
            raise ValueError("target text heights must be positive")
        scale = target_height / evidence.text_band_height
        resized_size = (
            max(1, round(text_roi.width * scale)),
            max(1, round(text_roi.height * scale)),
        )
        nearest = text_roi.resize(resized_size, Image.Resampling.NEAREST)
        bicubic = text_roi.resize(resized_size, Image.Resampling.BICUBIC)
        lanczos = text_roi.resize(resized_size, Image.Resampling.LANCZOS)
        variants.extend(
            [
                AuditVariant(
                    f"height{target_height}_nearest",
                    nearest,
                    target_height,
                    "nearest",
                    "none",
                ),
                AuditVariant(
                    f"height{target_height}_bicubic",
                    bicubic,
                    target_height,
                    "bicubic",
                    "none",
                ),
                AuditVariant(
                    f"height{target_height}_lanczos",
                    lanczos,
                    target_height,
                    "lanczos",
                    "none",
                ),
                AuditVariant(
                    f"height{target_height}_lanczos_gray",
                    normalize_grayscale_background(lanczos),
                    target_height,
                    "lanczos",
                    "grayscale_linear",
                ),
            ]
        )
    return variants


def normalize_grayscale_background(image: Image.Image) -> Image.Image:
    rgb = image.convert("RGB")
    background_luminance = luminance(dominant_color(rgb))
    luminances = [luminance(pixel) for pixel in rgb.get_flattened_data()]
    signed_contrasts = [
        value - background_luminance
        for value in luminances
        if abs(value - background_luminance) >= 12
    ]
    if not signed_contrasts:
        return Image.new("RGB", rgb.size, (250, 250, 250))

    foreground_is_lighter = median(signed_contrasts) > 0
    directed = sorted(
        max(0.0, value if foreground_is_lighter else -value)
        for value in signed_contrasts
    )
    reference = max(24.0, directed[round((len(directed) - 1) * 0.95)])
    normalized = Image.new("L", rgb.size)
    output = []
    for value in luminances:
        contrast = value - background_luminance
        if not foreground_is_lighter:
            contrast = -contrast
        darkness = min(225.0, max(0.0, contrast) * 225.0 / reference)
        output.append(round(250.0 - darkness))
    normalized.putdata(output)
    return normalized.convert("RGB")


def prepare_audit_fixtures(
    manifest_paths: Iterable[Path],
    artifact_root: Path,
    padding: int = 4,
    target_text_heights: Iterable[int] = (32, 40, 48),
    artifact_target_height: int = 40,
) -> list[dict[str, Any]]:
    target_heights = list(target_text_heights)
    if artifact_target_height not in target_heights:
        raise ValueError("artifact target height must be one of the benchmark target heights")

    artifact_root.mkdir(parents=True, exist_ok=True)
    prepared: list[dict[str, Any]] = []
    used_directories: set[Path] = set()
    for manifest_path in manifest_paths:
        full_manifest_path = manifest_path.resolve()
        manifest = json.loads(full_manifest_path.read_text(encoding="utf-8"))
        fixtures = _case_value(manifest, "fixtures")
        if not fixtures:
            raise ValueError(f"Audit manifest has no fixtures: {full_manifest_path}")
        for item in fixtures:
            name = str(_required_case_value(item, "name"))
            expected = str(_required_case_value(item, "expected"))
            crop_value = str(_required_case_value(item, "crop"))
            crop_path = (
                full_manifest_path.parent / Path(crop_value.replace("\\", "/"))
            ).resolve()
            dpi = int(_required_case_value(item, "captureDpi"))
            route = str(_required_case_value(item, "ocrRoute"))
            with Image.open(crop_path) as source:
                bubble = source.convert("RGB")
            evidence = detect_text_roi(bubble, padding=padding)
            variants = generate_variants(bubble, evidence, target_heights)

            fixture_directory = artifact_root / safe_filename(name)
            if fixture_directory in used_directories:
                raise ValueError(f"Duplicate audit fixture artifact directory: {fixture_directory.name}")
            used_directories.add(fixture_directory)
            variant_directory = fixture_directory / "variants"
            variant_directory.mkdir(parents=True, exist_ok=True)
            for variant in variants:
                variant.image.save(variant_directory / f"{variant.name}.png")

            raw = _variant_named(variants, "raw_whole")
            roi = _variant_named(variants, "text_roi")
            lanczos = _variant_named(variants, f"height{artifact_target_height}_lanczos")
            gray = _variant_named(variants, f"height{artifact_target_height}_lanczos_gray")
            raw.image.save(fixture_directory / "raw_crop.png")
            roi.image.save(fixture_directory / "text_roi.png")
            lanczos.image.save(fixture_directory / "normalized_lanczos.png")
            gray.image.save(fixture_directory / "normalized_gray.png")

            prepared.append(
                {
                    "fixture": name,
                    "expected": expected,
                    "dpi": dpi,
                    "route": route,
                    "bubble_size": [bubble.width, bubble.height],
                    "text_roi_size": [roi.image.width, roi.image.height],
                    "text_bounds": list(evidence.text_bounds),
                    "text_band_height": evidence.text_band_height,
                    "background_rgb": list(evidence.background_rgb),
                    "crop_path": str(crop_path),
                    "artifact_directory": str(fixture_directory),
                    "variants": variants,
                }
            )
    return prepared


def build_audit_row(
    fixture: dict[str, Any],
    variant: str,
    raw_recognized: str,
    rec_score: float,
    inference_ms: float,
) -> dict[str, Any]:
    expected = str(fixture["expected"])
    normalized_expected = normalize_text(expected)
    normalized_recognized = normalize_text(raw_recognized)
    return {
        "fixture": fixture["fixture"],
        "expected": expected,
        "dpi": fixture["dpi"],
        "route": fixture["route"],
        "bubble_width": fixture["bubble_size"][0],
        "bubble_height": fixture["bubble_size"][1],
        "text_roi_width": fixture["text_roi_size"][0],
        "text_roi_height": fixture["text_roi_size"][1],
        "text_band_height": fixture["text_band_height"],
        "variant": variant,
        "raw_recognized": raw_recognized,
        "normalized_recognized": normalized_recognized,
        "rec_score": rec_score,
        "raw_exact_match": expected == raw_recognized,
        "normalized_match": normalized_expected == normalized_recognized,
        "raw_cer": character_error_rate(expected, raw_recognized),
        "normalized_cer": character_error_rate(
            normalized_expected, normalized_recognized
        ),
        "inference_ms": inference_ms,
    }


def summarize_variant_rows(rows: Iterable[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    grouped: dict[str, list[dict[str, Any]]] = {}
    for row in rows:
        grouped.setdefault(str(row["variant"]), []).append(row)
    return {
        variant: _summarize_rows(variant_rows)
        for variant, variant_rows in sorted(grouped.items())
    }


def benchmark_variants(
    fixtures: list[dict[str, Any]],
    model_name: str,
    device: str,
    warmup_count: int,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    import numpy as np

    startup_started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import paddle
        import paddleocr
        from paddleocr import TextRecognition

        model = TextRecognition(model_name=model_name, device=device)
    startup_ms = (time.perf_counter() - startup_started) * 1000

    first_variant = fixtures[0]["variants"][0]
    warmup_image = np.asarray(first_variant.image.convert("RGB"))
    warmup_started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        for _ in range(warmup_count):
            list(model.predict(input=warmup_image, batch_size=1))
        _synchronize_gpu(device)
    warmup_ms = (time.perf_counter() - warmup_started) * 1000

    rows: list[dict[str, Any]] = []
    for fixture in fixtures:
        for variant in fixture["variants"]:
            input_image = np.asarray(variant.image.convert("RGB"))
            with contextlib.redirect_stdout(sys.stderr):
                _synchronize_gpu(device)
                started = time.perf_counter()
                output = list(model.predict(input=input_image, batch_size=1))
                _synchronize_gpu(device)
            inference_ms = (time.perf_counter() - started) * 1000
            if len(output) != 1:
                raise RuntimeError(
                    f"{model_name} returned {len(output)} results for "
                    f"{fixture['fixture']} ({variant.name})."
                )
            result = output[0]
            rows.append(
                build_audit_row(
                    fixture,
                    variant.name,
                    str(result["rec_text"]),
                    float(result["rec_score"]),
                    inference_ms,
                )
            )

    metadata = {
        "model": model_name,
        "device_requested": device,
        "device_active": paddle.device.get_device(),
        "paddle_version": paddle.__version__,
        "paddleocr_version": paddleocr.__version__,
        "startup_ms": startup_ms,
        "warmup_ms": warmup_ms,
        "warmup_count": warmup_count,
        "model_instances": 1,
    }
    return rows, metadata


def render_audit_markdown(
    rows: list[dict[str, Any]], metadata: dict[str, Any]
) -> str:
    summary = summarize_variant_rows(rows)
    lines = [
        "# Phase 4.5 OCR input audit",
        "",
        "> Paddle `rec_score` is uncalibrated diagnostic metadata, not a correctness probability or `OcrConfidence`.",
        "",
        "Preprocessing variants are not independent OCR engines and their agreement is not independent-engine agreement.",
        "Paddle text detection was not used. Every input derives from an already detected WeChat text-bubble crop.",
        "No production preprocessing, routing, or trust policy is changed by this audit.",
        "",
        "## Runtime",
        "",
        f"- Model: `{metadata['model']}` (one model instance for all variants)",
        f"- Device: requested `{metadata.get('device_requested', 'n/a')}`, active `{metadata['device_active']}`",
        f"- PaddlePaddle: `{metadata['paddle_version']}`; PaddleOCR: `{metadata['paddleocr_version']}`",
        f"- Startup: {float(metadata['startup_ms']):.1f} ms; warmup: {float(metadata['warmup_ms']):.1f} ms ({metadata['warmup_count']} run(s))",
        "",
        "## Variant summary",
        "",
        "| variant | count | raw exact | normalized exact | mean raw CER | mean normalized CER | mean ms | p50 ms | p95 ms |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for variant, item in summary.items():
        lines.append(
            f"| {markdown_cell(variant)} | {item['count']} | {item['raw_exact']} | "
            f"{item['normalized_exact']} | {item['mean_raw_cer']:.3f} | "
            f"{item['mean_normalized_cer']:.3f} | {item['mean_inference_ms']:.1f} | "
            f"{item['p50_inference_ms']:.1f} | {item['p95_inference_ms']:.1f} |"
        )

    lines.extend(["", "## Summary by DPI", ""])
    dpis = sorted({int(row["dpi"]) for row in rows})
    for dpi in dpis:
        lines.extend(
            [
                f"### {dpi} DPI",
                "",
                "| variant | count | raw exact | normalized exact |",
                "| --- | ---: | ---: | ---: |",
            ]
        )
        for variant, item in summary.items():
            dpi_item = item["by_dpi"].get(str(dpi))
            if dpi_item is not None:
                lines.append(
                    f"| {markdown_cell(variant)} | {dpi_item['count']} | "
                    f"{dpi_item['raw_exact']} | {dpi_item['normalized_exact']} |"
                )
        lines.append("")

    lines.extend(
        [
            "## Detailed results",
            "",
            "| fixture | expected | dpi | bubble size | text ROI size | text band | route | variant | recognized | rec_score | raw exact | normalized exact | raw CER | normalized CER | inference_ms |",
            "| --- | --- | ---: | --- | --- | ---: | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |",
        ]
    )
    for row in rows:
        lines.append(
            f"| {markdown_cell(row['fixture'])} | {markdown_cell(row['expected'])} | "
            f"{row['dpi']} | {row['bubble_width']}x{row['bubble_height']} | "
            f"{row['text_roi_width']}x{row['text_roi_height']} | "
            f"{row['text_band_height']} | {markdown_cell(row['route'])} | "
            f"{markdown_cell(row['variant'])} | {markdown_cell(row['raw_recognized'])} | "
            f"{row['rec_score']:.4f} | {str(row['raw_exact_match']).lower()} | "
            f"{str(row['normalized_match']).lower()} | {row['raw_cer']:.3f} | "
            f"{row['normalized_cer']:.3f} | {row['inference_ms']:.1f} |"
        )
    lines.append("")
    return "\n".join(lines)


def render_inspection_index(fixtures: list[dict[str, Any]], target_height: int = 40) -> str:
    lines = [
        "# Private OCR input artifact inspection",
        "",
        "Inspect every row left-to-right. The text ROI must retain complete glyph edges and safe padding; normalized images must not invent, cut, or merge strokes.",
        "",
        "These files contain private chat text and remain gitignored under `.ocr-cache`.",
        "",
    ]
    for fixture in fixtures:
        directory_name = Path(str(fixture["artifact_directory"])).name
        lines.extend(
            [
                f"## {markdown_cell(fixture['expected'])} — {fixture['dpi']} DPI",
                "",
                f"Fixture: `{markdown_cell(fixture['fixture'])}`; route recorded at capture: `{markdown_cell(fixture['route'])}`.",
                "",
                f"| raw whole bubble | padded text ROI | {target_height} px Lanczos | {target_height} px Lanczos + grayscale normalization |",
                "| --- | --- | --- | --- |",
                f"| ![raw]({directory_name}/raw_crop.png) | "
                f"![ROI]({directory_name}/text_roi.png) | "
                f"![Lanczos]({directory_name}/normalized_lanczos.png) | "
                f"![gray]({directory_name}/normalized_gray.png) |",
                "",
            ]
        )
    return "\n".join(lines)


def markdown_cell(value: Any) -> str:
    return (
        str(value)
        .replace("\\", "\\\\")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("|", "¦")
    )


def _synchronize_gpu(device: str) -> None:
    if device.startswith("gpu"):
        import paddle

        paddle.device.synchronize()


def _summarize_rows(rows: list[dict[str, Any]]) -> dict[str, Any]:
    elapsed = sorted(float(row["inference_ms"]) for row in rows)
    return {
        "count": len(rows),
        "raw_exact": sum(bool(row["raw_exact_match"]) for row in rows),
        "normalized_exact": sum(bool(row["normalized_match"]) for row in rows),
        "mean_raw_cer": statistics.fmean(float(row["raw_cer"]) for row in rows),
        "mean_normalized_cer": statistics.fmean(
            float(row["normalized_cer"]) for row in rows
        ),
        "mean_inference_ms": statistics.fmean(elapsed),
        "p50_inference_ms": _percentile(elapsed, 0.50),
        "p95_inference_ms": _percentile(elapsed, 0.95),
        "by_dpi": {
            str(dpi): {
                "count": len(dpi_rows),
                "raw_exact": sum(bool(row["raw_exact_match"]) for row in dpi_rows),
                "normalized_exact": sum(
                    bool(row["normalized_match"]) for row in dpi_rows
                ),
            }
            for dpi, dpi_rows in _group_by_dpi(rows).items()
        },
    }


def _group_by_dpi(rows: Iterable[dict[str, Any]]) -> dict[int, list[dict[str, Any]]]:
    grouped: dict[int, list[dict[str, Any]]] = {}
    for row in rows:
        grouped.setdefault(int(row["dpi"]), []).append(row)
    return grouped


def _percentile(values: list[float], probability: float) -> float:
    if not values:
        return 0.0
    position = (len(values) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return values[lower]
    return values[lower] + ((values[upper] - values[lower]) * (position - lower))


def safe_filename(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", value).strip("._") or "fixture"


def _case_value(mapping: dict[str, Any], name: str) -> Any:
    for key, value in mapping.items():
        if key.casefold() == name.casefold():
            return value
    return None


def _required_case_value(mapping: dict[str, Any], name: str) -> Any:
    value = _case_value(mapping, name)
    if value is None:
        raise ValueError(f"Audit fixture is missing '{name}'.")
    return value


def _variant_named(variants: Iterable[AuditVariant], name: str) -> AuditVariant:
    for variant in variants:
        if variant.name == name:
            return variant
    raise ValueError(f"Audit variant '{name}' was not generated.")


def _neighbors(
    x: int,
    y: int,
    width: int,
    height: int,
) -> Iterable[tuple[int, int]]:
    for offset_y in (-1, 0, 1):
        for offset_x in (-1, 0, 1):
            if offset_x == 0 and offset_y == 0:
                continue
            candidate_x = x + offset_x
            candidate_y = y + offset_y
            if 0 <= candidate_x < width and 0 <= candidate_y < height:
                yield candidate_x, candidate_y


def _touches_border(
    component: Iterable[tuple[int, int]],
    width: int,
    height: int,
) -> bool:
    return any(x == 0 or y == 0 or x == width - 1 or y == height - 1 for x, y in component)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Audit PP-OCRv6_small_rec input preparation on pre-detected private "
            "WeChat text-bubble crops."
        )
    )
    parser.add_argument("--manifest", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--artifact-dir", type=Path)
    parser.add_argument("--model", default="PP-OCRv6_small_rec")
    parser.add_argument("--device", default="gpu:0")
    parser.add_argument("--padding", type=int, default=4)
    parser.add_argument("--target-text-heights", type=int, nargs="+", default=[32, 40, 48])
    parser.add_argument("--artifact-target-height", type=int, default=40)
    parser.add_argument("--warmup-count", type=int, default=1)
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    if arguments.padding < 0:
        raise ValueError("--padding must be non-negative.")
    if arguments.warmup_count < 0:
        raise ValueError("--warmup-count must be non-negative.")
    if any(height <= 0 for height in arguments.target_text_heights):
        raise ValueError("--target-text-heights must all be positive.")

    os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "BOS")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    output_path = arguments.output.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    artifact_root = (
        arguments.artifact_dir.resolve()
        if arguments.artifact_dir
        else output_path.parent / f"{output_path.stem}-artifacts"
    )
    fixtures = prepare_audit_fixtures(
        [path.resolve() for path in arguments.manifest],
        artifact_root,
        padding=arguments.padding,
        target_text_heights=arguments.target_text_heights,
        artifact_target_height=arguments.artifact_target_height,
    )
    if not fixtures:
        raise ValueError("No OCR audit fixtures were prepared.")

    rows, metadata = benchmark_variants(
        fixtures,
        arguments.model,
        arguments.device,
        arguments.warmup_count,
    )
    report = {
        "manifests": [str(path.resolve()) for path in arguments.manifest],
        "artifact_root": str(artifact_root),
        "score_semantics": (
            "Paddle rec_score is uncalibrated diagnostic metadata; it is not "
            "OcrConfidence or a correctness probability."
        ),
        "variant_agreement_semantics": (
            "Preprocessing variants share one recognizer and are not independent engines."
        ),
        "production_policy_changed": False,
        "metadata": metadata,
        "variant_summary": summarize_variant_rows(rows),
        "rows": rows,
    }
    json_path = output_path.with_suffix(".json")
    json_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    output_path.write_text(
        render_audit_markdown(rows, metadata), encoding="utf-8"
    )
    inspection_path = artifact_root / "README.md"
    inspection_path.write_text(
        render_inspection_index(fixtures, arguments.artifact_target_height), encoding="utf-8"
    )
    print(f"OCR input audit report: {output_path}")
    print(f"OCR input audit JSON: {json_path}")
    print(f"Private visual artifacts: {artifact_root}")
    print(f"Manual inspection index: {inspection_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
