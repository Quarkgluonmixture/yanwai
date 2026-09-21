# ACCEPTANCE.md

Codex should treat these as implementation gates.

## Phase 0 — Repository/bootstrap

### Goal

Create the minimal Windows-native project skeleton without implementing the whole product.

### Acceptance

- [ ] .NET solution/build works on the Windows toolchain.
- [ ] Main modules/interfaces exist for window tracking, capture, bubble detection, OCR, Jev, and overlay.
- [ ] `TYPESAFE_API_KEY` or equivalent local secret mechanism is documented but no key is committed.
- [ ] `.gitignore` excludes local secrets, debug screenshots, OCR caches, and generated artifacts as appropriate.
- [ ] TypeSafe skill is installed/available to Codex.
- [ ] Codex has read current TypeSafe live docs before writing Jev API code; if Jev is not yet being implemented, record this as a later gate.
- [ ] App can launch a basic debug window/status without requiring Jev.

Do not implement later phases merely to make Phase 0 look complete.

---

## Phase 1 — WeChat window tracking and frame capture

**Status: PASS — manually verified on the real dual-monitor Windows machine.**

### Goal

Reliably identify the user's visible WeChat and capture its current surface/region.

### Acceptance

- [x] Finds the correct WeChat top-level window without requiring a fixed desktop coordinate.
- [x] Reports HWND/process/title/class/bounds/monitor/DPI in debug output.
- [x] Produces a visible captured frame of the current WeChat window/render area.
- [x] Moving WeChat within the same monitor updates bounds.
- [x] Moving WeChat to the other monitor updates monitor/DPI/bounds correctly.
- [x] Minimize/restore does not crash; capture suspends/resumes.
- [x] Coordinates support negative virtual-screen positions.
- [x] No injection, patching, local DB decryption, or private protocol use.

### Human verification

Provide one command/button that saves a single explicitly requested debug frame. The user should be able to visually verify it matches WeChat.

Manual verification evidence:
- laptop `DISPLAY1`: 2560×1600 at 150% DPI;
- external `DISPLAY5`: 1920×1080 at 100% DPI;
- capture remained correct across monitor/DPI changes;
- minimize suspended capture, restore resumed it, and no crash occurred.

---

## Phase 2 — Chat ROI and bubble detection

**Status: PASS — automated and manual acceptance complete.**

### Goal

Detect visible text-message bubble geometry and classify left/right side.

### Acceptance

Using the user's dark-theme WeChat layout and fixture screenshot:

- [x] Debug output draws the chat ROI.
- [x] Remote text bubbles receive `Remote`.
- [x] Self text bubbles receive `Self`.
- [x] Centered time labels are not classified as message bubbles.
- [x] Obvious avatar images are not classified as bubbles.
- [x] Detection returns capture-relative bounding boxes.
- [x] Window move does not change capture-relative message geometry.
- [x] No absolute global screen pixel constants are required.
- [x] Detector exposes a heuristic `detection_score` uncertainty indicator.

### Fixture target

Use `docs/assets/wechat-dark-layout-reference.png` as one regression fixture.

### Exit condition

Do not add OCR until the debug overlay/frame makes bubble detection visually credible.

Implementation evidence:
- reference fixture: 5 `Remote`, 6 `Self`, 0 `Unknown`, with no timestamp/avatar detections;
- real 150% and 100% captures: text bubbles remained detected after the DPI/monitor change;
- current image and sticker messages were ignored;
- debug output includes ROI, labeled boxes, heuristic detection scores, capture-relative coordinates, and `bubble_detect_ms`.

Manual evidence for 1277×1526 dark-theme captures:
- the user confirmed correct `Remote` and `Self` detection with no obvious false positives or false negatives;
- avatars, timestamps, image/sticker messages, composer, and conversation list were excluded;
- quoted reply text was not classified as a separate message bubble.

Visual comparison evidence (false-positive/false-negative counts are human judgments):

| Capture | DPI/layout | Visible text bubbles | Detected | False positives | False negatives |
| --- | --- | ---: | ---: | ---: | ---: |
| `docs/assets/wechat-dark-layout-reference.png` | 150% reference | 11 | 11 | 0 | 0 |
| `wechat-20260921-031220.png` | 100% | 4 | 4 | 0 | 0 |
| `wechat-20260921-030720.png` | 150% | 10 | 10 | 0 | 0 |
| `wechat-20260921-031243.png` | compact 100% | 4 | 4 | 0 | 0 |
| `wechat-20260921-033329.png` | current live 150% | 10 | 10 | 0 | 0 |

Cross-scale implementation evidence:

