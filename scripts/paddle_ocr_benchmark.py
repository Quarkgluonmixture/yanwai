#!/usr/bin/env python3
"""Benchmark PaddleOCR recognition-only models on existing WeChat bubble crops."""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import statistics
import time
from pathlib import Path
from typing import Any, Iterable


DEFAULT_MODELS = ("PP-OCRv6_small_rec", "PP-OCRv6_medium_rec")


def character_error_rate(expected: str, recognized: str) -> float:
    if not expected:
        return 0.0 if not recognized else 1.0
    return edit_distance(expected, recognized) / len(expected)


def edit_distance(expected: str, recognized: str) -> int:
    previous = list(range(len(recognized) + 1))
    current = [0] * (len(recognized) + 1)
    for row, expected_character in enumerate(expected, start=1):
        current[0] = row
        for column, recognized_character in enumerate(recognized, start=1):
            substitution_cost = int(expected_character != recognized_character)
            current[column] = min(
                current[column - 1] + 1,
                previous[column] + 1,
                previous[column - 1] + substitution_cost,
            )
        previous, current = current, previous
    return previous[len(recognized)]


def percentile(values: Iterable[float], probability: float) -> float:
    ordered = sorted(values)
    if not ordered:
        return 0.0
    position = (len(ordered) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    weight = position - lower
    return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight)


def summarize_rows(
    rows: list[dict[str, Any]], high_score_threshold: float
) -> dict[str, Any]:
    short_rows = [row for row in rows if row["group"] == "short_chinese"]
    short_expected_characters = sum(len(row["expected"]) for row in short_rows)
    short_edit_distance = sum(row["edit_distance"] for row in short_rows)
    elapsed = [float(row["elapsed_ms"]) for row in rows]
    high_score_wrong = [
        row["fixture"]
        for row in rows
        if not row["exact_match"]
        and row["rec_score"] is not None
        and row["rec_score"] >= high_score_threshold
    ]
    return {
        "short_exact_match": sum(row["exact_match"] for row in short_rows),
        "short_count": len(short_rows),
        "short_corpus_cer": (
            short_edit_distance / short_expected_characters
            if short_expected_characters
            else 0.0
        ),
        "overall_exact_match": sum(row["exact_match"] for row in rows),
        "overall_count": len(rows),
        "high_score_threshold": high_score_threshold,
        "high_score_wrong_count": len(high_score_wrong),
        "high_score_wrong_fixtures": high_score_wrong,
        "latency_mean_ms": statistics.fmean(elapsed) if elapsed else 0.0,
        "latency_p50_ms": percentile(elapsed, 0.50),
        "latency_p95_ms": percentile(elapsed, 0.95),
    }


def normalize_text(value: str | None) -> str:
    compact = re.sub(r"\s+", " ", (value or "").strip())
    if len(compact) < 3:
        return compact
    output: list[str] = []
    for index, character in enumerate(compact):
        if (
            character == " "
            and index > 0
            and index + 1 < len(compact)
            and should_remove_space(compact[index - 1], compact[index + 1])
        ):
            continue
        if (
            character == "\\"
            and index > 0
            and index + 1 < len(compact)
            and is_cjk(compact[index - 1])
            and is_cjk(compact[index + 1])
        ):
            continue
        output.append(character)
    return "".join(output).strip(" |¦")


def should_remove_space(previous: str, following: str) -> bool:
    return (
        (is_cjk(previous) and is_cjk(following))
        or is_punctuation(previous)
        or is_punctuation(following)
    )


def is_cjk(character: str) -> bool:
    codepoint = ord(character)
    return (
        0x3400 <= codepoint <= 0x4DBF
        or 0x4E00 <= codepoint <= 0x9FFF
        or 0xF900 <= codepoint <= 0xFAFF
    )


def is_punctuation(character: str) -> bool:
    import unicodedata

    return unicodedata.category(character).startswith("P")


