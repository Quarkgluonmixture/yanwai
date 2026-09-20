# WeChat × Jev Conversation HUD

This checkout implements accepted Phases 0–2 and the Phase 3 OCR candidate awaiting manual acceptance. It does not implement the new-message observer, Jev calls, or the HUD overlay.

## What is available

- `WeChatJevHud.App`: WPF diagnostic UI that refreshes HWND/process/title/class, desktop bounds, monitor, and DPI every 500 ms. Its button saves and previews one frame only when explicitly pressed.
- `WeChatJevHud.Diagnostics`: command-line window diagnostics, explicit capture, offline fixture detection, and capture-plus-detection.
- `WeChatJevHud.Vision`: capture-relative chat ROI location, `Remote`/`Self`/`Unknown` text-bubble detection, heuristic detection scores, timing, and annotated debug rendering.
- Replaceable interfaces for window tracking, capture, bubble detection, OCR, Jev, and overlay rendering.
- `WeChatJevHud.Ocr.Windows` and `WeChatJevHud.Ocr.Tesseract`: crop-only Simplified Chinese/English OCR adapters, explicit nullable `OcrConfidence`, low-confidence status, preprocessing variants, and an adaptive candidate policy.
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

The report has `fixture | expected | recognized | ocr_confidence | elapsed_ms` tables
for each candidate. Matching crop PNGs are saved beside it for visual inspection.
`.ocr-cache` is gitignored because local evaluations may include private chat text.

From WSL, invoke the same Windows scripts through interop, for example:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w scripts/diagnose.ps1)" -Capture
```

## Secrets and later phases

No TypeSafe/Jev code runs through Phase 3. The official TypeSafe skill is installed at `.agents/skills/typesafe-ai/`. Before Phase 5 implementation, the live-docs gate in `docs/ACCEPTANCE.md` still applies.

When Jev is implemented later, keep the key outside the repository, for example in the current Windows user's environment:

```powershell
$env:TYPESAFE_API_KEY = "<local value>"
```

Never paste the key into source, logs, screenshots, or committed configuration.
