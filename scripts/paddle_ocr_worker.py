#!/usr/bin/env python3
"""Persistent JSON-lines PaddleOCR recognition-only worker.

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


PROTOCOL_VERSION = 1
DEFAULT_MODEL = "PP-OCRv6_small_rec"


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
) -> None:
    for line in input_stream:
        try:
            request = json.loads(line)
            request_type = request.get("type")
            if request_type == "shutdown":
                protocol_write(output_stream, {"type": "shutdown_ack"})
                return
            if request_type != "recognize":
                raise ValueError("Unsupported request type.")
            request_id = str(request["request_id"])
            image = decode_image(str(request["image_base64"]))
            raw_text, rec_score, inference_ms = recognize(model, image, device)
            protocol_write(
                output_stream,
                {
                    "type": "result",
                    "request_id": request_id,
                    "raw_text": raw_text,
                    "rec_score": rec_score,
                    "inference_ms": inference_ms,
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
                    "message": str(exception),
                },
            )


def load_runtime(model_name: str, device: str, warmup_count: int) -> tuple[Any, dict[str, Any]]:
    startup_started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import paddle
        import paddleocr
        from paddleocr import TextRecognition

        model = TextRecognition(model_name=model_name, device=device)
    startup_ms = (time.perf_counter() - startup_started) * 1000

    warmup_started = time.perf_counter()
    warmup_image = np.full((48, 256, 3), 255, dtype=np.uint8)
    for _ in range(warmup_count):
        recognize(model, warmup_image, device)
    warmup_ms = (time.perf_counter() - warmup_started) * 1000
    return model, {
        "type": "ready",
        "protocol_version": PROTOCOL_VERSION,
        "model_name": model_name,
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
        model, ready = load_runtime(args.model, args.device, args.warmup_count)
        protocol_write(sys.stdout, ready)
        serve(model, args.device, sys.stdin, sys.stdout)
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
