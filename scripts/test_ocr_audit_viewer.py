import json
import tempfile
import unittest
from pathlib import Path

from scripts.ocr_audit_viewer import render, runs


class AuditViewerTests(unittest.TestCase):
    def test_stale_routing_cannot_silently_omit_samples(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dpi = root / 'dpi100'
            dpi.mkdir()
            (dpi / 'manifest.json').write_text(json.dumps({'Fixtures': [{'Name': 'one'}]}))
            (dpi / 'manifest.routing.json').write_text('[]')
            with self.assertRaisesRegex(ValueError, 'Stale routing'):
                render(root)
            self.assertFalse((root / 'inspection.html').exists())

    def test_projection_runs_retain_final_active_row(self):
        self.assertEqual(runs([False, True, True, False, True]), [[1, 3], [4, 5]])


if __name__ == '__main__':
    unittest.main()
