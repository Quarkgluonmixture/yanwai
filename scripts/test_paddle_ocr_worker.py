import base64
import io
import json
import unittest
from unittest.mock import patch

from PIL import Image

from scripts.paddle_ocr_worker import serve


class FakeResult:
    def __getitem__(self, key):
        return {"rec_text": "好", "rec_score": 0.999}[key]


class FakeModel:
    def __init__(self):
        self.calls = 0

    def predict(self, input, batch_size):
        self.calls += 1
        return [FakeResult()]


def png_base64():
    output = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(output, format="PNG")
    return base64.b64encode(output.getvalue()).decode("ascii")


class PaddleWorkerProtocolTests(unittest.TestCase):
    @patch("scripts.paddle_ocr_worker.synchronize")
    def test_multiple_requests_keep_one_model_and_correlate_ids(self, _synchronize):
        requests = "\n".join(
            json.dumps(
                {
                    "type": "recognize",
                    "request_id": request_id,
                    "image_base64": png_base64(),
                }
            )
            for request_id in ("first", "second")
        ) + "\n" + json.dumps({"type": "shutdown"}) + "\n"
        output = io.StringIO()
        model = FakeModel()

        serve(model, "cpu", io.StringIO(requests), output)

        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(2, model.calls)
        self.assertEqual(["first", "second"], [item["request_id"] for item in responses[:2]])
        self.assertEqual("shutdown_ack", responses[2]["type"])


if __name__ == "__main__":
    unittest.main()
