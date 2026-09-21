# DECISIONS.md

This file records important decisions, their reasons, and rejected alternatives.

---

## D-001 — Product is an observational conversation HUD

**Decision**

V0 observes WeChat conversations, makes typed probabilistic judgments, and renders them beside messages. It does not automatically reply, click, or send.

**Reason**

The desired product is a live decision/intelligence layer rather than an AI that takes over the conversation. This also keeps the first version narrow and testable.

**Rejected alternatives**

- Full auto-reply bot
- Agent that sends messages on the user's behalf
- Immediate LLM reply generation as the core product

---

## D-002 — Jev should be used as multiple narrow judgments, not one generic intent label

**Decision**

Represent semantics as a set of Noul / Choice / Score judgments over shared conversation state.

**Reason**

This matches TypeSafe's programming model: code owns workflow and System One judgments provide bounded semantic primitives. It also preserves reusable signals and probabilities.

**Rejected alternatives**

- One `intent = X` classifier as the entire semantic layer
- One large free-form LLM explanation
- Parsing unstructured prose into product state

---

## D-003 — UI Automation is not the V0 message-reading channel

**Decision**

Do not use UI Automation as the primary source of message text or message bubble geometry.

**Evidence**

The user's actual WeChat was inspected with Accessibility Insights. The accessible tree stopped at the outer WeChat/render shell and exposed `MMUIRenderSubWindowHW`, not per-message text/bubble nodes.

Current WeChat community implementations report the same failure mode for self-drawn 4.x chat rendering.

**Reason**

Building message ingestion on an interface that is absent in the user's real environment would be brittle and block the project immediately.

**Rejected alternatives**

- Keep searching the same normal UIA tree and assume message nodes exist
- Design the pipeline around `TextControl`/`ListItemControl` message access

**Allowed residual use**

Win32/UIA/accessibility tooling may still help with outer-window discovery, diagnostics, menus/dialogs, or future WeChat versions. It is simply not the message-content contract.

---

## D-004 — Visual-first message perception

**Decision**

Use:
- Win32/window APIs for exact window/lifecycle/monitor/DPI information;
- visual capture for the WeChat chat surface;
- computer vision for bubble geometry/side;
- OCR for text.

**Reason**

This fits the user's current WeChat rendering behavior while maintaining a non-invasive companion-app design.

**Rejected alternatives**

- OCR the entire desktop continuously
- Treat screen coordinates as fixed
- Couple the semantic layer directly to screenshot pixels

---

## D-005 — No process injection / binary patching / database decryption in V0

**Decision**

Do not:
- inject into the WeChat process;
- patch `Weixin.dll`;
- write WeChat process memory;
- hook private protocols;
- depend on decrypted local WeChat databases.

**Reason**

These approaches are more invasive, version-coupled, harder to maintain, and conflict with the desired independent companion overlay.

**Rejected alternatives**

- Qt accessibility hot activation via process patching
- WeChat internals/database extraction as the main ingestion path

This may be revisited only after explicit user approval and only if the non-invasive path proves insufficient.

---

## D-006 — Overlay is separate from WeChat

**Decision**

Render the HUD in an independent transparent Windows overlay/companion window.

**Reason**

Avoid modifying WeChat, keep rendering under our control, and allow independent layout/debugging.

**Rejected alternatives**

- Inject custom UI into WeChat
- Modify WeChat resources/layout

---

## D-007 — Anchor HUD to remote message geometry

**Decision**

Default HUD placement is to the right of a remote/left-side message bubble, using the bubble's detected bounding box.

**Reason**

The user's real dark-theme WeChat layout has a large unused area to the right of remote bubbles. This preserves WeChat's reading flow and avoids covering the original message.

**Rejected alternatives**

- Fixed global side panel only
- HUD below every message, which can interfere with vertical reading flow
- Absolute desktop coordinates

---

## D-008 — Multi-monitor and DPI are architectural requirements

**Decision**

Track the WeChat window rather than assuming a fixed screen/location. Implement explicit physical-pixel/DIP transformations and Per-Monitor DPI Awareness V2.

**Reason**

The user has a laptop display plus an external monitor and commonly positions WeChat on the right side of the laptop screen. They may move it later.

**Rejected alternatives**

- Assume a single display
- Assume screen origin `(0,0)` is always the relevant monitor
- Hard-code the current laptop placement

---

## D-009 — Detect change before expensive OCR/inference

**Decision**

