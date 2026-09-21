import base64
import io
import json
import unittest
from unittest.mock import patch

from PIL import Image

from scripts.paddle_ocr_worker import serve, extract
from scripts.paddle_bubble_extraction import compose, boxes_from_polygons
import numpy as np
from types import SimpleNamespace
from scripts.paddle_ocr_worker import load_runtime


class FakeResult:
    def __getitem__(self, key):
        return {"rec_text": "好", "rec_score": 0.999}[key]


class FakeModel:
    def __init__(self):
        self.calls = 0

    def predict(self, input, batch_size):
        self.calls += 1
        return [FakeResult()]


class Detector:
    def __init__(self, boxes=()):
        self.boxes = boxes
        self.calls = 0

    def predict(self, input, batch_size):
        self.calls += 1
        return [dict(dt_polys=[[(x1,y1),(x2,y1),(x2,y2),(x1,y2)] for x1,y1,x2,y2 in self.boxes])]


def png_base64():
    output = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(output, format="PNG")
    return base64.b64encode(output.getvalue()).decode("ascii")


class PaddleWorkerProtocolTests(unittest.TestCase):
    def test_both_models_load_and_warm_once_with_explicit_ready(self):
        detector, recognizer = Detector(), FakeModel()
        from unittest.mock import Mock
        det_factory, rec_factory = Mock(return_value=detector), Mock(return_value=recognizer)
        paddle = SimpleNamespace(__version__='test', device=SimpleNamespace(get_device=lambda: 'cpu'))
        paddleocr = SimpleNamespace(__version__='test', TextDetection=det_factory, TextRecognition=rec_factory)
        with patch.dict('sys.modules', paddle=paddle, paddleocr=paddleocr):
            loaded_det, loaded_rec, ready = load_runtime('PP-OCRv6_small_rec', 'cpu', 1)
        self.assertIs(detector, loaded_det)
        self.assertIs(recognizer, loaded_rec)
        det_factory.assert_called_once_with(model_name='PP-OCRv6_small_det', device='cpu')
        rec_factory.assert_called_once_with(model_name='PP-OCRv6_small_rec', device='cpu')
        self.assertEqual(1, detector.calls)
        self.assertEqual(1, recognizer.calls)
        self.assertEqual(2, ready['protocol_version'])
        self.assertEqual('PP-OCRv6_small_det', ready['detector_model'])
        self.assertEqual('PP-OCRv6_small_rec', ready['recognizer_model'])

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

        detector = Detector()
        serve(model, "cpu", io.StringIO(requests), output, detector)

        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(2, model.calls)
        self.assertEqual(2, detector.calls)
        self.assertEqual(["first", "second"], [item["request_id"] for item in responses[:2]])
        self.assertEqual("shutdown_ack", responses[2]["type"])

    def test_zero_one_and_changed_one_box_use_identical_original_pixels(self):
        image = np.arange(20*30*3, dtype=np.uint8).reshape(20,30,3)
        for boxes in ([], [[1,2,8,9]], [[5,6,18,19]]):
            with patch('scripts.paddle_ocr_worker.recognize', return_value=('好', .999, 1)) as rec:
                result = extract(Detector(boxes), object(), image, 'cpu')
                self.assertIs(image, rec.call_args.args[1])
                self.assertEqual('好', result['raw_text'])
                self.assertEqual(len(boxes), result['detected_line_count'])

    def test_multiline_clips_sorts_and_uses_exact_pixels(self):
        image = np.arange(20*30*3, dtype=np.uint8).reshape(20,30,3)
        with patch('scripts.paddle_ocr_worker.recognize', side_effect=[('Hello',.9,2),('world',.8,3)]) as rec:
            result = extract(Detector([[2,10,40,22],[-2,-1,28,9]]), object(), image, 'cpu')
            self.assertEqual([[0,0,28,9],[2,10,30,20]], result['line_boxes'])
            np.testing.assert_array_equal(image[0:9,0:28], rec.call_args_list[0].args[1])
            np.testing.assert_array_equal(image[10:20,2:30], rec.call_args_list[1].args[1])
            self.assertEqual('Hello world', result['raw_text'])
            self.assertIsNone(result['rec_score'])
            self.assertEqual(5, result['recognition_ms'])

    def test_chinese_and_latin_composition_matches_benchmark(self):
        from scripts.paddle_bubble_benchmark import compose as benchmark_compose
        for lines, expected in [(['中文','换行'],'中文换行'), (['Hello','world.'],'Hello world.'),
                                (['中文','English'],'中文English'), (['Hello ','world'],'Hello world')]:
            self.assertEqual(expected, compose(lines))
            self.assertEqual(benchmark_compose(lines), compose(lines))

    def test_invalid_box_fails_instead_of_silently_dropping_text(self):
        with self.assertRaises(ValueError):
            boxes_from_polygons([[(50,50),(60,60)]], 20,20)

    def test_detector_and_recognizer_failure_are_correlated_errors(self):
        for target in ('detect','recognize'):
            request = json.dumps(dict(type='recognize',request_id='bad',image_base64=png_base64()))+'\n'
            output = io.StringIO()
            with patch('scripts.paddle_ocr_worker.'+target, side_effect=RuntimeError('private text')):
                serve(FakeModel(), 'cpu', io.StringIO(request), output, Detector())
            result = json.loads(output.getvalue())
            self.assertEqual('error', result['type'])
            self.assertEqual('bad', result['request_id'])
            self.assertNotIn('private text', output.getvalue())


if __name__ == "__main__":
    unittest.main()
