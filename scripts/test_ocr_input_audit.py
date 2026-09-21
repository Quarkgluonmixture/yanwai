import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from scripts.ocr_input_audit import (
    build_audit_row,
    detect_text_roi,
    generate_variants,
    prepare_audit_fixtures,
    render_audit_markdown,
    render_inspection_index,
    summarize_variant_rows,
)


class OcrInputAuditTests(unittest.TestCase):
    def test_text_roi_excludes_border_background_and_preserves_safe_padding(self):
        image = Image.new("RGB", (80, 36), (24, 24, 24))
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((3, 2, 76, 33), radius=8, fill=(48, 203, 142))
        draw.rectangle((24, 11, 29, 24), fill=(18, 18, 18))
        draw.rectangle((36, 11, 41, 24), fill=(18, 18, 18))

        evidence = detect_text_roi(image, padding=4)

        self.assertEqual(evidence.text_bounds, (24, 11, 42, 25))
        self.assertEqual(evidence.padded_bounds, (20, 7, 46, 29))
        self.assertEqual(evidence.text_band_height, 14)
        self.assertEqual(evidence.background_rgb, (48, 203, 142))

    def test_variants_compare_geometry_scale_interpolation_and_gray_normalization(self):
        image = Image.new("RGB", (80, 36), (48, 203, 142))
        draw = ImageDraw.Draw(image)
        draw.rectangle((24, 11, 29, 24), fill=(18, 18, 18))
        draw.rectangle((36, 11, 41, 24), fill=(18, 18, 18))
        evidence = detect_text_roi(image, padding=4)

        variants = generate_variants(image, evidence, target_text_heights=[32])

        self.assertEqual(
            [variant.name for variant in variants],
            [
                "raw_whole",
                "text_roi",
                "height32_nearest",
                "height32_bicubic",
                "height32_lanczos",
                "height32_lanczos_gray",
            ],
        )
        self.assertEqual(variants[0].image.size, (80, 36))
        self.assertEqual(variants[1].image.size, (26, 22))
        for variant in variants[2:]:
            self.assertEqual(variant.image.size, (59, 50))
        self.assertEqual(variants[-1].image.mode, "RGB")
        self.assertEqual(variants[-1].interpolation, "lanczos")
        self.assertEqual(variants[-1].background_normalization, "grayscale_linear")

    def test_manifest_preparation_records_geometry_dpi_route_and_private_artifacts(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            crop_path = root / "bubble.png"
            image = Image.new("RGB", (80, 36), (48, 203, 142))
            draw = ImageDraw.Draw(image)
            draw.rectangle((24, 11, 29, 24), fill=(18, 18, 18))
            draw.rectangle((36, 11, 41, 24), fill=(18, 18, 18))
            image.save(crop_path)
            manifest_path = root / "manifest.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "Fixtures": [
                            {
                                "Name": "real/fixture",
                                "Crop": "bubble.png",
                                "Expected": "测试",
                                "CaptureDpi": 144,
                                "OcrRoute": "PaddleSingleLine",
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            fixtures = prepare_audit_fixtures(
                [manifest_path],
                root / "artifacts",
                padding=4,
                target_text_heights=[32, 40, 48],
                artifact_target_height=40,
            )

            fixture = fixtures[0]
            self.assertEqual(fixture["dpi"], 144)
            self.assertEqual(fixture["route"], "PaddleSingleLine")
            self.assertEqual(fixture["bubble_size"], [80, 36])
            self.assertEqual(fixture["text_roi_size"], [26, 22])
            self.assertEqual(fixture["text_band_height"], 14)
            artifact_directory = root / "artifacts" / "real_fixture"
            self.assertTrue((artifact_directory / "raw_crop.png").is_file())
            self.assertTrue((artifact_directory / "text_roi.png").is_file())
            self.assertTrue((artifact_directory / "normalized_lanczos.png").is_file())
            self.assertTrue((artifact_directory / "normalized_gray.png").is_file())
            self.assertEqual(len(fixture["variants"]), 14)

    def test_audit_rows_and_summary_keep_variant_evidence_separate(self):
        fixture = {
            "fixture": "english-150",
            "expected": "Hello OCR test 123",
            "dpi": 144,
            "route": "PaddleSingleLine",
            "bubble_size": [180, 42],
            "text_roi_size": [164, 28],
            "text_band_height": 18,
        }
        exact = build_audit_row(
            fixture,
            "height40_lanczos",
            "Hello OCR test 123",
            rec_score=0.98,
            inference_ms=8.5,
        )
        wrong = build_audit_row(
            fixture,
            "raw_whole",
            "H引0 OCR test 123",
            rec_score=0.99,
            inference_ms=9.5,
        )

        self.assertTrue(exact["raw_exact_match"])
        self.assertEqual(exact["dpi"], 144)
        self.assertEqual(exact["bubble_width"], 180)
        self.assertEqual(exact["text_roi_height"], 28)
        self.assertFalse(wrong["normalized_match"])
        self.assertGreater(wrong["raw_cer"], 0)

        summary = summarize_variant_rows([exact, wrong])

        self.assertEqual(summary["height40_lanczos"]["raw_exact"], 1)
        self.assertEqual(summary["raw_whole"]["raw_exact"], 0)
        self.assertEqual(summary["raw_whole"]["count"], 1)

    def test_markdown_reports_variant_and_dpi_evidence_without_confidence_claims(self):
        fixture = {
            "fixture": "english-150",
            "expected": "Hello OCR test 123",
            "dpi": 144,
            "route": "PaddleSingleLine",
            "bubble_size": [180, 42],
            "text_roi_size": [164, 28],
            "text_band_height": 18,
        }
        row = build_audit_row(
            fixture,
            "height40_lanczos",
            "Hello OCR test 123",
            rec_score=0.98,
            inference_ms=8.5,
        )

        report = render_audit_markdown(
            [row],
            {
                "model": "PP-OCRv6_small_rec",
                "device_active": "gpu:0",
                "paddle_version": "3.test",
                "paddleocr_version": "3.test",
                "startup_ms": 123.4,
                "warmup_ms": 45.6,
                "warmup_count": 1,
            },
        )

        self.assertIn("rec_score` is uncalibrated", report)
        self.assertIn("Preprocessing variants are not independent OCR engines", report)
        self.assertIn("height40_lanczos", report)
        self.assertIn("144 DPI", report)
        self.assertIn("180x42", report)
        self.assertIn("164x28", report)

    def test_inspection_index_maps_private_artifacts_to_expected_text_and_dpi(self):
        fixtures = [
            {
                "fixture": "english-150",
                "expected": "Hello OCR test 123",
                "dpi": 144,
                "route": "PaddleSingleLine",
                "artifact_directory": "/private/artifacts/english-150",
            }
        ]

        index = render_inspection_index(fixtures)

        self.assertIn("Hello OCR test 123", index)
        self.assertIn("144", index)
        self.assertIn("english-150/raw_crop.png", index)
        self.assertIn("english-150/text_roi.png", index)
        self.assertIn("english-150/normalized_lanczos.png", index)
        self.assertIn("english-150/normalized_gray.png", index)


if __name__ == "__main__":
    unittest.main()