| Capture | Frame | DPI/layout | Detected | Debug artifact |
| --- | --- | --- | ---: | --- |
| `wechat-20260921-031220.png` | 989×680 | 100% external monitor | 4 | `wechat-20260921-031220-bubbles-detection-score.png` |
| `wechat-20260921-034532.png` | 662×680 | 100% narrow window | 6 | `wechat-20260921-034532-bubbles-detection-score.png` |

The automated cross-scale test also exercises 989×680 and 662×680 layouts
through the public ROI/detector pipeline and asserts capture-relative ROI,
`Remote`/`Self` bounds, and bounded heuristic detection scores.

The matching `*-bubbles.png` files in the ignored `debug-captures/` directory
are the local visual artifacts. They are intentionally not committed because
real chat captures are private.

Final manual acceptance evidence (2026-09-21):
- the user accepted the latest 989×680, 100% DPI external-monitor result and the
  662×680 narrow-window result;
- `Remote`/`Self` boxes remained correct at 100% and 150% DPI and after resizing;
- timestamps, avatars, conversation list, composer, and quoted reply text remained
  excluded from independent bubble detections;
- the user accepted `detection_score` as the correct name for the uncalibrated
  heuristic score.

The Phase 2 human exit gate was satisfied before Phase 3 began.

---

## Phase 3 — OCR

**Status: PASS — automated evaluation and manual acceptance complete.**

### Goal

Extract text from individual detected message crops.

### Acceptance

- [x] `IOcrEngine` abstraction exists.
- [x] OCR runs on bubble crops, not entire dual-monitor desktop.
- [x] Simplified Chinese short text works on representative samples.
- [x] Long wrapped Chinese text works on a representative sample.
- [x] Mixed Chinese/English is tested.
- [x] OCR result exposes confidence if available.
- [x] Low-confidence text is surfaced as uncertain/skipped rather than silently trusted.
- [x] Emoji-only/sticker/image messages may return `Unsupported`/skip in V0.
- [x] Quoted reply main text and optional quote-region text are evaluated separately; automatic quote-region location remains explicitly unsupported in Phase 3.

### Evaluation artifact

Provide a small table/console report:

```text
fixture | expected | raw_recognized | normalized_recognized | status | ocr_confidence | raw_exact_match | normalized_match | raw_cer | normalized_cer | elapsed_ms
```

`exact_match` means literal equality between the expected text and raw OCR output.
Normalization is evaluated separately and can never promote a raw mismatch to an
exact match.

Implementation evidence:
- the evaluation harness accepts capture-relative bubble bounds, extracts only those
  crops, and writes both a Markdown report and the exact crop PNGs used;
- Windows Media OCR and Tesseract `chi_sim+eng` adapters were compared on the same
  real WeChat crops using raw/upscaled variants;
- no single candidate dominated: Windows OCR was strongest on short Chinese but does
  not expose confidence, while Tesseract was strongest on the long wrapped, mixed,
  and quoted-region samples and exposes `OcrConfidence`;
- the candidate composition therefore uses confidence-bearing Tesseract results at or
  above `0.90`, with Windows OCR as the short-text fallback; every engine remains behind
  `IOcrEngine`;
- after safety-threshold recalibration, only results with Tesseract confidence at or
  above `0.90` are promoted to `Recognized`; all evaluated incorrect adaptive outputs
  are now `LowConfidence` or `NoText`, never trusted text;
- pure English is covered by both an automated OCR integration test and a real
  WeChat English-only bubble captured from File Transfer Assistant;
- Tesseract results below `0.90` return `LowConfidence`; Windows results preserve
  `OcrConfidence = null` because that API supplies no confidence value. Adaptive
  fallback text is also marked `LowConfidence` rather than silently promoted.

The reproducible public manifest is `fixtures/ocr/phase3-public.json`. The full local
run, including a private long wrapped capture, is written to the gitignored
`.ocr-cache/phase3-evaluation.md`; its source crops are in
`.ocr-cache/phase3-evaluation-crops/`.

The Tesseract rows are a real-model integration check, not part of the hermetic unit
suite: the pinned language models are downloaded into the gitignored `.ocr-cache`
directory. Unit tests cover crop isolation, preprocessing, normalization, adaptive
threshold/fallback behavior, Windows OCR, and the committed real screenshot crop.

Final manual-acceptance evidence in the private/gitignored evaluation area:
- 11 short Chinese bubbles were taken directly from Phase 2 detector boxes across two
  real local captures;
- adaptive output produced 7/11 normalized matches: 2 `Recognized`, 5 conservatively
  `LowConfidence`; the remaining 3 normalized mismatches were
  `LowConfidence` and the one-character sample returned `NoText`;
