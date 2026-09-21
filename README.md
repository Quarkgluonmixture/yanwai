# WeChat × Jev Conversation HUD

This checkout implements accepted Phases 0–4 and an in-progress Phase 4.5 production
OCR runtime. It does not implement Phase 5 Jev calls or the HUD overlay.

## What is available

- `WeChatJevHud.App`: WPF diagnostic UI that refreshes HWND/process/title/class, desktop bounds, monitor, and DPI every 500 ms. Its button saves and previews one frame only when explicitly pressed.
- `WeChatJevHud.Diagnostics`: command-line window diagnostics, explicit capture, offline fixture detection, and capture-plus-detection.
- `WeChatJevHud.Vision`: capture-relative chat ROI location, `Remote`/`Self`/`Unknown` text-bubble detection, heuristic detection scores, timing, and annotated debug rendering.
- Replaceable interfaces for window tracking, capture, bubble detection, OCR, Jev, and overlay rendering.
- `WeChatJevHud.Ocr.Windows` and `WeChatJevHud.Ocr.Tesseract`: crop-only Simplified Chinese/English OCR adapters, explicit nullable `OcrConfidence`, low-confidence status, preprocessing variants, and an adaptive candidate policy.
- `WeChatJevHud.Ocr`: explicit single-line/multiline routing, a persistent
  recognition-only `PP-OCRv6_small_rec` worker client, engine-specific evidence, and
  a conservative trust/fallback policy. Paddle `rec_score` never becomes
  `OcrConfidence`.
- `WeChatJevHud.Observer`: in-memory change detection, conversation epochs, ordered
  visible-message reconciliation, duplicate suppression, and Bootstrap/History/LiveNew
  observation state.
- Per-Monitor DPI Awareness V2 manifests for both runnable programs.

The Phase 1 capture adapter first asks WeChat's `MMUIRenderSubWindow*` child to paint into an off-screen bitmap. If that path is unavailable, it falls back to copying the visible desktop pixels occupied by the render/client bounds and reports `VisibleDesktopFallback`; that fallback requires WeChat to be unobscured. WeChat must always be restored for an explicit capture. This is the deliberately small capture spike permitted by `docs/SPEC.md`; a Windows Graphics Capture adapter can replace it later without changing callers.

## Windows setup and commands

Run these from Windows PowerShell in the repository directory:

```powershell
# Only needed if a .NET 8 SDK is not already installed.
.\scripts\install-dotnet-sdk.ps1

.\scripts\build.ps1
.\scripts\run-debug.ps1
```

The shortest Phase 1 diagnostic/capture command is:

```powershell
.\scripts\diagnose.ps1 -Capture
```

It writes exactly one PNG under `debug-captures\` and prints its full path. That directory is gitignored because frames can contain private chat text.

Capture and immediately produce the Phase 2 detection list and annotated PNG:

```powershell
.\scripts\diagnose.ps1 -CaptureDetect
```

Run Phase 2 against an existing fixture or explicitly saved frame:

```powershell
.\scripts\diagnose.ps1 `
  -Detect .\debug-captures\wechat-example.png `
  -Output .\debug-captures\wechat-example-bubbles.png
```

The detector prints `side, x, y, width, height, detection_score`. Coordinates are relative to the captured WeChat render frame. `detection_score` is a heuristic ranking/quality signal, not a calibrated probability. Debug frames are explicit, local, and gitignored.

Install the pinned local Tesseract language models, then compare OCR candidates on the
committed real WeChat fixture:

```powershell
.\scripts\install-ocr-models.ps1
.\scripts\diagnose.ps1 `
  -OcrEvaluate .\fixtures\ocr\phase3-public.json `
  -OcrOutput .\.ocr-cache\phase3-public-evaluation.md
```

The report keeps raw and normalized evaluation layers separate:
`raw_recognized`, `normalized_recognized`, `raw_exact_match`, `normalized_match`,
`raw_cer`, and `normalized_cer`. `exact_match` always means literal raw equality;
normalization can never turn a raw mismatch into an exact match. Matching crop PNGs
are saved beside the report for visual inspection.
`.ocr-cache` is gitignored because local evaluations may include private chat text.

### Experimental PaddleOCR recognition benchmark

`scripts/paddle_ocr_benchmark.py` is an isolated Phase 3 evaluation tool. It calls
PaddleOCR's `TextRecognition` API only; it does not run Paddle text detection and it
does not alter the .NET `IOcrEngine` selection policy.

Create a gitignored virtual environment, install the PaddlePaddle backend selected
for the machine by the [official installation guide](https://www.paddlepaddle.org.cn/documentation/docs/en/install/index_en.html),
then install the experiment dependency and run the private manifest:

```bash
python3 -m venv .ocr-cache/paddle-venv
# Use the current official selector to install the backend matching the local CUDA
# runtime (or the current CPU wheel) first; do not infer this from the OCR package.
.ocr-cache/paddle-venv/bin/pip install paddleocr==3.7.0
.ocr-cache/paddle-venv/bin/python scripts/paddle_ocr_benchmark.py \
  --manifest .ocr-cache/phase3-paddle-benchmark.json \
  --output .ocr-cache/phase3-paddle-results.md \
  --device gpu:0