Steady state should use lightweight frame/ROI change detection and only OCR/infer new or changed candidate messages.

**Reason**

Continuous full-window OCR wastes resources and increases latency/cost without adding value.

**Rejected alternatives**

- OCR the entire WeChat window many times per second
- Call Jev for every visible message on every frame

---

## D-010 — Privacy-minimizing state

**Decision**

Keep recent conversation state in memory and send Jev only the context needed for current judgments. No raw chat persistence by default.

**Reason**

Conversation content is private, and the product does not require permanent storage for V0.

**Rejected alternatives**

- Persist every screenshot
- Upload entire chat history by default
- Verbose raw-text logs as normal diagnostics

---

## D-011 — TypeSafe skill and live docs govern Jev integration

**Decision**

Codex must install/use the official TypeSafe skill and re-read current live docs before implementing Jev API code.

**Reason**

The official skill explicitly states the live docs are the source of truth for current concepts, API contracts, SDKs, limits, and cookbooks.

**Current handoff limitation**

During handoff creation, the official skill file was successfully read from TypeSafe's GitHub repository, but the live `docs.typesafe.ai` Markdown pages were not accessible from this chat tool. Therefore this handoff deliberately does not freeze a request/response schema that may be version-dependent.

**Rejected alternatives**

- Guess current Jev endpoints/fields from memory
- Copy an old integration without checking migration/current docs

---

## D-012 — Initial stack is Windows native .NET/WPF, but perception components remain replaceable

**Decision**

Default implementation stack:
- .NET 8
- WPF host/overlay
- Win32 interop
- modular capture/detection/OCR/Jev interfaces

**Reason**

The hard parts are Windows window tracking, DPI, overlay anchoring, and capture, for which a native Windows stack is a good fit.

**Status**

This is a strong default rather than an irreversible product requirement. A temporary Python/OpenCV feasibility harness is allowed if it materially accelerates a spike, but production modules should remain cleanly replaceable.

**Rejected alternatives**

- Electron-first architecture where native window/perception problems become secondary wrappers
- Locking V0 to one OCR engine before real accuracy tests

---

## D-013 — Stepwise implementation

**Decision**

Codex implements one acceptance phase at a time and produces evidence before moving forward.

**Reason**

The project contains multiple uncertain interfaces (WeChat capture, bubble detection, OCR accuracy, Jev latency, overlay anchoring). A monolithic build would make failures hard to isolate.

**Rejected alternatives**

- "Build the whole app" in one pass
- Hide failing perception behind increasingly complicated semantic/UI layers

---

## D-014 — HWND title is diagnostic metadata, not conversation identity

**Decision**

Do not use the WeChat top-level window title as the current conversation identity.
It may be reported for window diagnostics, but later conversation-switch detection
must use evidence from the captured WeChat surface or another separately validated
signal.

**Evidence**

Phase 1 testing on the user's real WeChat showed that the top-level HWND title does
not reliably equal the visibly selected chat name.

**Reason**

Treating the title as authoritative would mix message state between conversations
when the metadata is stale or unrelated to the visible chat.

**Rejected alternative**

- Use `GetWindowText(HWND)` as the conversation key.

---

## D-015 — Detection scores are not confidence probabilities

**Decision**

Name the Phase 2 heuristic output `DetectionScore` in code and
`detection_score` in diagnostics. Do not label it as confidence or probability.
Keep it distinct from `OcrConfidence` and Jev `Probability`/confidence values.

**Reason**

The current value combines shape and alignment heuristics. It is useful for
ranking and thresholding but has not been calibrated against an empirical
probability distribution.

**Rejected alternative**

- Present the heuristic value as generic `Confidence`, which could incorrectly
  imply that `0.94` means a calibrated 94% likelihood.

---

## D-016 — OCR remains replaceable and uses an evidence-based adaptive candidate

**Decision**

Keep Windows Media OCR and Tesseract behind `IOcrEngine`. For the Phase 3 candidate,
run confidence-bearing Tesseract raw/upscaled variants and accept the strongest result
only at or above `0.90`; otherwise fall back to upscaled Windows Media OCR. Preserve
`OcrConfidence` as nullable and separate from `DetectionScore`. Because the fallback
has no confidence signal, surface its non-empty text as `LowConfidence` for downstream
human review rather than silently treating it as trusted.

**Evidence**

