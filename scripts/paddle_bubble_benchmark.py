"""Experimental bubble-internal detection + recognition. Never used by the observer."""
import argparse
import contextlib
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time

from PIL import Image

try:
    from scripts.paddle_ocr_benchmark import normalize_text, character_error_rate, is_cjk, is_cjk_punctuation, markdown_cell, percentile
except ModuleNotFoundError:
    from paddle_ocr_benchmark import normalize_text, character_error_rate, is_cjk, is_cjk_punctuation, markdown_cell, percentile


def compose(lines):
    """Join display wraps; keep original per-line output separately. No expected-text input."""
    text = ''
    for line in lines:
        if not line:
            continue
        separator = '' if not text or text[-1].isspace() or line[0].isspace() else ' '
        if text and (is_cjk(text[-1]) or is_cjk(line[0]) or is_cjk_punctuation(text[-1]) or is_cjk_punctuation(line[0])):
            separator = ''
        text += separator + line
    return text


def boxes_from_polygons(polygons, width, height):
    boxes = []
    for polygon in polygons:
        xs, ys = zip(*polygon)
        box = [max(0, math.floor(min(xs))), max(0, math.floor(min(ys))),
               min(width, math.ceil(max(xs))), min(height, math.ceil(max(ys)))]
        if box[2] <= box[0] or box[3] <= box[1]:
            raise ValueError('Invalid detected line box')
        boxes.append(box)
    return sorted(boxes, key=lambda box: ((box[1]+box[3])/2, box[0]))


def recognition_inputs(image, boxes, unified=False):
    if unified and len(boxes) <= 1:
        return [image]
    return [image.crop(box) for box in boxes]


def prepare(manifests, root):
    (root / 'crops').mkdir(parents=True, exist_ok=True)
    fixtures = []
    for cohort, manifest in enumerate(manifests):
        data = json.loads(manifest.read_text(encoding='utf-8'))
        items = next(v for k,v in data.items() if k.lower() == 'fixtures')
        for original in items:
            item = {k.lower(): v for k,v in original.items()}
            name = f'{cohort}-{item["name"]}'
            source = manifest.parent / (item.get('crop') or item['image']).replace('\\', '/')
            with Image.open(source) as image:
                crop = image.convert('RGB')
                if not item.get('crop'):
                    x,y,w,h = (item[k] for k in ('x','y','width','height'))
                    if min(x,y) < 0 or min(w,h) <= 0 or x+w > crop.width or y+h > crop.height:
                        raise ValueError('Invalid source crop')
                    crop = crop.crop((x,y,x+w,y+h))
            crop.save(root / 'crops' / f'{name}.png')
            fixtures.append(dict(Name=name, Expected=item['expected'], Crop=f'crops/{name}.png',
                                 Group=f'cohort-{cohort}', Role=item.get('role'), CaptureDpi=item.get('capturedpi'),
                                 SourceManifest=str(manifest), Sha256=hashlib.sha256((root/'crops'/f'{name}.png').read_bytes()).hexdigest()))
    (root / 'manifest.json').write_text(json.dumps({'Fixtures':fixtures},ensure_ascii=False,indent=2),encoding='utf-8')
    return fixtures


