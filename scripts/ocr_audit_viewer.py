"""Render private audit artifacts at native device-pixel scale; no OCR calls."""
import argparse
import html
import json
from pathlib import Path
from PIL import Image


def runs(rows):
    result = []
    for y, active in enumerate(rows):
        if active:
            if result and result[-1][1] == y:
                result[-1][1] = y + 1
            else:
                result.append([y, y + 1])
    return result


def render(root, routing_suffix='.routing.json', output_name='inspection.html'):
    sections = []
    for manifest in sorted(root.glob('dpi*/manifest.json')):
        routing = json.loads(manifest.with_suffix(routing_suffix).read_text())
        fixtures = json.loads(manifest.read_text())['Fixtures']
        if len(fixtures) != len(routing):
            raise ValueError(f'Stale routing audit: {manifest}')
        for fixture, route in zip(fixtures, routing):
            assert fixture['Name'] == route['Name']
            name = fixture['Name']
            directory = root / 'artifacts' / name
            roi = Image.open(directory / 'text_roi.png').size
            pictures = []
            for file in ('raw_crop.png', 'text_roi.png', 'normalized_lanczos.png', 'normalized_gray.png'):
                path = directory / file
                with Image.open(path) as image:
                    width, height = image.size
                src = path.relative_to(root).as_posix()
                pictures.append(f'<p>{file}: source {width} × {height} px; text ROI {roi[0]} × {roi[1]} px; capture DPI {fixture["CaptureDpi"]}</p><div class="scroll"><img data-native src="{src}" width="{width}" height="{height}"></div>')
            analysis = route['Analysis']
            active = analysis['ActiveRows']
            row_runs = runs(active)
            gap_limit = analysis.get('MaximumBridgedGap', 2)
            min_height = analysis.get('MinimumBandHeight', 3)
            merged, gaps = [], []
            for start, end in row_runs:
                if merged and start - merged[-1][1] <= gap_limit:
                    gaps.append([merged[-1][1], start])
                    merged[-1][1] = end
                else:
                    merged.append([start, end])
            heights = [end-start for start, end in merged]
            details = dict(route)
            details.update(detected_row_runs=row_runs, bridged_gaps=gaps, band_heights=heights,
                           maximum_bridged_gap=gap_limit, minimum_band_height=min_height)
            (directory / f'audit{routing_suffix}').write_text(json.dumps(details, ensure_ascii=False, indent=2), encoding='utf-8')
            bars = ''.join(f'<rect x="60" y="{y*4}" width="{count*3}" height="3" fill="{"#16834a" if active[y] else "#999"}"/><text x="0" y="{y*4+4}" font-size="4">{y}: {count}</text>' for y,count in enumerate(analysis['RowCounts']))
            svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{route["BubbleWidth"]*3+80}" height="{route["BubbleHeight"]*4}">{bars}</svg>'
            (directory / f'row-projection{routing_suffix}.svg').write_text(svg, encoding='utf-8')
            sections.append(f'<section><h2>{html.escape(fixture["Expected"])} — {fixture["CaptureDpi"]} DPI</h2>{"".join(pictures)}<h3>Row projection (4× vertical; green=active, gray=inactive)</h3><div class="scroll">{svg}</div><p>Background luminance={analysis["EstimatedBackgroundLuminance"]:.3f}; minimum pixels={analysis["MinimumContrastingPixels"]}; runs={row_runs}; bridged gaps={gaps}; heights={heights}; final bands={analysis["BandCount"]}; route={analysis["SelectedRoute"]}</p><details><summary>Every row / exact .NET evidence</summary><pre>{html.escape(json.dumps(details,ensure_ascii=False,indent=2))}</pre></details></section>')
    page = '''<!doctype html><meta charset="utf-8"><title>Private OCR native inspection</title>
<style>body{font:16px sans-serif;margin:24px;background:#eee;color:#222}section{padding:16px;background:white;margin:24px 0}.scroll{overflow-x:auto;max-width:100%;padding:8px;background:#ddd}img{max-width:none!important;display:block}pre{overflow:auto}header{position:sticky;top:0;background:#fff;padding:12px;border:1px solid #888}</style>
<header><label>Display mode <select id="mode"><option value="device">Native: 1 source pixel = 1 device pixel</option><option value="css">1 source pixel = 1 CSS pixel (OS/browser may scale)</option></select></label><p id="scale"></p></header>
<p>Long images scroll horizontally. Normalized artifacts remain experimental. Device-pixel ratio combines OS scaling and desktop browser zoom; browsers do not expose those separately. Reset browser zoom with Ctrl+0 for inspection.</p>'''+f'<p>Routing evidence file suffix: <code>{html.escape(routing_suffix)}</code> (routing-fixed = corrected policy; routing = captured baseline).</p>'+''.join(sections)+'''
<script>function update(){const dpr=window.devicePixelRatio||1;const pinch=window.visualViewport?.scale||1;const ratio=dpr*pinch;document.getElementById('scale').textContent=`devicePixelRatio=${dpr}; visualViewport.scale=${pinch}; source/device scale=${document.getElementById('mode').value==='device'?1:ratio}`;document.querySelectorAll('[data-native]').forEach(img=>{const divisor=document.getElementById('mode').value==='device'?ratio:1;img.style.width=img.getAttribute('width')/divisor+'px';img.style.height=img.getAttribute('height')/divisor+'px';});}document.getElementById('mode').onchange=update;window.onresize=update;setInterval(update,500);update();</script>'''
    (root / output_name).write_text(page, encoding='utf-8')
    print(root / output_name)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--routing-suffix', default='.routing.json')
    parser.add_argument('--output-name', default='inspection.html')
    args = parser.parse_args()
    render(args.root, args.routing_suffix, args.output_name)