```

The benchmark defaults to `PP-OCRv6_small_rec` and `PP-OCRv6_medium_rec`, saves
the exact input crops plus Markdown/JSON results under `.ocr-cache`, and reports
Paddle `rec_score` verbatim. `rec_score` is not treated as a calibrated probability
or as directly comparable with `DetectionScore`, Tesseract confidence, or future
Jev probability. See the current
[PaddleOCR Text Recognition documentation](https://www.paddleocr.ai/main/en/version3.x/module_usage/text_recognition.html)
for the upstream API.

### Phase 4 live message observer

Run the observer against the Windows desktop WeChat session from PowerShell:

```powershell
.\scripts\observe.ps1
```

It captures in memory only, establishes the visible messages as a bootstrap baseline,
then reports conversation epochs, evidence-based identity decisions, new-message
events, duplicate suppression, counters, and per-stage timings. Header identity uses a
scale-tolerant perceptual comparison plus visible-message continuity and a three-frame
switch confirmation. Only strong continuity against the immediately previous visible
snapshot can rebase a changed header; permissive visual/history matches are diagnostic
evidence and cannot suppress a real switch. Resize/DPI layout transitions cannot
immediately change epochs. After a confirmed switch, the observer waits for the first
stable visible snapshot before establishing the new baseline. A transitional empty
viewport therefore cannot replay subsequently rendered history as new; a genuinely
empty conversation becomes the baseline only after three stable empty observations.
Non-empty snapshots must remain visually stable for two consecutive observations, so
incrementally rendered existing history remains Bootstrap throughout settling.
Chat text is redacted by default. For an explicitly opted-in, truncated normalized-text
diagnostic:

```powershell
.\scripts\observe.ps1 -DebugText
```

Use `-Seconds 30` for a bounded run or `-IntervalMilliseconds 200` to change the
lightweight check interval. The observer never writes screenshots or chat logs.

### Phase 4.5 production OCR runtime

Install the Windows-native runtime once from Windows PowerShell. The default GPU
install follows Paddle's current Windows CUDA 12.9 wheel guidance and stores the
environment under `%LOCALAPPDATA%\WeChatJevHud\paddle-ocr`:

The installer requires [`uv`](https://docs.astral.sh/uv/getting-started/installation/)
on the Windows `PATH`; follow its official install instructions first if `Get-Command
uv` fails.

```powershell
.\scripts\install-paddle-runtime.ps1
.\scripts\observe.ps1 -DebugText
```

The observer launches one persistent worker, waits for `READY`, reports exact
model/library/device information, and keeps the model resident. Bubble PNG bytes use
an ID-correlated UTF-8 JSON-lines protocol in memory; normal operation writes no
message crops. `PP-OCRv6_small_rec` handles confidently classified single-line crops.
Wrapped/multiline and ambiguous crops remain Adaptive. Worker/protocol/device failure
falls back without terminating the observer. Paddle disagreement, Adaptive-only text,
and fallback text remain available as `LowConfidence` candidates but are not trusted
for downstream semantics.
Worker/library stderr is suppressed by default. `-PaddleWorkerDebug` explicitly
enables privacy-safe troubleshooting metadata (severity category and character count),
never the raw third-party stderr line; output is capped to avoid log flooding.

Private calibration data belongs under `.ocr-cache`. Put the expected visible
messages in top-to-bottom order in a text file, show exactly those text bubbles in
WeChat, then run:

```powershell
.\scripts\collect-ocr-calibration.ps1 `
  -ExpectedFile .\.ocr-cache\expected-batch.txt `
  -Side self

.\scripts\evaluate-production-ocr.ps1
```

If known older matching bubbles remain above the labeled batch, pass `-Skip N`.
Collection still requires the viewport to contain exactly `N + expected lines` matching
bubbles, then saves only the labeled suffix.

Collection aborts without saving when the expected-line and detected-bubble counts
differ. Every crop/label pairing still requires visual inspection. The report keeps
raw/normalized accuracy, Paddle `rec_score`, Adaptive output, trust, CER,
startup/warmup and request timings, fallbacks, polarity errors, and trusted-wrong count
separate. Crops and reports remain gitignored.

Phase 4.5 acceptance is currently paused for routing verification. The input audit compared
the same detected bubble as a raw crop, a contrast-derived text ROI with safe padding,
32/40/48 px text-band normalization using nearest/bicubic/Lanczos interpolation, and
a conservative grayscale/background-normalized Lanczos variant. It does not use text
detection, deskew, dewarp, perspective correction, or binarization, and it does not
change production preprocessing or trust policy.

All 196 audit runs were exact, including every raw bubble. Production keeps raw
whole-bubble Paddle input. Use `.ocr-cache/phase4.5-input-audit/inspection.html` for
baseline routing and `inspection-fixed.html` for corrected routing with native
device-pixel images, source dimensions, DPI and browser scale. Long images scroll;
the Markdown table is a navigation aid, not a sharpness comparison.

To export actual .NET routing evidence without running OCR:

```powershell
dotnet run --project src/WeChatJevHud.Diagnostics -- `
  --routing-audit .ocr-cache/phase4.5-input-audit/dpi150/manifest.json `
  --output .ocr-cache/phase4.5-input-audit/dpi150/manifest.routing-fixed.json
