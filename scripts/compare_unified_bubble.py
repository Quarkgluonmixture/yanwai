"""Private three-way comparison; never used for OCR trust or normalization."""
import argparse
import json
from pathlib import Path
import re
import unicodedata

try:
    from scripts.paddle_bubble_benchmark import compare, is_cjk, markdown_cell, percentile
except ModuleNotFoundError:
    from paddle_bubble_benchmark import compare, is_cjk, markdown_cell, percentile


def punctuation_spacing_key(text):
    # Diagnostic classification only. Never used for exactness, normalization or CER.
    return ''.join(unicodedata.normalize('NFKC', c) if unicodedata.category(c).startswith('P') else c
                   for c in text if not c.isspace())


def language(text):
    chinese = any(is_cjk(c) for c in text)
    latin = bool(re.search('[A-Za-z]', text))
    return 'mixed' if chinese and latin else 'Chinese' if chinese else 'English' if latin else 'digits/other'


def render(root):
    compare(root)  # Validates the immutable crop hashes/labels against routed input snapshot.
    unified = json.loads((root/'unified-results.json').read_text(encoding='utf-8'))
    line = {r['fixture']:r for r in json.loads((root/'results.json').read_text(encoding='utf-8'))['rows']}
    routed = {r['Fixture']:r for r in json.loads((root/'routed.json').read_text(encoding='utf-8'))['rows']}
    rows = unified['rows']
    if set(line) != {r['fixture'] for r in rows} or len(rows)!=len(line):
        raise ValueError('Mismatched corpus')
    for r in rows:
        old = line[r['fixture']]
        for key in ('expected','crop_sha256','detected_line_count','line_boxes'):
            if r[key] != old[key]:
                raise ValueError(f'Changed input/detection: {r["fixture"]} {key}')
    cohorts = {'overall':rows,
        'calibration single-line':[r for r in rows if r['source_manifest'].replace('\\','/').endswith('/phase4.5-calibration/manifest.json') and line[r['fixture']]['detected_line_count']==1],
        'multiline / independent quote':[r for r in rows if line[r['fixture']]['detected_line_count']>=2 or 'quot' in (r['role'] or '').lower()],
        '96 DPI':[r for r in rows if r['dpi']==96], '144 DPI':[r for r in rows if r['dpi']==144],
        'punctuation-containing':[r for r in rows if any(unicodedata.category(c).startswith('P') for c in r['expected'])]}
    for label in ('Chinese','English','mixed','digits/other'):
        cohorts[label] = [r for r in rows if language(r['expected'])==label]
    lines=['# Private Unified Bubble OCR comparison','',
        f'Same {len(rows)} entries / {len({r["crop_sha256"] for r in rows})} byte-distinct crops. Baseline labels, hashes and detection boxes verified.',
        'Language cohorts use expected CJK/ASCII-letter presence; digits/other is reported separately. Punctuation cohort overlaps languages.',
        'Calibration single-line uses the original phase4.5-calibration source manifest; multiline uses baseline detector counts plus explicit quote roles, not new segmentation ground truth.',
        'Exactness is literal composed-output equality. Diagnostic punctuation equivalence never changes exactness or CER.',
        'Latency excludes startup, image decoding, artifact IO, raw-control calls and IPC; routed includes IPC and secondary OCR.',
        'Each Unified sample was preceded by a raw recognition control call: that can warm shape-specific caches/GPU state even though its elapsed time is excluded. This is not an unperturbed observer replay.','',
        '| cohort | n | routed raw / normalized | det-line-rec raw / normalized | Unified raw / normalized | Unified p50 / p95 ms |',
        '| --- | ---: | --- | --- | --- | --- |']
    for label,group in cohorts.items():
        b=[routed[r['fixture']] for r in group]; l=[line[r['fixture']] for r in group]
        lines.append(f'| {label} | {len(group)} | {sum(r["RawExactMatch"] for r in b)} / {sum(r["NormalizedMatch"] for r in b)} | {sum(r["raw_exact_match"] for r in l)} / {sum(r["normalized_match"] for r in l)} | {sum(r["raw_exact_match"] for r in group)} / {sum(r["normalized_match"] for r in group)} | {percentile([r["total_ms"] for r in group],.5):.1f} / {percentile([r["total_ms"] for r in group],.95):.1f} |')
    cases={
        'Unified improves over det-line-rec':[r for r in rows if r['raw_exact_match'] and not line[r['fixture']]['raw_exact_match']],
        'Unified improves over routed':[r for r in rows if r['raw_exact_match'] and not routed[r['fixture']]['RawExactMatch']],
        'Unified regresses versus det-line-rec':[r for r in rows if not r['raw_exact_match'] and line[r['fixture']]['raw_exact_match']],
        'Unified regresses versus raw single-line control':[r for r in rows if r['detected_line_count']<=1 and r['raw_whole_control']==r['expected'] and not r['raw_exact_match']],
        'Unified regresses versus routed':[r for r in rows if routed[r['fixture']]['RawExactMatch'] and not r['raw_exact_match']],
        'Unified vs expected differs only in punctuation width/spacing':[r for r in rows if not r['raw_exact_match'] and punctuation_spacing_key(r['expected'])==punctuation_spacing_key(r['composed_text'])],
        'Unified vs det-line-rec differs only in punctuation width/spacing':[r for r in rows if r['composed_text']!=line[r['fixture']]['composed_text'] and punctuation_spacing_key(r['composed_text'])==punctuation_spacing_key(line[r['fixture']]['composed_text'])]}
    for title,group in cases.items():
        lines += ['',f'## {title} ({len(group)})','', '| fixture | expected | Unified | det-line-rec | routed |', '| --- | --- | --- | --- | --- |']
        lines += ['| '+' | '.join(markdown_cell(v) for v in (r['fixture'],r['expected'],r['composed_text'],line[r['fixture']]['composed_text'],routed[r['fixture']]['FinalRawText']))+' |' for r in group]
    (root/'unified-comparison.md').write_text('\n'.join(lines),encoding='utf-8')
    (root/'unified-regressions.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2),encoding='utf-8')
    print('\n'.join(lines[:20]))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,required=True)
    render(parser.parse_args().root)