- no incorrect adaptive result was left as `Recognized`; the observed corpus-level
  normalized character error rate was `0.222` (8 edit operations over 36 expected
  characters);
- all 11 generated short-message crops and the existing six main/quote evaluation
  crops were visually inspected and contain only their intended bubble or separately
  supplied quote region;
- the short-set manifest, full candidate tables, and crop PNGs remain under
  `.ocr-cache/` and are intentionally not committed.

Final PaddleOCR recognition-only benchmark evidence:
- `scripts/paddle_ocr_benchmark.py` uses the official `TextRecognition` module only;
  the existing Phase 2 detector supplies the crop geometry and Paddle text detection
  is never invoked;
- PaddlePaddle `3.3.0` and PaddleOCR `3.7.0` ran locally on `gpu:0` (RTX 5080 Laptop
  GPU). One warmup per model was excluded from timing;
- on the same 11 private short-Chinese crops, both `PP-OCRv6_small_rec` and
  `PP-OCRv6_medium_rec` produced 10/11 raw-exact strings and raw/normalized corpus
  CER `0.056`; Adaptive OCR produced 7/11 normalized matches and normalized corpus
  CER `0.222` (its raw layer retains engine-inserted CJK spacing);
- across all 18 real crops, each Paddle model produced 12/18 raw-exact and 12/18
  normalized matches; Adaptive OCR produced 0/18 raw-exact and 10/18 normalized
  matches because its engines expose raw OCR spacing/line artifacts separately;
- the real English fixture expected `Hello OCR test 123, I just got home.` while
  PP-OCRv6 small returned `Hello OCR test 123,I just got home.`. The corrected
  evaluator reports `raw_exact_match=false`, `normalized_match=false`, and raw/
  normalized CER `0.028` rather than hiding the missing space;
- PP-OCRv6 small produced five incorrect raw outputs with `rec_score >= 0.90`;
  medium produced four.
  Therefore `rec_score` remains an uncalibrated engine-specific diagnostic and must
  not by itself promote text to trusted `Recognized` status;
- direct whole-crop recognition truncated the long wrapped and quote-region samples,
  confirming that a future Paddle production adapter would need an explicit
  recognition-only line-splitting policy without reintroducing Paddle text detection;
- the two independently generated crop sets were pixel-identical for all 18 inputs.
  Full reports and crops are stored under `.ocr-cache/` and are not committed.

Final Phase 3 conclusions:
- `PP-OCRv6_small_rec` materially improves short/single-line Chinese recognition;
- `PP-OCRv6_medium_rec` is rejected because it produced no accuracy benefit;
- Paddle `rec_score` is uncalibrated, wrong high-score outputs were observed, and it
  must not be treated as a correctness probability;
- whole-crop Paddle recognition truncates multiline and quoted-region text;
- existing Adaptive/Tesseract remains useful for multiline cases;
- future Paddle production use requires explicit routing and disagreement handling,
  not `rec_score` thresholds alone;
- Paddle remains an evaluated candidate and is not productionized in this PR.

The user supplied and accepted the real English-bubble test as the final manual gate.
The corrected 18-crop evaluation completed successfully, so Phase 3 is PASS. Phase 4
began separately after the Phase 3 PR was merged.

---

## Phase 4 — New-message observer and conversation state

**Status: PASS — automated and real-machine manual acceptance complete.**

### Goal

Process new/changed messages once, not every frame.

### Acceptance

- [x] Lightweight change detection avoids unnecessary OCR on unchanged frames.
- [x] A visible message persisting across frames does not trigger repeated OCR work.
- [x] A newly appearing remote text message produces one normalized `ObservedMessage`.
- [x] Recent-message state is bounded and kept in memory.
- [x] Deterministic scroll reconciliation does not replay ordinary old history.
- [x] Switching conversation creates a new epoch and replaces recent state.
- [x] No raw conversation or screenshot persistence by default.
- [x] Timings/counts are instrumented.

Automated evidence:
- stable identical frames skip bubble detection and OCR;
- one appended remote message and one appended self message each emit once;
- two consecutive Remote `好` messages receive distinct logical IDs;
- Self `嗯` and Remote `嗯` remain distinct;
- scrolling to existing history and returning to the live edge does not replay known
  messages;
- an all-identical sequence growing by one ambiguous bubble is conservatively treated
  as history rather than replayed as live-new;
- a true stable low-overlap header change increments the epoch once, clears prior
  state, and bootstraps the new view without a fresh-message event;
- a minimize/restore-equivalent capture suspension retains reconciliation state and
  does not replay the restored frame;
