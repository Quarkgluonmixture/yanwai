"""Validate actual .NET production API results against the immutable Unified benchmark."""
import argparse
import hashlib
import json
from pathlib import Path
import unicodedata

try:
    from scripts.compare_unified_bubble import language
    from scripts.paddle_ocr_benchmark import percentile
except ModuleNotFoundError:
    from compare_unified_bubble import language
    from paddle_ocr_benchmark import percentile


def milliseconds(value):
    hours, minutes, seconds = value.split(':')
    return (int(hours)*3600 + int(minutes)*60 + float(seconds))*1000


def compare(root):
    manifest = json.loads((root/'manifest.json').read_text(encoding='utf-8'))['Fixtures']
    baseline = json.loads((root/'unified-results.json').read_text(encoding='utf-8'))['rows']
    production = json.loads((root/'production-unified.json').read_text(encoding='utf-8'))
    rows = production['rows']
    old = {r['fixture']: r for r in baseline}
    inputs = {r['Name']: r for r in manifest}
    if len(rows) != len(old) or {r['Fixture'] for r in rows} != set(old) or set(inputs) != set(old):
        raise ValueError('Corpus entries changed')
    for row in rows:
        fixture, reference = inputs[row['Fixture']], old[row['Fixture']]
        digest = hashlib.sha256((root/fixture['Crop']).read_bytes()).hexdigest()
        if digest != fixture['Sha256'] or digest != reference['crop_sha256'] or row['Expected'] != reference['expected'] or row['Expected'] != fixture['Expected']:
            raise ValueError('Corpus bytes or expected label changed')
        diagnostic = row['Diagnostics']
        extraction = diagnostic['Extraction']
        if diagnostic['RuntimeFallback'] or extraction is None:
            raise ValueError('Healthy corpus unexpectedly used fallback')
        if (row['FinalRawText'] != reference['composed_text'] or
                extraction['DetectedLineCount'] != reference['detected_line_count'] or
                extraction['LineBoxes'] != reference['line_boxes']):
            raise ValueError('Production extraction differs from benchmark: '+row['Fixture'])
        if row['IsTrustedForSemantics'] or any(e['OcrConfidence'] is not None for e in diagnostic['Evidence']):
            raise ValueError('Paddle promoted trust/confidence')
    if production['counters']['PaddleFallbacks'] != 0:
        raise ValueError('Fallback count is not zero')
    groups = {
        'overall': rows,
        'calibration single-line': [r for r in rows if r['Fixture'].startswith('0-') and old[r['Fixture']]['detected_line_count']==1],
        'multiline / independent quote': [r for r in rows if old[r['Fixture']]['detected_line_count']>=2 or 'quot' in (old[r['Fixture']]['role'] or '').lower()],
        '96 DPI': [r for r in rows if old[r['Fixture']]['dpi']==96],
        '144 DPI': [r for r in rows if old[r['Fixture']]['dpi']==144],
        'punctuation': [r for r in rows if any(unicodedata.category(c).startswith('P') for c in r['Expected'])],
    }
    for name in ('Chinese', 'English', 'mixed'):
        groups[name] = [r for r in rows if language(r['Expected'])==name]
    report = ['# Actual production Unified extraction parity', '',
              f'{len(rows)} entries / {len({f["Sha256"] for f in manifest})} byte-distinct crops; hashes, expected labels, output strings, line counts and boxes match the accepted benchmark.',
              'Adaptive fallback count: 0. All Paddle outputs remain untrusted; calibration deferred.', '',
              '| cohort | raw exact | normalized match |', '| --- | --- | --- |']
    for name, group in groups.items():
        report.append(f'| {name} | {sum(r["RawExactMatch"] for r in group)}/{len(group)} | {sum(r["NormalizedMatch"] for r in group)}/{len(group)} |')
    report += ['', '| timing | p50 ms | p95 ms |', '| --- | --- | --- |']
    timings = {
        'worker roundtrip': [milliseconds(r['PaddleRoundtrip']) for r in rows],
        'detector': [milliseconds(r['Diagnostics']['Extraction']['DetectionElapsed']) for r in rows],
        'recognizer': [milliseconds(r['Diagnostics']['Extraction']['RecognitionElapsed']) for r in rows],
        'total OCR (including .NET PNG encode)': [milliseconds(r['TotalOcr']) for r in rows],
    }
    for name, values in timings.items():
        report.append(f'| {name} | {percentile(values,.5):.1f} | {percentile(values,.95):.1f} |')
    report += ['', 'Includes first post-warmup request; excludes model startup/warmup. No interleaved raw-control inference. Native Windows Python + .NET over in-memory PNG JSONL IPC.',
               'Full private rows with text, scores and stage timings: production-unified.json.']
    text = '\n'.join(report)
    (root/'production-parity.md').write_text(text, encoding='utf-8')
    print(text)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    compare(parser.parse_args().root)