# Repeat for dpi100. Render existing evidence without re-running OCR:
python scripts/ocr_audit_viewer.py --root .ocr-cache/phase4.5-input-audit `
  --routing-suffix .routing-fixed.json --output-name inspection-fixed.html
```

The viewer requires Pillow. Native mode compensates for `devicePixelRatio` and
`visualViewport.scale`; desktop browsers do not separately expose OS scale and
browser zoom. Reset zoom with Ctrl+0 and inspect at native mode.

Collect the same seven visible self-message bubbles once on each monitor, without
resending between captures:

```powershell
.\scripts\collect-ocr-calibration.ps1 `
  -ExpectedFile .\.ocr-cache\phase4.5-input-audit\expected.txt `
  -Side self `
  -CalibrationDirectory .\.ocr-cache\phase4.5-input-audit\dpi150

# Move the same WeChat window, with the same bubbles visible, to the 100% monitor.
.\scripts\collect-ocr-calibration.ps1 `
  -ExpectedFile .\.ocr-cache\phase4.5-input-audit\expected.txt `
  -Side self `
  -CalibrationDirectory .\.ocr-cache\phase4.5-input-audit\dpi100

.\scripts\audit-ocr-input.ps1 `
  -Manifest `
    .\.ocr-cache\phase4.5-input-audit\dpi150\manifest.json, `
    .\.ocr-cache\phase4.5-input-audit\dpi100\manifest.json
```

The private Markdown/JSON report records bubble and ROI geometry, estimated text-band
height, actual capture DPI, route, raw/normalized exactness and CER, `rec_score`, and
inference time. Each fixture directory also contains `raw_crop.png`, `text_roi.png`,
`normalized_lanczos.png`, and `normalized_gray.png` for manual inspection. Agreement
between preprocessing variants is explicitly not independent-engine agreement.

From WSL, invoke the same Windows scripts through interop, for example:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w scripts/diagnose.ps1)" -Capture
```

### Experimental full-bubble Paddle benchmark

The Phase 4.5 experiment in `scripts/paddle_bubble_benchmark.py` evaluates small text
detection **inside existing bubble crops** followed by small recognition per line.
It leaves the production worker/router/trust unchanged. Both models stay loaded for
the run; document orientation, unwarping and line orientation modules are absent.
Use the installed Windows Paddle Python (with the project as current directory):

```powershell
& "$env:LOCALAPPDATA\WeChatJevHud\paddle-ocr\.venv\Scripts\python.exe" `
  scripts/paddle_bubble_benchmark.py `
  --manifest .ocr-cache/phase4.5-calibration/manifest.json `
    .ocr-cache/phase4.5-input-audit/dpi100/manifest.json `
    .ocr-cache/phase4.5-input-audit/dpi150/manifest.json `
    .ocr-cache/phase3-paddle-benchmark.json `
  --output .ocr-cache/phase4.5-paddle-bubble
python scripts/paddle_bubble_benchmark.py `
  --manifest .ocr-cache/phase4.5-calibration/manifest.json `
  --output .ocr-cache/phase4.5-paddle-bubble --snapshot-baseline
.\scripts\evaluate-production-ocr.ps1 `
  -Manifest .ocr-cache/phase4.5-paddle-bubble/manifest.json `
  -Output .ocr-cache/phase4.5-paddle-bubble/routed.md
python scripts/paddle_bubble_benchmark.py `
  --manifest .ocr-cache/phase4.5-calibration/manifest.json `
  --output .ocr-cache/phase4.5-paddle-bubble --compare-only
```

The combined manifest keeps main and quoted regions separate. Expected text never
participates in line composition. Reports retain raw line strings, boxes, composed
text, exactness/CER and separate detection/recognition/total timings. `rec_scores`
remain diagnostic only. Current evidence is 78/84 exact vs routed 71/84, but three
new single-line punctuation substitutions prevent claiming a regression-free replacement.

API sources consulted 2026-09-21: [official TextDetection](https://www.paddleocr.ai/main/en/version3.x/module_usage/text_detection.html)
and [TextRecognition](https://www.paddleocr.ai/main/en/version3.x/module_usage/text_recognition.html).

## Secrets and later phases

No TypeSafe/Jev code runs through Phase 4.5. The official TypeSafe skill is installed at `.agents/skills/typesafe-ai/`. Before Phase 5 implementation, the live-docs gate in `docs/ACCEPTANCE.md` still applies.

When Jev is implemented later, keep the key outside the repository, for example in the current Windows user's environment:

```powershell
$env:TYPESAFE_API_KEY = "<local value>"
```

Never paste the key into source, logs, screenshots, or committed configuration.
