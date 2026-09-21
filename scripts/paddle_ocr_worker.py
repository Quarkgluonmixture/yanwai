#!/usr/bin/env python3
"""Persistent JSON-lines Unified Paddle bubble extraction worker.

Standard output is reserved for protocol messages. All diagnostics go to stderr.
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import io
import json
import sys
import time
from typing import Any, TextIO


try:
    from scripts.paddle_bubble_extraction import boxes_from_polygons, compose
except ModuleNotFoundError:
    from paddle_bubble_extraction import boxes_from_polygons, compose

PROTOCOL_VERSION = 2
DEFAULT_MODEL = "PP-OCRv6_small_rec"
DETECTOR_MODEL = "PP-OCRv6_small_det"


def protocol_write(output: TextIO, payload: dict[str, Any]) -> None:
    output.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    output.flush()


def decode_image(image_base64: str) -> Any:
    import numpy as np
    from PIL import Image

    image_bytes = base64.b64decode(image_base64, validate=True)
    with Image.open(io.BytesIO(image_bytes)) as image:
        return np.asarray(image.convert("RGB"))


def synchronize(device: str) -> None:
    if device.startswith("gpu"):
        import paddle

        paddle.device.synchronize()


def recognize(model: Any, image: Any, device: str) -> tuple[str, float, float]:
    synchronize(device)
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        results = list(model.predict(input=image, batch_size=1))
    synchronize(device)
    elapsed_ms = (time.perf_counter() - started) * 1000
    if len(results) != 1:
        raise RuntimeError(f"Recognition returned {len(results)} results; expected one.")
    result = results[0]
    return str(result["rec_text"]), float(result["rec_score"]), elapsed_ms


def serve(
    model: Any,
    device: str,
    input_stream: TextIO,
    output_stream: TextIO,
    detector: Any,
) -> None:
    for line in input_stream:
        request = {}
        try:
            request = json.loads(line)
            request_type = request.get("type")
            if request_type == "shutdown":
                protocol_write(output_stream, {"type": "shutdown_ack"})
                return
            if request_type != "recognize":
                raise ValueError("Unsupported request type.")
            request_id = str(request["request_id"])
            started = time.perf_counter()
            image = decode_image(str(request["image_base64"]))
            result = extract(detector, model, image, device)
            result['worker_total_ms'] = (time.perf_counter() - started) * 1000
            protocol_write(
                output_stream,
                {
                    "type": "result",
                    "request_id": request_id,
                    **result,
                },
            )
        except Exception as exception:  # protocol must survive per-request failures
            request_id = None
            try:
                request_id = request.get("request_id")
            except (NameError, AttributeError):
                pass
            protocol_write(
                output_stream,
                {
                    "type": "error",
                    "request_id": request_id,
                    "error_code": type(exception).__name__,
                    "message": "Paddle extraction failed: " + type(exception).__name__,
                },
            )


def detect(detector, image, device):
    synchronize(device)
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        results = list(detector.predict(input=image, batch_size=1))
    synchronize(device)
    elapsed = (time.perf_counter() - started) * 1000
    if len(results) != 1:
        raise RuntimeError('Detection must return one result')
    boxes = boxes_from_polygons(results[0]['dt_polys'], image.shape[1], image.shape[0])
    return boxes, elapsed


def extract(detector, recognizer, image, device):
    boxes, detection_ms = detect(detector, image, device)
    inputs = [image] if len(boxes) <= 1 else [image[y1:y2, x1:x2] for x1,y1,x2,y2 in boxes]
    lines = []
    recognition_ms = 0
    for crop in inputs:
        text, score, elapsed = recognize(recognizer, crop, device)
        lines.append(dict(raw_text=text, rec_score=score))
        recognition_ms += elapsed
    return dict(raw_text=compose([line['raw_text'] for line in lines]),
                rec_score=lines[0]['rec_score'] if len(boxes) <= 1 else None,
                detected_line_count=len(boxes), line_boxes=boxes, lines=lines,
                detection_ms=detection_ms, recognition_ms=recognition_ms,
                inference_ms=detection_ms + recognition_ms)


def load_runtime(model_name: str, device: str, warmup_count: int):
    startup_started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import paddle
        import paddleocr
        from paddleocr import TextDetection, TextRecognition

        detector = TextDetection(model_name=DETECTOR_MODEL, device=device)
        model = TextRecognition(model_name=model_name, device=device)
    startup_ms = (time.perf_counter() - startup_started) * 1000

    warmup_started = time.perf_counter()
    warmup_image = np.full((48, 256, 3), 255, dtype=np.uint8)
    for _ in range(warmup_count):
        detect(detector, warmup_image, device)
        recognize(model, warmup_image, device)
    warmup_ms = (time.perf_counter() - warmup_started) * 1000
    return detector, model, {
        "type": "ready",
        "protocol_version": PROTOCOL_VERSION,
        "model_name": model_name,
        "detector_model": DETECTOR_MODEL,
        "recognizer_model": model_name,
        "paddleocr_version": paddleocr.__version__,
        "paddlepaddle_version": paddle.__version__,
        "device_requested": device,
        "device_active": paddle.device.get_device(),
        "startup_ms": startup_ms,
        "warmup_ms": warmup_ms,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--device", default="gpu:0")
    parser.add_argument("--warmup-count", type=int, default=1)
    return parser.parse_args()


def main() -> int:
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    args = parse_args()
    try:
        detector, model, ready = load_runtime(args.model, args.device, args.warmup_count)
        protocol_write(sys.stdout, ready)
        serve(model, args.device, sys.stdin, sys.stdout, detector)
        return 0
    except Exception as exception:
        protocol_write(
            sys.stdout,
            {
                "type": "startup_error",
                "error_code": type(exception).__name__,
                "message": str(exception),
            },
        )
        print(f"Paddle worker startup failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