def safe_filename(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", value).strip("._") or "fixture"


def results_json_path(output_path: Path) -> Path:
    return output_path.with_name(f"{output_path.stem}.results.json")


def prepare_crops(manifest_path: Path, crop_directory: Path) -> list[dict[str, Any]]:
    from PIL import Image

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    fixtures = manifest.get("fixtures", [])
    if not fixtures:
        raise ValueError("Benchmark manifest has no fixtures.")

    crop_directory.mkdir(parents=True, exist_ok=True)
    prepared: list[dict[str, Any]] = []
    crop_paths: set[Path] = set()
    for item in fixtures:
        fixture = str(item["name"])
        expected = normalize_text(str(item["expected"]))
        crop_path = crop_directory / f"{safe_filename(fixture)}.png"
        if crop_path in crop_paths:
            raise ValueError(
                f"Fixture '{fixture}' maps to duplicate crop name '{crop_path.name}'."
            )
        crop_paths.add(crop_path)
        if "crop" in item:
            source_path = (manifest_path.parent / item["crop"]).resolve()
            with Image.open(source_path) as image:
                image.convert("RGB").save(crop_path)
        else:
            source_path = (manifest_path.parent / item["image"]).resolve()
            x = int(item["x"])
            y = int(item["y"])
            width = int(item["width"])
            height = int(item["height"])
            if width <= 0 or height <= 0:
                raise ValueError(f"Fixture '{fixture}' has an empty crop.")
            with Image.open(source_path) as image:
                if x < 0 or y < 0 or x + width > image.width or y + height > image.height:
                    raise ValueError(f"Fixture '{fixture}' crop is outside its source image.")
                image.convert("RGB").crop((x, y, x + width, y + height)).save(crop_path)

        prepared.append(
            {
                "fixture": fixture,
                "group": str(item.get("group", "representative")),
                "expected": expected,
                "crop_path": str(crop_path),
            }
        )
    return prepared


def synchronize_gpu(device: str) -> None:
    if device.startswith("gpu"):
        import paddle

        paddle.device.synchronize()


def benchmark_model(
    model_name: str,
    fixtures: list[dict[str, Any]],
    device: str,
    warmup_count: int,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    import paddle
    import paddleocr
    from paddleocr import TextRecognition

    model = TextRecognition(model_name=model_name, device=device)
    for _ in range(warmup_count):
        list(model.predict(input=fixtures[0]["crop_path"], batch_size=1))
    synchronize_gpu(device)

    rows: list[dict[str, Any]] = []
    for fixture in fixtures:
        synchronize_gpu(device)
        started = time.perf_counter()
        output = list(model.predict(input=fixture["crop_path"], batch_size=1))
        synchronize_gpu(device)
        elapsed_ms = (time.perf_counter() - started) * 1000
        if len(output) != 1:
            raise RuntimeError(
                f"{model_name} returned {len(output)} results for {fixture['fixture']}."
            )
        result = output[0]
        recognized_raw = str(result["rec_text"])
        recognized = normalize_text(recognized_raw)
        expected = fixture["expected"]
        distance = edit_distance(expected, recognized)
        rows.append(
            {
                "engine": model_name,
                "fixture": fixture["fixture"],
                "group": fixture["group"],
                "expected": expected,
                "recognized": recognized,
                "recognized_raw": recognized_raw,
                "rec_score": float(result["rec_score"]),
                "exact_match": expected == recognized,
                "edit_distance": distance,
                "cer": (
                    distance / len(expected)
                    if expected
                    else (0.0 if not recognized else 1.0)
                ),
                "elapsed_ms": elapsed_ms,
            }
        )

    metadata = {
        "model": model_name,
        "device_requested": device,
        "device_active": paddle.device.get_device(),
        "paddle_version": paddle.__version__,
        "paddleocr_version": paddleocr.__version__,
        "warmup_count": warmup_count,
    }
    del model
    if device.startswith("gpu"):
        paddle.device.cuda.empty_cache()
    return rows, metadata


def markdown_cell(value: Any) -> str:
    return str(value).replace("|", "¦").replace("\r", " ").replace("\n", " ")


def render_markdown(
    results: list[dict[str, Any]], high_score_threshold: float
) -> str:
    lines = [
        "# PaddleOCR recognition-only benchmark",
        "",
        "> `rec_score` is Paddle model output, not a calibrated probability and not directly comparable to DetectionScore, Tesseract confidence, or future Jev probability.",
        "",
        "Text detection was not used. Inputs are existing capture-relative WeChat bubble/quote crops.",
        "",
        "## Summary",
        "",
        "| engine | short_exact | short_count | short_corpus_cer | overall_exact | overall_count | high_score_wrong | mean_ms | p50_ms | p95_ms |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for result in results:
        summary = result["summary"]
        lines.append(
            f"| {result['model']} | {summary['short_exact_match']} | {summary['short_count']} | "
            f"{summary['short_corpus_cer']:.3f} | {summary['overall_exact_match']} | "
            f"{summary['overall_count']} | {summary['high_score_wrong_count']} | "
            f"{summary['latency_mean_ms']:.1f} | {summary['latency_p50_ms']:.1f} | "
            f"{summary['latency_p95_ms']:.1f} |"
        )
    lines.append("")

    for result in results:
        lines.extend(
            [
                f"## {result['model']}",
                "",
                f"Device: `{result['metadata']['device_active']}`. Warmups excluded: {result['metadata']['warmup_count']}.",
                "",
                "| fixture | expected | recognized | rec_score | exact_match | CER | elapsed_ms |",
                "| --- | --- | --- | ---: | --- | ---: | ---: |",
            ]
        )
        for row in result["rows"]:
            lines.append(
                f"| {markdown_cell(row['fixture'])} | {markdown_cell(row['expected'])} | "
                f"{markdown_cell(row['recognized'])} | {row['rec_score']:.3f} | "
                f"{str(row['exact_match']).lower()} | {row['cer']:.3f} | "
                f"{row['elapsed_ms']:.1f} |"
            )
        wrong = result["summary"]["high_score_wrong_fixtures"]
        lines.extend(
            [
                "",
                f"Wrong results with `rec_score >= {high_score_threshold:.2f}`: "
                + (", ".join(f"`{name}`" for name in wrong) if wrong else "none"),
                "",
            ]
        )
    return "\n".join(lines)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Benchmark PaddleOCR TextRecognition on pre-detected WeChat crops."
    )
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device", default="gpu:0")
    parser.add_argument("--models", nargs="+", default=list(DEFAULT_MODELS))
    parser.add_argument("--warmup-count", type=int, default=1)
    parser.add_argument("--high-score-threshold", type=float, default=0.90)
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    if arguments.warmup_count < 0:
        raise ValueError("--warmup-count must be non-negative.")
    if not 0 <= arguments.high_score_threshold <= 1:
        raise ValueError("--high-score-threshold must be between zero and one.")

    os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "BOS")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    manifest_path = arguments.manifest.resolve()
    output_path = arguments.output.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    crop_directory = output_path.parent / f"{output_path.stem}-crops"
    fixtures = prepare_crops(manifest_path, crop_directory)

    results: list[dict[str, Any]] = []
    for model_name in arguments.models:
        rows, metadata = benchmark_model(
            model_name,
            fixtures,
            arguments.device,
            arguments.warmup_count,
        )
        results.append(
            {
                "model": model_name,
                "metadata": metadata,
                "summary": summarize_rows(rows, arguments.high_score_threshold),
                "rows": rows,
            }
        )

    report = {
        "manifest": str(manifest_path),
        "confidence_semantics": "rec_score is uncalibrated and not cross-engine comparable",
        "high_score_diagnostic_threshold": arguments.high_score_threshold,
        "results": results,
    }
    json_path = results_json_path(output_path)
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    output_path.write_text(
        render_markdown(results, arguments.high_score_threshold), encoding="utf-8"
    )
    print(f"PaddleOCR benchmark report: {output_path}")
    print(f"PaddleOCR benchmark JSON: {json_path}")
    print(f"PaddleOCR crop artifacts: {crop_directory}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