Real WeChat bubble crops showed complementary behavior: Windows Media OCR was better
on short Chinese, while Tesseract `chi_sim+eng` was better on the representative long
wrapped, Chinese/English, and quoted-region crops. High-contrast preprocessing damaged
small strokes and was rejected. Windows Media OCR does not expose a confidence value;
inventing one would be misleading.

The initial `0.75` adaptive threshold was superseded after the short-Chinese stress
set produced incorrect but higher-scoring results at `0.77` and `0.87`. Raising the
candidate threshold to `0.90` turns those cases into explicit low-confidence fallback
outcomes instead of trusted recognized text. This remains an empirical safety policy,
not probability calibration.

An isolated recognition-only PaddleOCR benchmark was added before final Phase 3
acceptance. On the same private crop corpus, both PP-OCRv6 small and medium materially
improved short-Chinese exact match, but both also assigned high `rec_score` values to
wrong results and truncated multiline crops. `rec_score` is therefore recorded as an
engine-specific, uncalibrated diagnostic—not as `OcrConfidence`, `DetectionScore`, or
a probability. Small is the preferred candidate for later explicit routing because
medium provided no accuracy benefit. Existing Adaptive/Tesseract remains useful for
multiline cases. Paddle is not productionized by the Phase 3 PR.

**Status**

Phase 3 is accepted. The adapters and selection policy remain independently
replaceable; Paddle remains an evaluated candidate rather than a production adapter.

**Rejected alternatives**

- OCR the whole WeChat window.
- Choose an engine before comparing real crops.
- Relabel Tesseract mean confidence as detection confidence or a calibrated accuracy
  probability.
- Manufacture a confidence value for Windows Media OCR.
- Use Paddle text detection after Phase 2 has already supplied reliable crop geometry.
- Treat Paddle `rec_score` as calibrated or directly comparable across engines.

---

## D-017 — OCR evaluation preserves raw and normalized truth separately

**Decision**

Every OCR evaluation row records raw recognized text, normalized recognized text,
literal raw exact match, normalized match, raw CER, and normalized CER. Generic
`exact_match` is an alias for literal raw equality only. Normalization is CJK-aware and
conservative: it may remove artificial spacing between CJK characters, CJK-adjacent
full-width punctuation spacing, and OCR separator artifacts, but it preserves normal
Latin punctuation spacing such as `123, I just got home.`.

**Reason**

The first real English run returned `123,I` while the expected text contained
`123, I`. The previous normalization removed the expected space too, incorrectly
reporting an exact match. Separate layers preserve evaluation truth while still making
CJK OCR artifacts measurable.

**Rejected alternatives**

- Call normalized equality an exact match.
- Remove spaces adjacent to every Unicode punctuation character.
- Discard raw engine output before evaluation.

---

## D-018 — Message observation uses epoch-scoped ordered reconciliation

**Decision**

Phase 4 places stateful observation behind `IMessageObserver`. It establishes a
bootstrap baseline per visual conversation epoch, compares cheap chat-ROI fingerprints
before bubble detection, and reconciles changed views with ordered sequence alignment.
Logical identity combines message side, visual crop fingerprint, normalized OCR text
when already available, and relative order. Geometry is updated state, not identity.

A replaceable visual chat-header evidence provider canonicalizes a stable header
subregion and compares perceptual hashes by distance; the HWND title is never a
conversation key. A header mismatch is only a possible conversation change. The
observer reconciles visible messages before deciding, rebases the accepted header when
message continuity is strong, and requires three stable observations without strong
continuity to confirm a switch. Strong identity continuity is scoped to the immediately
previous visible snapshot and requires trusted normalized text plus side, or a stricter
visual threshold across at least two ordered matches. Permissive perceptual matches and
matches found only in older recent history remain weak evidence and cannot approve a
rebase. A live-tail match is strong only with independently observed trusted text or as
part of the multi-message strict visual continuity. OCR reused from a visual match does
not become independent trusted-text evidence.
Dimension/ROI changes enter an explicit layout transition and cannot cause an immediate
epoch change. The provider owns its opaque evidence and comparison thresholds so
another identity implementation does not have to expose perceptual-hash internals to
the observer. Recent state is bounded and memory-only. Only non-empty `Recognized` OCR
is semantic-ready.