def run(args, fixtures):
    import numpy as np
    os.environ.setdefault('PADDLE_PDX_MODEL_SOURCE','BOS')
    os.environ.setdefault('PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK','True')
    start = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import paddle
        import paddleocr
        from paddleocr import TextDetection, TextRecognition
        det = TextDetection(model_name='PP-OCRv6_small_det', device=args.device)
        rec = TextRecognition(model_name='PP-OCRv6_small_rec', device=args.device)
    startup_ms = (time.perf_counter()-start)*1000

    def sync():
        if args.device.startswith('gpu'):
            paddle.device.synchronize()

    def predict(model, image):
        sync()
        start = time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            result = list(model.predict(input=np.asarray(image), batch_size=1))
        sync()
        if len(result) != 1:
            raise RuntimeError('Expected one model result')
        return result[0], (time.perf_counter()-start)*1000

    with Image.open(args.output / fixtures[0]['Crop']) as first:
        start = time.perf_counter()
        predict(det, first.convert('RGB'))
        predict(rec, first.convert('RGB'))
        warmup_ms = (time.perf_counter()-start)*1000
    rows = []
    unified = getattr(args, 'unified', False)
    prefix = 'unified-' if unified else ''
    for fixture in fixtures:
        with Image.open(args.output / fixture['Crop']) as source:
            image = source.convert('RGB')
        # Separate direct-recognition control; excluded from candidate latency.
        raw_control = predict(rec, image)[0] if unified else None
        start = time.perf_counter()
        detected, detection_ms = predict(det, image)
        boxes = boxes_from_polygons(detected['dt_polys'].tolist(), image.width, image.height)
        lines, scores, line_images = [], [], []
        recognition_ms = 0
        for index,line in enumerate(recognition_inputs(image, boxes, unified)):
            line_images.append(line)
            recognized, elapsed = predict(rec, line)
            recognition_ms += elapsed
            lines.append(str(recognized['rec_text']))
            scores.append(float(recognized['rec_score']))
        text = compose(lines)
        total_ms = (time.perf_counter()-start)*1000
        for index,line in enumerate(line_images):
            line.save(args.output / 'crops' / f'{prefix}{fixture["Name"]}-line{index}.png')
        expected = fixture['Expected']
        row = dict(fixture=fixture['Name'], expected=expected, dpi=fixture['CaptureDpi'], role=fixture['Role'],
                   source_manifest=fixture['SourceManifest'], crop_sha256=fixture['Sha256'],
                   detected_line_count=len(boxes), line_boxes=boxes, recognized_lines=lines,
                   rec_scores=scores, composed_text=text, raw_exact_match=text==expected,
                   normalized_match=normalize_text(text)==normalize_text(expected),
                   raw_cer=character_error_rate(expected,text),
                   normalized_cer=character_error_rate(normalize_text(expected),normalize_text(text)),
                   detection_ms=detection_ms, recognition_ms=recognition_ms, total_ms=total_ms)
        if unified:
            row.update(whole_bubble_recognized=lines[0] if len(boxes)<=1 else None,
                       recognized_lines=lines if len(boxes)>=2 else [],
                       raw_whole_control=str(raw_control['rec_text']),
                       extraction_path='whole_bubble' if len(boxes)<=1 else 'detected_lines')
        rows.append(row)
        # Save progress in case a later model call fails.
        (args.output / f'{prefix}rows.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
    metadata = dict(device=paddle.device.get_device(), paddle=paddle.__version__, paddleocr=paddleocr.__version__,
                    startup_ms=startup_ms,warmup_ms=warmup_ms,model_instances={'det':1,'rec':1},
                    use_doc_orientation_classify=False,use_doc_unwarping=False,use_textline_orientation=False,
                    cropping='axis-aligned bounds; no perspective warp',
                    timing='synchronized module inference including pre/postprocessing; total includes sorting/cropping/composition, excludes PNG artifact writes and input decoding')
    metadata['extraction_policy'] = '0/1 detections: raw whole bubble; 2+: sorted detected lines' if unified else 'all detected lines cropped'
    metadata['raw_control_timing_excluded'] = unified
    summary = dict(count=len(rows),raw_exact=sum(r['raw_exact_match'] for r in rows),
                   normalized_exact=sum(r['normalized_match'] for r in rows),
                   p50_ms=percentile([r['total_ms'] for r in rows],.5),p95_ms=percentile([r['total_ms'] for r in rows],.95))
    (args.output/f'{prefix}results.json').write_text(json.dumps(dict(metadata=metadata,summary=summary,rows=rows),ensure_ascii=False,indent=2),encoding='utf-8')
    fields=['fixture','expected','dpi','detected_line_count','line_boxes','recognized_lines','composed_text','raw_exact_match','normalized_match','raw_cer','normalized_cer','detection_ms','recognition_ms','total_ms']
    if unified:
        fields += ['whole_bubble_recognized', 'raw_whole_control', 'extraction_path']
    report=['# Private Paddle bubble benchmark','',json.dumps(summary),'',
            'Scores are uncalibrated diagnostics. No trust decision. Main/quote regions evaluated separately.',
            'Composition joins CJK wraps without spaces and other wraps with a space; literal engine line strings are retained.',
            'Total timing includes detection, sorting, cropping, recognition and composition; excludes input decoding and artifact writes.','',
            '| '+' | '.join(fields)+' |','| '+' | '.join(['---']*len(fields))+' |']
    report += ['| '+' | '.join(markdown_cell(row[key]) for key in fields)+' |' for row in rows]
    (args.output/f'{prefix}report.md').write_text('\n'.join(report),encoding='utf-8')
    print(json.dumps(summary))


def compare(root):
    result = json.loads((root/'results.json').read_text(encoding='utf-8'))
    routed = json.loads((root/'routed.json').read_text(encoding='utf-8'))
    baseline = {row['Fixture']:row for row in routed['rows']}
    inputs = {r['Name']:r for r in json.loads((root/'routed-inputs.json').read_text(encoding='utf-8'))['Fixtures']}
    rows = result['rows']
    if set(baseline) != {row['fixture'] for row in rows}:
        raise ValueError('Comparison fixtures do not match')
    for row in rows:
        old = baseline[row['fixture']]
        recorded = inputs[row['fixture']]
        if old['Expected'] != row['expected'] or recorded['Expected'] != row['expected'] or recorded['Sha256'] != row['crop_sha256']:
            raise ValueError('Stale baseline labels or crop hashes')
        if hashlib.sha256((root / recorded['Crop']).read_bytes()).hexdigest() != recorded['Sha256']:
            raise ValueError('Crop changed after baseline snapshot')
    unique = len({hashlib.sha256((root/'crops'/f'{row["fixture"]}.png').read_bytes()).digest() for row in rows})
    cohorts = {'all':rows}
    for source in dict.fromkeys(row['source_manifest'] for row in rows):
        cohorts[source] = [r for r in rows if r['source_manifest']==source]
    cohorts['detected 2 lines'] = [r for r in rows if r['detected_line_count']==2]
    cohorts['detected 3+ lines'] = [r for r in rows if r['detected_line_count']>=3]
    table=['# Private extraction comparison','',
           f'{len(rows)} evaluation entries; {unique} byte-distinct crop PNGs. Repeated/alias fixtures are not independent evidence.',
           'Cohorts are the recorded source manifest paths; expected labels and crop hashes are checked against the routed-inputs snapshot.',
           'Line-count groups are detector outputs, not independently labeled segmentation ground truth.',
           'Paddle timing excludes transport, artifact IO and startup; routed timing includes worker transport and secondary OCR.','',
           '| cohort | n | Paddle raw / normalized | routed raw / normalized | Paddle p50 / p95 ms |',
           '| --- | ---: | --- | --- | --- |']
    for label,group in cohorts.items():
        if not group: continue
        old=[baseline[r['fixture']] for r in group]
        timings=[r['total_ms'] for r in group]
        table.append(f'| {label} | {len(group)} | {sum(r["raw_exact_match"] for r in group)} / {sum(r["normalized_match"] for r in group)} | {sum(r["RawExactMatch"] for r in old)} / {sum(r["NormalizedMatch"] for r in old)} | {percentile(timings,.5):.1f} / {percentile(timings,.95):.1f} |')
    table += ['', '| fixture | expected | Paddle | routed | new raw exact | old raw exact |', '| --- | --- | --- | --- | --- | --- |']
    for row in rows:
        old=baseline[row['fixture']]
        table.append('| '+' | '.join(markdown_cell(x) for x in [row['fixture'],row['expected'],row['composed_text'],old['FinalRawText'],row['raw_exact_match'],old['RawExactMatch']])+' |')
        source = root/'crops'/f'{row["fixture"]}.png'
        with Image.open(source) as image:
            w,h=image.size
            for index,box in enumerate(row['line_boxes']):
                image.crop(box).save(root/'crops'/f'{row["fixture"]}-line{index}.png')
        rectangles=''.join(f'<rect x="{x}" y="{y}" width="{right-x}" height="{bottom-y}" fill="none" stroke="red" stroke-width="1"/>' for x,y,right,bottom in row['line_boxes'])
        (root/'crops'/f'{row["fixture"]}-boxes.svg').write_text(
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}"><image href="{source.name}" width="{w}" height="{h}"/>{rectangles}</svg>',encoding='utf-8')
    (root/'comparison.md').write_text('\n'.join(table),encoding='utf-8')


if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--manifest',type=Path,nargs='+',required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--device',default='gpu:0')
    parser.add_argument('--prepare-only',action='store_true')
    parser.add_argument('--compare-only',action='store_true')
    parser.add_argument('--snapshot-baseline',action='store_true')
    parser.add_argument('--unified',action='store_true',help='Reuse the existing combined manifest; preserve baseline results')
    args=parser.parse_args()
    if args.snapshot_baseline:
        (args.output/'routed-inputs.json').write_bytes((args.output/'manifest.json').read_bytes())
    elif args.compare_only:
        compare(args.output)
    else:
        fixtures=(json.loads((args.output/'manifest.json').read_text(encoding='utf-8'))['Fixtures']
                  if args.unified else prepare(args.manifest,args.output))
        if args.unified:
            for fixture in fixtures:
                if hashlib.sha256((args.output/fixture['Crop']).read_bytes()).hexdigest() != fixture['Sha256']:
                    raise ValueError('Corpus changed since baseline')
        if not args.prepare_only:
            run(args,fixtures)
