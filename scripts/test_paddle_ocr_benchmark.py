import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from scripts.paddle_ocr_benchmark import (
    build_evaluation_row,
    character_error_rate,
    normalize_text,
    prepare_crops,
    render_markdown,
    results_json_path,
    summarize_rows,
)


class PaddleOcrBenchmarkTests(unittest.TestCase):
    def test_evaluation_preserves_raw_and_normalized_layers(self):
        english = build_evaluation_row(
            "model",
            {
                "fixture": "english",
                "group": "real_english",
                "expected": "Hello OCR test 123, I just got home.",
            },
            "Hello OCR test 123,I just got home.",
            rec_score=0.962,
            elapsed_ms=6.9,
        )

        self.assertEqual(
            english["raw_recognized"], "Hello OCR test 123,I just got home."
        )
        self.assertEqual(
            english["normalized_recognized"],
            "Hello OCR test 123,I just got home.",
        )
        self.assertFalse(english["exact_match"])
        self.assertFalse(english["raw_exact_match"])
        self.assertFalse(english["normalized_match"])
        self.assertEqual(english["raw_cer"], 1 / 36)
        self.assertEqual(english["normalized_cer"], 1 / 36)

        cjk = build_evaluation_row(
            "model",
            {"fixture": "cjk", "group": "short_chinese", "expected": "中文"},
            "中 文",
            rec_score=0.9,
            elapsed_ms=1.0,
        )
        self.assertFalse(cjk["raw_exact_match"])
        self.assertTrue(cjk["normalized_match"])
        self.assertEqual(cjk["raw_cer"], 0.5)
        self.assertEqual(cjk["normalized_cer"], 0)

    def test_prepare_crops_preserves_the_exact_requested_pixels(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            source_path = root / "source.png"
            source = Image.new("RGB", (4, 3))
            source.putdata(
                [(x * 40, y * 60, x + y) for y in range(3) for x in range(4)]
            )
            source.save(source_path)
            manifest_path = root / "manifest.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "fixtures": [
                            {
                                "name": "crop",
                                "expected": "测试",
                                "image": "source.png",
                                "x": 1,
                                "y": 1,
                                "width": 2,
                                "height": 1,
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            fixtures = prepare_crops(manifest_path, root / "crops")

            with Image.open(fixtures[0]["crop_path"]) as crop:
                self.assertEqual(crop.size, (2, 1))
                self.assertEqual(
                    crop.convert("RGB").tobytes(),
                    source.crop((1, 1, 3, 2)).tobytes(),
                )

    def test_prepare_crops_rejects_sanitized_filename_collisions(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            Image.new("RGB", (2, 2)).save(root / "source.png")
            manifest_path = root / "manifest.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "fixtures": [
                            {"name": "a/b", "expected": "a", "crop": "source.png"},
                            {"name": "a?b", "expected": "b", "crop": "source.png"},
                        ]
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "duplicate crop name"):
                prepare_crops(manifest_path, root / "crops")

    def test_normalization_is_cjk_aware_and_preserves_latin_punctuation_spacing(self):
        self.assertEqual(normalize_text("  一直 异地， sos  "), "一直异地， sos")
        self.assertEqual(
            normalize_text("Hello OCR test 123, I just got home."),
            "Hello OCR test 123, I just got home.",
        )
        self.assertEqual(normalize_text("中 文 ， 测试"), "中文，测试")
        self.assertEqual(normalize_text("中文 ＡＢＣ"), "中文 ＡＢＣ")
        self.assertEqual(normalize_text("s 。 s"), "s 。 s")
        self.assertEqual(normalize_text("Hello,¦ I"), "Hello, I")

    def test_results_json_path_cannot_overwrite_same_stem_manifest(self):
        self.assertEqual(
            results_json_path(Path("phase3-paddle-benchmark.md")),
            Path("phase3-paddle-benchmark.results.json"),
        )

    def test_markdown_report_labels_raw_and_normalized_metrics_explicitly(self):
        row = build_evaluation_row(
            "model",
            {"fixture": "english", "group": "real_english", "expected": "123, text"},
            "123,\ntext",
            rec_score=0.96,
            elapsed_ms=2.0,
        )
        result = {
            "model": "model",
            "metadata": {"device_active": "gpu:0", "warmup_count": 1},
            "summary": summarize_rows([row], 0.90),
            "rows": [row],
        }

        report = render_markdown([result], 0.90)

        self.assertIn("raw_recognized", report)
        self.assertIn("normalized_recognized", report)
        self.assertIn("raw_exact_match", report)
        self.assertIn("normalized_match", report)
        self.assertIn("raw_CER", report)
        self.assertIn("normalized_CER", report)
        self.assertIn(r"123,\ntext", report)

    def test_character_error_rate_uses_expected_character_count(self):
        self.assertEqual(character_error_rate("怎么说", "怎久说"), 1 / 3)
        self.assertEqual(character_error_rate("", ""), 0)
        self.assertEqual(character_error_rate("", "extra"), 1)

    def test_summary_reports_raw_and_normalized_accuracy_high_score_errors_and_latency(self):
        rows = [
            {
                "fixture": "short_exact",
                "group": "short_chinese",
                "expected": "好",
                "rec_score": 0.99,
                "exact_match": True,
                "raw_exact_match": True,
                "normalized_match": True,
                "raw_edit_distance": 0,
                "normalized_edit_distance": 0,
                "elapsed_ms": 10.0,
            },
            {
                "fixture": "short_normalized_only",
                "group": "short_chinese",
                "expected": "中文",
                "rec_score": 0.95,
                "exact_match": False,
                "raw_exact_match": False,
                "normalized_match": True,
                "raw_edit_distance": 1,
                "normalized_edit_distance": 0,
                "elapsed_ms": 20.0,
            },
            {
                "fixture": "representative_exact",
                "group": "representative",
                "expected": "mixed text",
                "rec_score": 0.88,
                "exact_match": True,
                "raw_exact_match": True,
                "normalized_match": True,
                "raw_edit_distance": 0,
                "normalized_edit_distance": 0,
                "elapsed_ms": 30.0,
            },
        ]

        summary = summarize_rows(rows, high_score_threshold=0.90)

        self.assertEqual(summary["short_raw_exact_match"], 1)
        self.assertEqual(summary["short_normalized_match"], 2)
        self.assertEqual(summary["short_count"], 2)
        self.assertEqual(summary["short_raw_corpus_cer"], 1 / 3)
        self.assertEqual(summary["short_normalized_corpus_cer"], 0)
        self.assertEqual(summary["overall_raw_exact_match"], 2)
        self.assertEqual(summary["overall_normalized_match"], 3)
        self.assertEqual(summary["overall_count"], 3)
        self.assertEqual(summary["high_score_wrong_count"], 1)
        self.assertEqual(summary["latency_mean_ms"], 20.0)
        self.assertEqual(summary["latency_p50_ms"], 20.0)
        self.assertEqual(summary["latency_p95_ms"], 29.0)


if __name__ == "__main__":
    unittest.main()