A confirmed switch enters `AwaitingInitialSnapshot` rather than accepting an empty
transition frame as the new baseline. The first stable non-empty view is bootstrapped.
A non-empty view is stable after two consecutive strongly equivalent observations by
default, while all messages discovered during settling remain Bootstrap. A truly empty
conversation is established only after a configurable stable-empty gate (three
observations by default), after which its first later message may be live-new. Both
gates are explicit options. The final stable non-empty snapshot supplies the live-tail
anchor; finalizing an empty baseline clears provisional settling messages and tail.
This baseline-settling state is separate from conversation-identity evidence: it fixes
render timing without changing the strong/weak identity hierarchy.

**Scrolling policy**

The observer remembers the chronological live tail. Bubbles discovered before or away
from that anchor are conservative history, while unmatched suffixes after the known
live tail are live-new. Insufficient-overlap cases are suppressed rather than risk
replaying old history. An all-identical ambiguous growth is also history unless a
distinct matched bubble anchors the live edge. Ordered alignment deliberately
preserves separate occurrences of repeated equal text when the sequence is anchored.

**Reason**

Screen Y changes during append, scroll, resize, and restore. A global side-plus-text
set would collapse legitimate repeats, while coordinate identity would replay nearly
everything after movement. Epoch-scoped sequence reconciliation retains identity
without persistent chat logging.

The identity policy deliberately prefers temporarily retaining the current epoch when
evidence is ambiguous. This avoids false bootstrap/OCR storms during resize and
per-monitor DPI rerendering. A confirmed different conversation still clears the old
state once, waits for the target view to settle, bootstraps the new visible view, and
emits no old messages as live-new.

**Rejected alternatives**

- Treat bubble Y coordinate as message identity.
- Globally deduplicate by side plus text.
- Emit every newly visible bubble after scrolling.
- Use the top-level WeChat HWND title as conversation identity.
- Treat exact raw header-pixel hash inequality as an immediate conversation switch.
- Confirm a switch from one mismatching frame.
- Rebase from two permissive visual matches anywhere in recent history.
- Treat a permissive perceptual match to the old live tail as strong continuity.
- Persist screenshots or raw conversation history to support reconciliation.

---

## D-019 — Production OCR uses routed evidence, not Paddle score trust

**Decision**

Phase 4.5 runs `PP-OCRv6_small_rec` through a persistent Windows-native Python worker
for scale-aware single-line crops only. The worker uses PaddleOCR recognition without
Paddle text detection, loads and warms once, transfers image bytes in memory, and
reports an ID-correlated UTF-8 protocol plus exact runtime/device metadata. Multiline,
wrapped, quoted, and ambiguous crops remain on the existing Adaptive path.

Paddle `rec_score` is retained only as `EngineScoreKind = paddle_rec_score`; it never
populates `OcrConfidence` or establishes semantic trust. Paddle-only results and
Paddle/secondary disagreement remain untrusted. Adaptive-only and worker-fallback
output remains available as a text candidate but is untrusted in the production
router. A new agreement result is trusted only when Paddle, the selected Adaptive
output, and a separate confidence-bearing OCR candidate normalize to the same text.
Worker failure falls back to Adaptive without terminating observation or losing the
candidate text.

**Reason**

Phase 3 showed strong short-Chinese accuracy from the small model, no benefit from the
medium model, high-score wrong Paddle outputs, and multiline truncation. An initial
Phase 4.5 two-engine policy also reproduced a shared wrong result (`啦` read as `哒`) in
both Paddle and Windows OCR. Requiring stronger independent evidence reduced the
18-crop production-policy evaluation from one trusted-wrong result to zero without
using an invented Paddle score threshold.

The subsequent 52-crop calibration exposed six trusted-wrong results: three cases
where trusted Adaptive output overrode a correct disagreeing Paddle result, and three
high-confidence Adaptive-only multiline errors. Production trust was therefore
tightened again: disagreement and Adaptive-only output can no longer become
semantic-ready. This preserves diagnostic/candidate text while preferring safe failure.

**Rejected alternatives**

- Launch one Python process per crop.
- Make WSL part of the installed application runtime.
- Use `PP-OCRv6_medium_rec` after it showed no accuracy benefit.
- Use Paddle text detection instead of the accepted bubble detector.
- Route a whole multiline/quoted crop through Paddle recognition.
- Map `rec_score` to `OcrConfidence` or trust a high score.
- Trust agreement between Paddle and uncalibrated Windows OCR without additional
  independent evidence.

---

## D-020 — Audit OCR image evidence before further trust tuning

**Decision**