- `LowConfidence` OCR remains observable but has `IsTrustedForSemantics = false`;
- message-count limits are configurable and enforced;
- slight header rerendering, wider/narrower resize, simulated 150%/100% DPI scaling,
  and gradual multi-frame resize retain one epoch;
- six strict matches against the immediately previous visible snapshot survive resize
  and rebase the accepted identity without repeated OCR;
- trusted text plus side contributes strong previous-visible continuity;
- two permissive visual matches in a different chat do not rebase;
- two to four history-only perceptual matches do not prevent pending/confirmed switch;
- a permissive visual match to the old live tail is weak and does not approve rebase;
- a true low-overlap switch requires three stable observations, creates exactly one
  epoch, and bootstraps without replay;
- remaining in the switched conversation does not increment the epoch again;
- switching back creates exactly one further epoch and does not replay old state;
- empty and near-empty resize transitions settle without epoch churn;
- a same-size chat-ROI change starts a layout transition;
- returning to the accepted identity interrupts and resets a pending switch;
- replacing one pending candidate with another does not reuse the first candidate's
  cached OCR;
- a confirmed switch followed by one or more transitional empty frames keeps the new
  epoch in `AwaitingInitialSnapshot`; when existing target history appears, it is
  Bootstrap and emits zero `NEW` events;
- incrementally rendered non-empty target history remains Bootstrap until two
  consecutive strongly equivalent snapshots establish the baseline, and the final
  bootstrap tail still anchors a subsequent live append;
- a genuinely empty switched conversation establishes an empty baseline only after
  the configurable stable-empty gate (three observations by default);
- if a provisional non-empty snapshot precedes that stable-empty result, its staged
  messages and tail are discarded before the empty baseline is established;
- after that genuine empty baseline, the first later message emits exactly one `NEW`;
- temporary zero-bubble frames during a same-conversation layout transition neither
  change the epoch nor reset the established baseline;
- existing normal non-empty switch behavior remains covered by the three-observation
  confirmation and switch-back regression tests.

Real-machine diagnostic evidence before manual acceptance:
- a six-second redacted run checked 20 captured frames;
- the first frame ran bubble detection once and OCRed three bootstrap bubbles;
- the remaining 19 identical frames skipped bubble detection and OCR;
- no message was emitted, no screenshot/chat log was written, and no raw text was
  printed;
- observed first-frame timings were `capture_ms=78.3`, `frame_check_ms=27.1`,
  `change_detect_ms=1.1`, `bubble_detect_ms=20.0`, `ocr_ms=587.5`, and
  `observer_reconcile_ms=8.1`;
- the final 5.23-second unchanged window averaged `0.75%` process CPU normalized
  across logical processors. These are one-run diagnostics, not performance claims.

Blocking manual evidence from the first acceptance attempt:
- 3,311 frames were checked and 67 changed frames ran bubble detection;
- scrolling produced `history`/duplicate suppression rather than a `NEW` replay storm;
- resize and cross-monitor capture continued without a crash;
- the same conversation incorrectly advanced from epoch 3 through epoch 11;
- the run ended with 10 conversation switches and 161 OCR calls;
- the cause was exact raw header-pixel inequality committing a switch before visible
  message reconciliation.

Blocking manual evidence from the second acceptance attempt:
- same-chat resize and 150%/100% DPI moves remained in epoch 1;
- strong same-chat continuity, including 6/6 overlap plus live-tail continuity, rebased
  large header changes correctly;
- scrolling and minimize/restore remained suppressed without crashes or replay;
- deliberate switches to visibly different conversations incorrectly remained in
  epoch 1 with `REBASE_SAME_CONVERSATION` decisions, including aggregate overlaps of
  2/5, 4/7, and 3/4 without live-tail matches;
- the cause was treating permissive perceptual matches against the entire 25-message
  history as strong identity evidence. The second fix separates strong previous-visible
  continuity from weak visual/history alignment.

Blocking manual evidence from the third acceptance attempt:
- same-chat resize and cross-DPI behavior passed without epoch churn;
- normal non-empty conversation switches followed
  `pending_switch=1/3` -> `pending_switch=2/3` -> one confirmed epoch increment, and
  visible target history was bootstrap-only;
- weak visual overlap no longer suppressed a genuine switch;
- one switch confirmed while WeChat temporarily showed zero bubbles, and the first
  existing target message rendered afterward was incorrectly emitted as `NEW`;
- the cause was treating the confirming empty transition frame as an established empty
  baseline. The post-switch baseline fix now waits for a non-empty initial snapshot or
  a stable-empty settle gate. At that point, the exact real-machine transition still
  required retesting.

