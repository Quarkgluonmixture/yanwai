import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from scripts.paddle_ocr_benchmark import (
    character_error_rate,
    normalize_text,
    prepare_crops,
    results_json_path,
    summarize_rows,
)


class PaddleOcrBenchmarkTests(unittest.TestCase):
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

    def test_normalization_matches_the_dotnet_evaluator_rules(self):
        self.assertEqual(normalize_text("  一直 异地， sos  "), "一直异地，sos")

    def test_results_json_path_cannot_overwrite_same_stem_manifest(self):
        self.assertEqual(
            results_json_path(Path("phase3-paddle-benchmark.md")),
            Path("phase3-paddle-benchmark.results.json"),
        )

    def test_character_error_rate_uses_expected_character_count(self):
        self.assertEqual(character_error_rate("怎么说", "怎久说"), 1 / 3)
        self.assertEqual(character_error_rate("", ""), 0)
        self.assertEqual(character_error_rate("", "extra"), 1)

    def test_summary_reports_short_and_overall_accuracy_high_score_errors_and_latency(self):
        rows = [
            {
                "fixture": "short_exact",
                "group": "short_chinese",
                "expected": "好",
                "recognized": "好",
                "rec_score": 0.99,
                "exact_match": True,
                "edit_distance": 0,
                "elapsed_ms": 10.0,
            },
            {
                "fixture": "short_wrong",
                "group": "short_chinese",
                "expected": "晚安",
                "recognized": "晚按",
                "rec_score": 0.95,
                "exact_match": False,
                "edit_distance": 1,
                "elapsed_ms": 20.0,
            },
            {
                "fixture": "representative_exact",
                "group": "representative",
                "expected": "mixed text",
                "recognized": "mixed text",
                "rec_score": 0.88,
                "exact_match": True,
                "edit_distance": 0,
                "elapsed_ms": 30.0,
            },
        ]

        summary = summarize_rows(rows, high_score_threshold=0.90)

        self.assertEqual(summary["short_exact_match"], 1)
        self.assertEqual(summary["short_count"], 2)
        self.assertEqual(summary["short_corpus_cer"], 1 / 3)
        self.assertEqual(summary["overall_exact_match"], 2)
        self.assertEqual(summary["overall_count"], 3)
        self.assertEqual(summary["high_score_wrong_count"], 1)
        self.assertEqual(summary["latency_mean_ms"], 20.0)
        self.assertEqual(summary["latency_p50_ms"], 20.0)
        self.assertEqual(summary["latency_p95_ms"], 29.0)


if __name__ == "__main__":
    unittest.main()