Phase 4.5 manual observer acceptance and further production trust-policy changes are
paused while input preparation is audited on the same real bubble crops at 100% and
150% DPI. The private audit compares the raw whole bubble, a contrast-derived text ROI
with safe padding, text-band normalization at 32/40/48 px using nearest, bicubic, and
Lanczos interpolation, and a conservative grayscale/background normalization. It uses
one `PP-OCRv6_small_rec` instance and does not use Paddle text detection, perspective
correction, deskew, dewarp, or aggressive thresholding.

The audit is evidence only: it does not change production preprocessing, routing, or
trust. Paddle `rec_score` remains uncalibrated. Agreement among preprocessing variants
must not be described as independent-engine agreement. Private crops, variants, and
reports remain under `.ocr-cache` and are manually inspected before any policy change.

**Reason**

The 52-crop corpus showed 46/47 exact Paddle single-line results but only 6/52
semantic-ready results, while the real observer exposed both exact-but-untrusted output
and genuine English recognition errors. Image-input quality and evidence/trust policy
are therefore separate problems and must be measured separately before either is
changed.

**Audit outcome and follow-up**

All 196 runs (14 crops × 14 variants) were raw/normalized exact, including 14/14 raw
whole bubbles. No variant demonstrated improvement. Freeze production Paddle input
as raw bubble crops; ROI, resampling and grayscale remain experiments only.

The next investigation reproduced a routing defect: rounded border pixels produced
two false row bands at 144 DPI. Routing excludes boundary-connected contrast regions
and scales row-band thresholds from estimated glyph height. This mask is used only
for route selection and never replaces OCR pixels. Trust policy remains unchanged
pending routing acceptance. Native-pixel HTML inspection replaces table-fit images
for judging sharpness.

---

## D-021 — Evaluate Paddle detection only inside already-isolated bubbles

The user explicitly superseded the earlier prohibition on Paddle text detection for
an isolated Phase 4.5 benchmark. Phase 2 still owns message-bubble detection. The
experiment uses `PP-OCRv6_small_det` only inside each bubble/independent quote crop,
then `PP-OCRv6_small_rec` per line. Both models are loaded/warmed once in a native
Windows process. Orientation, unwarping and line-orientation modules are not created.
Axis-aligned line bounds are cropped without perspective correction. Per-line raw
strings are retained; CJK wraps concatenate and Latin wraps receive a separating
space. This composition is explicit and independent of expected labels.

The 84-entry real-crop comparison (76 byte-distinct PNGs) improved raw/normalized
exactness from routed 71/84 to 78/84. Seven multiline/quote entries were all exact,
including a three-line English crop. Both DPI cohorts remained 7/7. However,
calibration single-line exactness decreased from 46/47 to 44/47 due to three new
full-width/ASCII punctuation substitutions (one old punctuation-space error improved).
Two existing Remote glyph errors remain. These scores do not establish trust.

Production routing, worker protocol and trust are unchanged by this experiment.
The architecture is promising, but the router is not deleted pending review of the
single-line regression and broader paired-DPI multiline evidence. No generic score
threshold or Adaptive-agreement trust policy is introduced.

---

## D-022 — Unified Paddle extraction is the recommended next production candidate

The isolated follow-up experiment runs small detection on every pre-isolated crop.
Zero or one detection uses the original whole bubble with small recognition; two or
more detections reuse D-021 line cropping/order/composition. Zero detection does not
select Adaptive. No preprocessing or detector-box padding is added.

On the same 84 entries / 76 byte-distinct crops, Unified is 79/84 raw/normalized
exact versus detector-line-rec 78/84 and current routed 71/84. Calibration single-line
accuracy is restored to 46/47 while multiline/independent quote remains 7/7. Both
single-line DPI sets remain 7/7. There are no new errors relative to current routed
or same-run raw single-line recognition. Three punctuation-width errors are fixed;
two English samples lose a comma-following space that detector-line-rec preserved.
Those two failures were already present in raw whole-bubble recognition.

Recommend removing `ScaleAwareOcrRoutingPolicy` from the normal production path in a
separately authorized implementation, using Unified extraction and retaining Adaptive
only for runtime failure as explicitly untrusted fallback. This benchmark does not
make that production change or establish trust. Existing Remote glyph errors and
Latin wrap/spacing ambiguity remain. Real zero-detection behavior was not observed;
its whole-crop branch has deterministic test coverage only. Dual-DPI multiline
coverage remains limited. Phase 4.5 is IN PROGRESS.
