import unittest
import json
import tempfile
from pathlib import Path
from scripts.paddle_bubble_benchmark import compose, boxes_from_polygons, compare


class BubbleBenchmarkTests(unittest.TestCase):
    def test_composition_keeps_latin_spacing_and_cjk_wraps(self):
        self.assertEqual(compose(['Hello OCR', 'test 123']), 'Hello OCR test 123')
        self.assertEqual(compose(['今天不', '可以']), '今天不可以')
        self.assertEqual(compose(['123,', 'I got home.']), '123, I got home.')
        self.assertEqual(compose([]), '')

    def test_boxes_are_ordered_and_clipped_to_bubble(self):
        polygons = [[[2,20],[40,20],[40,30],[2,30]], [[-1,2],[30,2],[30,12],[-1,12]]]
        self.assertEqual(boxes_from_polygons(polygons,35,25), [[0,2,30,12],[2,20,35,25]])

    def test_invalid_line_is_not_silently_dropped(self):
        with self.assertRaises(ValueError):
            boxes_from_polygons([[[40,20],[50,20],[50,30],[40,30]]],35,25)

    def test_stale_baseline_label_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'results.json').write_text(json.dumps({'rows':[{'fixture':'one','expected':'new','crop_sha256':'a'}]}))
            (root/'routed.json').write_text(json.dumps({'rows':[{'Fixture':'one','Expected':'old'}]}))
            (root/'routed-inputs.json').write_text(json.dumps({'Fixtures':[{'Name':'one','Expected':'old','Sha256':'a'}]}))
            with self.assertRaisesRegex(ValueError, 'Stale baseline'):
                compare(root)


if __name__ == '__main__':
    unittest.main()