Final manual acceptance evidence (2026-09-21):

- the earlier real-machine run retained one epoch through same-conversation resize and
  150%/100% DPI moves;
- each genuine switch followed `PendingSwitch` 1/3 -> 2/3 -> exactly one
  `ConfirmedSwitch`, and switch-back created exactly one further epoch;
- `AwaitingInitialSnapshot` settled before baseline establishment, while existing
  target history remained Bootstrap and produced no `NEW` replay;
- neither switch produced a replay storm, and duplicate suppression remained stable;
- uncertain OCR remained observable with `semantic_ready=false`;
- the final inspected run recorded two confirmed switches, zero emitted/new messages,
  and no epoch churn. The user accepted the complete real-machine workflow, so Phase 4
  is PASS. Phase 5 remains separate and unimplemented.

---

## Phase 5 — Jev integration

### Goal

Turn normalized message + short recent context into typed probabilistic judgments.

### Preconditions

- [ ] Official TypeSafe skill is installed.
- [ ] Codex has re-read current live docs (`llms.txt`, API/SDK, state, primitives, confidence, relevant cookbook/pattern).
- [ ] Current API contract is implemented from live docs, not from this handoff.

### Acceptance

- [ ] API key is loaded from local secret/environment, never repository content.
- [ ] Jev client is isolated behind an interface.
- [ ] Multiple independent judgments over the same state are batched/parallelized according to current TypeSafe guidance where appropriate.
- [ ] Initial set includes at least:
  - expects response (Noul)
  - depends on prior context (Noul)
  - direct request (Noul)
  - speech act (Choice)
  - urgency or emotional intensity (Score)
- [ ] Raw probabilities/confidence are retained.
- [ ] `other`/no-match handling exists for bounded choices where needed.
- [ ] Jev outage/timeout does not crash or block the capture loop.
- [ ] API payload contains only the recent context needed for the question set.
- [ ] `jev_ms` is measured.

### Semantic quality

Create a small manually inspectable evaluation fixture set. The goal is not "100% accuracy"; it is to verify:
- prompts/questions mean what we intend;
- outputs are stable enough to be useful;
- uncertainty is preserved;
- no mind-reading/personality labels are introduced.

---

## Phase 6 — Anchored HUD overlay

### Goal

Show Jev judgments beside the corresponding remote message.

### Acceptance

- [ ] Transparent companion overlay exists independently of WeChat.
- [ ] HUD anchors to the detected remote bubble, normally to its right.
- [ ] HUD does not cover the source message in the normal reference layout.
- [ ] Move/resize WeChat -> HUD follows.
- [ ] Move WeChat between laptop/external monitor -> HUD remains correctly aligned.
- [ ] DPI change -> alignment remains correct.
- [ ] Scroll -> HUD reconciles/repositions/hides stale anchors.
- [ ] Minimize/hide WeChat -> HUD hides.
- [ ] Default behavior avoids leaving the HUD floating over unrelated foreground apps.
- [ ] Collapsed HUD shows only a few concise judgments.
- [ ] Debug-expanded view can show timings plus distinct detection scores, OCR confidence, and Jev probability/confidence.
- [ ] Overlay does not contaminate its own capture path.

---

## Phase 7 — End-to-end V0

### Scenario

A remote person sends a new text message while the user is viewing that WeChat conversation.

### Acceptance

The system completes:

```text
visible WeChat
-> change detected
-> remote bubble detected
-> bubble OCR
-> normalized message
-> short conversation state
-> Jev judgments
-> HUD beside that bubble
```

and:

- [ ] no automatic reply is generated/sent as a required action;
- [ ] no mouse/keyboard action is injected into WeChat;
- [ ] no WeChat process modification occurs;
- [ ] no chat database decryption is required;
- [ ] duplicate API calls are controlled;
- [ ] failures degrade gracefully;
- [ ] pipeline timings are visible in debug mode;
- [ ] idle CPU use and active latency are measured on the user's machine.

### Initial performance goals

Treat these as engineering goals to measure, not promises:

- idle/change-detection loop should be lightweight enough to leave running;
- semantic HUD should feel near-real-time after a simple text message appears;
- avoid long blocking work on the UI thread;
- no retry storm if OCR/Jev fails.

Document actual measured numbers before optimization.

---

# V0 completion definition

V0 is complete when the above end-to-end path works on the user's real Windows + dual-monitor + dark-theme WeChat setup with a useful anchored Jev HUD and without invasive WeChat modification.

A demo that only calls Jev on hard-coded text does **not** satisfy V0.

A demo that detects screenshots but cannot anchor the HUD to real messages does **not** satisfy V0.
