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
