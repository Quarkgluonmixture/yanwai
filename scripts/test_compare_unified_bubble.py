import unittest
from scripts.compare_unified_bubble import language, punctuation_spacing_key


class UnifiedComparisonTests(unittest.TestCase):
    def test_punctuation_diagnostic_does_not_erase_glyph_errors(self):
        self.assertEqual(punctuation_spacing_key('可以吗？'), punctuation_spacing_key('可以吗?'))
        self.assertEqual(punctuation_spacing_key('123, I'), punctuation_spacing_key('123,I'))
        self.assertNotEqual(punctuation_spacing_key('没事啦'), punctuation_spacing_key('没事哒'))
        self.assertNotEqual(punctuation_spacing_key('Hello'), punctuation_spacing_key('Ｈello'))

    def test_language_cohorts_keep_digits_separate(self):
        self.assertEqual(language('不行123'), 'Chinese')
        self.assertEqual(language('Hello 123'), 'English')
        self.assertEqual(language('微信 OCR 456'), 'mixed')
        self.assertEqual(language('123'), 'digits/other')
