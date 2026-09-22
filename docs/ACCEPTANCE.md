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

## Phase 4.5 — Production OCR runtime

**Status: IN PROGRESS — Unified production extraction implemented and production
corpus parity verified; real observer matrix and separate trust calibration pending.**

### Observer regression gate (2026-09-22, D-024)

Phase 4.5 remains blocked on real-device observer acceptance. The user confirmed that
two suspicious NEW samples were genuinely newly sent (not history replay), but a
chat switch was missed. A controlled repeat then logged only the first of two Self
`好` messages as NEW; the second had a separate ID but History origin. A→B→A remained
epoch 1 and B content entered A history. Multiline text also changed on rediscovery;
the old logs cannot prove whether that crop was clipped.

- Five deterministic regressions were run red before fixes: Self/Remote equal
  appends, sparse different title ink, and top/bottom partial OCR suppression.
- Occurrence alignment now preserves previous-visible order and minimizes translated
  geometry displacement; a stationary identical suffix may emit NEW. The old
  all-identical scroll test was corrected to include actual viewport translation,
  because its former unchanged positions were indistinguishable from a real append.
- Title identity now uses tight ink with rasterization tolerance and structured
  distances. Old-chat OCR is not borrowed when a new title fails continuity approval.
- Partial candidates have explicit visibility/completeness, skip OCR, preserve
  complete text, and need anchored evidence for edge-based association. Full recovery
  OCRs incomplete text once; cached complete text needs a full-crop fingerprint or
  independent full OCR agreement after clipping.
- Private header audit: same title in 1116×680 / 662×680 captures had zero title
  distance; same title at known 150%/100% DPI had visual distance 0.0854 and aspect
  distance 0.0541 (same); different titles had 0.2972 / 0.4595 (different).
  Title PNGs and JSON stay under `.ocr-cache/header-identity-*-audit.*`.
  This small audit supports the representation, not general identity accuracy.
- No Unified Paddle extraction, OCR normalization, OCR trust calibration or Jev changes.
- Final native Windows gates: `dotnet format --verify-no-changes`, full solution
  build (0 warnings/errors), and 129 .NET tests passed (63 Observer, 53 OCR,
  6 Vision, 5 Windows, 2 Capture; no failures/skips).
- Post-fix 10-second real observer smoke: 32 frames, 31 unchanged, one detection
  run, 13 bootstrap OCR/Paddle requests, zero NEW/failures/fallbacks. Unchanged
  frames issued no additional OCR. This is not the interactive manual gate below.

Required manual retest (not yet PASS):

1. Self `好`, `好`, then Remote `好`, `好`: two NEW events and distinct IDs per side.
2. Scroll away/back: no replay.
3. A→B→A: each switch pending 1/3→2/3→confirmed, exactly one epoch increment;
   no B state in A and target history bootstrap-only.
4. Resize and 150%↔100% DPI within one chat: no epoch churn.
5. Scroll multiline history partially beyond each viewport edge, then reveal it:
   partial diagnostics, no partial OCR/corrupted text replacement, full OCR once.

### Goal

Use Unified small-det + small-rec for already-isolated bubbles without regressing
Phase 4 observation behavior. Trust calibration remains separate.

### Explicit live-edge follow-up (D-025)

- Post-D-024 manual run `observer-manual-retest-20260922-session2.log` still missed
  two Self appends and emitted a clipped historical fragment as NEW. A later controlled
  run `observer-manual-retest-20260922-183416.log` emitted exactly two Self and one
  Remote `好` with distinct IDs; A→B→A advanced epochs 1→2→3, bootstrap-only;
  subsequent resize/cross-DPI caused no extra epoch or NEW. The success does not erase
  the earlier failures. Both logs remain private under `.ocr-cache`.
- NEW authorization now runs in a dedicated live-edge append detector before history
  reconciliation, reserving chronological occurrences and the appended suffix.
  Generic LCS/history overlap cannot itself authorize NEW.
- Deterministic coverage includes Self/Remote stationary repeats of lengths 1–8 and
  suffixes 0–3; every two-symbol sequence through length six with either appended
  symbol on either side (504 combinations); anchored translation, offscreen/top-clipped
  old prefixes, scrolling, partial suffix rejection, and DPI→idle→append.
- The old post-switch-settling append fixture made a visible prefix disappear without
  motion. It now retains that prefix to test a real extension; a separate regression
  explicitly rejects unexplained prefix disappearance, without weakening assertions.
- Manual validation of this new detector remains pending. Repeat three Self and three
  Remote equal appends, scrolling/return, resize/DPI followed by another append, and
  A→B→A. All-equal moving views without an anchor remain ambiguous and suppressed.
- Bottom-clipped-history completeness remains an open independent blocker. This change
  does not alter OCR, Paddle, trust calibration, completeness or conversation identity.
- Final validation: Windows format verification passed; full Windows build passed with
  0 warnings/errors; all 209 .NET tests passed (143 Observer, 53 OCR, 6 Vision,
  5 Windows, 2 Capture), no failures/skips. First full build was blocked by the prior
  manual observer holding its DLL; after stopping that exact test process the build
  and tests passed. Python/model extraction tests were not rerun: no worker/OCR code
  changed. Two-axis review found and corrected top-partial and stale-DPI-fingerprint
  issues; real-machine validation remains outstanding, including multi-frame layout
  settling where reliable tail continuity is temporarily absent.

### Acceptance

- [x] A configurable persistent worker loads and warms both models once and emits an
  explicit version/device/timing `READY` handshake.
- [x] Crop bytes remain in memory and every UTF-8 protocol response is correlated by
  request ID.
- [x] Unified Paddle is behind `IOcrEngine`; detection occurs only inside isolated crops.
- [x] Zero/one line recognizes original whole crop; 2+ lines use clipped ordered crops.
- [x] Paddle `rec_score` remains engine-specific metadata and never becomes
  `OcrConfidence` or a trust threshold.
- [x] Paddle outputs remain untrusted pending separate calibration; no normal secondary engine.
- [x] Worker unavailable/startup/crash/timeout/malformed-response paths fall back to
  Adaptive without terminating the observer.
- [x] Runtime/observer counters and startup, warmup, inference, roundtrip, transport,
  and total OCR timing are exposed.
- [x] Unchanged observer frames issue no additional Paddle requests.
- [x] A visually inspected private 50–100 crop corpus, including the required
  polarity/negation pairs, has zero trusted-wrong results.
- [ ] The real observer workflow covers short Self/Remote `好`, another very short
  Chinese message, a negation, English, mixed text, and a long wrapped message.
- [ ] Phase 4 identity, deduplication, scrolling, and post-switch bootstrap behavior
  are manually reconfirmed with production OCR enabled.

### Unified production evidence (current)

- Final gates: Windows `dotnet format --verify-no-changes`, full solution build,
  and 112 .NET tests passed (including 46 observer regressions); native Windows Python
  worker/benchmark suite 31 tests passed. No skipped or failed tests.
- Deterministic coverage includes 0/1 original-pixel identity, changed one-line boxes,
  ordered/clipped multiline crops, CJK/Latin composition, dual-model warmup/READY,
  request correlation, detector/recognizer errors, crash, timeout, malformed/oversized
  boxes, untrusted fallback, no successful-path Adaptive, score isolation and idle OCR.

- The actual .NET `PaddleWorkerClient` / `PaddleRecognitionOcrEngine` /
  `UnifiedPaddleOcrEngine` replay matches accepted Unified outputs and boxes on all
  immutable 84 entries / 76 byte-distinct crops. Private report:
  `.ocr-cache/phase4.5-paddle-bubble/production-parity.md`; full rows `production-unified.json`.
- Exact/normalized: overall 79/84; calibration single-line 46/47; multiline/quote 7/7;
  96 DPI 7/7; 144 DPI 7/7; punctuation 16/18; Chinese 62/65; English 6/8; mixed 9/9.
- Healthy corpus fallback count 0. Semantic-ready remains 0; no score-based promotion.
- Final replay steady-state p50/p95 ms: worker roundtrip 18.8/42.7, detector 9.1/17.1,
  recognizer 8.4/30.8, total .NET OCR including PNG encode 19.4/46.4.
  These include the first post-warmup request, exclude startup/warmup, and have no
  interleaved benchmark control calls.
- Remaining real-device gate: Self/Remote 好, 不行, English, mixed, long single-line,
  two/three-line messages; scroll, resize/DPI, switch/bootstrap, minimize/restore.
  A current-view smoke run does not substitute for that matrix.
- Current-view smoke: seven real Self bubbles (好, 晚安, 不行, English, two mixed,
  longer Chinese) were extracted exactly with one detected line each, LowConfidence /
  semantic_ready=false and no fallback. 46 frames: 1 changed, 45 unchanged, 7 OCR
  calls only at bootstrap, 0 NEW. Worker startup/warmup 3007.1/565.0 ms.
  Remote, multiline and physical interaction matrix are not yet reconfirmed.

Historical routed-runtime evidence (superseded by D-023, retained for traceability):

- Final automated gates passed on Windows: solution format verification, a full build
  with 0 warnings/errors, 104 .NET tests, and 9 Python worker/benchmark tests.
- Windows-native Python 3.10 under `%LOCALAPPDATA%` loaded PaddleOCR 3.7.0,
  PaddlePaddle GPU 3.2.2, and `PP-OCRv6_small_rec` on `gpu:0` (RTX 5080); WSL is not
  an application runtime dependency.
- A real observer run established one four-bubble bootstrap, routed two crops to
  Paddle, then skipped OCR on all 33 unchanged frames. It reported 17.7 ms aggregate
  Paddle inference, 62.2 ms aggregate roundtrip, no failure/fallback, and no new event.
- The first 18-crop production run exposed and fixed a UTF-8 protocol decoding bug;
  its pre-fix accuracy is invalid evidence.
- The corrected two-engine policy produced one trusted-wrong shared glyph error
  (`没事啦没事啦` -> `没事哒没事哒`). The policy was deliberately tightened without a
  Paddle-score threshold.
- The stronger-evidence run produced 12/18 raw and normalized exact, 4/18
  semantic-ready, and 0 trusted-wrong. `好`, `嗯嗯`, and `怎么说` were correct Paddle
  outputs but remained safely untrusted where independent evidence was absent. At
  that point, the only represented polarity item (`好`) was correct; the later 52-crop
  run supplied the complete polarity corpus.
- The completed 52-crop private corpus was collected from seven batches and visually
  reviewed crop-by-crop. The corrected production policy produced 46/52 raw and
  normalized exact, 46/47 single-line exact, 6/52 semantic-ready, and 0 trusted-wrong.
- All 12 required polarity/negation samples were exact. The same-crop Adaptive-only
  baseline marked 5/47 single-line samples semantic-ready, but only 2 were correct and
  3 were trusted-wrong; routed production OCR produced 6 correct semantic-ready
  single-line samples and zero trusted-wrong.
- The initial 52-crop run exposed six trusted-wrong cases caused by trusting Adaptive
  disagreement or high-confidence Adaptive-only multiline output. The production
  router now keeps both categories as `LowConfidence` candidates; the rerun passed the
  corpus gate without using Paddle `rec_score` as a threshold.
- The passing run used `gpu:0`, reported 3431.5 ms startup, 589.8 ms warmup, 52.4/137.3
  ms total OCR p50/p95, and 11.4/12.1 ms Paddle inference p50/p95.
- The private report and crops remain under `.ocr-cache` and are not committed.
- The first real observer message-matrix attempt separated a recognition/input issue
  from a trust-policy issue: `好`, `不行`, and `微信 OCR test 456` were recognized
  exactly but remained `LowConfidence`; `晚安` was exact and `Recognized`; English
  produced `Hello` -> `H引0`, and a wrapped English sample gained an extra character.
  The 52-crop corpus simultaneously showed 46/47 exact single-line Paddle results but
  only 6/52 semantic-ready results. No score threshold will be tuned to hide this gap.
- Before further trust-policy work, a private same-crop input audit must compare raw
  whole bubbles, a safely padded contrast-derived text ROI, 32/40/48 px detected
  text-band normalization with nearest/bicubic/Lanczos interpolation, and conservative
  grayscale/background normalization on both 96 DPI (100%) and 144 DPI (150%) real
  captures. The same `PP-OCRv6_small_rec` instance must process every variant.
- The audit must retain bubble/ROI/text-band geometry, DPI, route, raw/normalized
  exactness and CER, `rec_score`, inference timing, and private side-by-side artifacts.
  Preprocessing-variant agreement is not independent-engine agreement.

Routing audit and fix evidence:

- Final routing-change gates: Windows format verification and full build passed
  (0 warnings/errors); 109 .NET tests including 46 observer tests passed; 17 Python
  audit/benchmark/worker tests passed. Review found and fixed EOF band flushing and
  stale viewer-manifest truncation; quoted-role diagnostic requests retain their role.
- Input audit completed: 14 real bubbles at 96/144 DPI, 14 variants, 196/196 raw and
  normalized exact. Raw whole-bubble input itself was 14/14; no preprocessing benefit
  was demonstrated. Production Paddle input remains the raw whole bubble.
- The actual .NET router counted three bands for two 144-DPI single-line crops.
  Corner/background pixels formed false bands at rows 4–6 and 47–49, with 4–5
  contrasting pixels crossing the four-pixel/three-row thresholds. At 96 DPI the
  same edges contributed only 1–2 pixels and did not cross the threshold.
- Routing now excludes contrast components connected to the crop boundary from its
  layout evidence. Glyph-height-derived gap and minimum-band thresholds replace fixed
  2/3-pixel values; the raw image sent to OCR is unchanged. Final bands flush at EOF.
- Replaying the 14 real crops through .NET yields 14/14 PaddleSingleLine routes.
  Five existing real multiline calibration crops remain Adaptive. Those older crops
  lack DPI metadata, so paired real multiline DPI acceptance remains outstanding.
- A real external-monitor observer run bootstrapped all seven texts exactly, made
  seven Paddle requests with zero failures, and skipped OCR on 27 unchanged frames.
  It emitted zero NEW events. All seven remained LowConfidence under unchanged trust.
- Private native HTML inspection shows source dimensions, ROI size, capture DPI,
  devicePixelRatio and viewport scale. Images scroll horizontally without fitting to
  table columns. Physical native viewing still needs browser/manual confirmation.

Manual acceptance remains blocked until paired multiline routing and the 144-DPI
observer rerun are confirmed, followed separately by review of trust evidence.
Paddle-only full-bubble experiment (production remains unchanged):

- Evaluated the 52 calibration crops, both seven-crop DPI sets and 18 Phase 3
  fixtures: 84 entries / 76 byte-distinct PNGs, including separately labeled quote
  regions. Older samples without recorded DPI retain unknown DPI rather than guesses.
- Windows GPU runtime: PaddleOCR 3.7.0 / PaddlePaddle 3.2.2; one small detector and
  one small recognizer loaded and warmed once, no document/orientation modules.
- Raw/normalized exact: Paddle-only 78/84 vs freshly rerun current routed 71/84.
  Six two-line entries plus one three-line entry were exact; current routed was 0/7.
  The 96-DPI and 144-DPI single-line sets each remained 7/7.
- Calibration-only single-line exactness regressed from 46/47 to 44/47: three new
  punctuation-width errors versus one repaired comma-space error. Overall calibration
  accuracy improved 46/52 -> 49/52 through multiline recovery. Existing Remote glyph
  errors persisted. No trust conclusions follow from these extraction scores.
- Paddle module pipeline total p50/p95: 13.3/32.5 ms, including line sorting/cropping
  and composition, excluding image decoding, artifact IO, IPC and startup. Current
  routed end-to-end OCR p50/p95: 52.0/142.0 ms, including IPC and secondary OCR; these
  are different timing boundaries. Cached startup/warmup: 2993.1/440.3 ms.
- Private `phase4.5-paddle-bubble/` contains per-line crops, box SVGs, full JSON/Markdown
  and same-fixture comparison with expected-label/crop-hash validation. Twenty-one
  Python tests passed. Production .NET code was
  not changed in this experiment; prior Windows tests are not a new production test.
- Router removal is deferred: segmentation is promising but single-line regression,
  Latin word-wrap ambiguity and limited paired-DPI multiline coverage need review.
  Trust calibration remains separate; no rec_score threshold or Adaptive veto was added.

The remaining real observer matrix and Phase 4 regression workflow stay paused. Phase
4.5 remains IN PROGRESS.

Unified extraction follow-up (benchmark only):

- Same immutable 84 entries / 76 distinct PNGs; labels, hashes, detected counts and
  line boxes checked against the prior experiment. One detector and one recognizer
  remain loaded, with extra direct whole-crop control calls excluded from timing.
- Zero/one detection uses original raw whole-bubble recognition; two or more retains
  the prior line-crop algorithm. Main and quote samples stay separate.
- Unified exact: 79/84 overall, 46/47 calibration single-line, 7/7 multiline/quote,
  7/7 each at 96/144 DPI, 16/18 punctuation, 62/65 Chinese, 6/8 English, 9/9 mixed,
  2/2 digits/other. Raw and normalized exact counts are equal. Language cohorts use
  CJK/Latin-letter presence; punctuation overlaps language cohorts.
- Three punctuation-width cases improve over line-rec; two English comma-space
  cases regress relative to line-rec. Zero regressions relative to routed or raw
  single-line control; the remaining three errors are existing Remote glyph errors
  (including one aliased crop). No evaluation normalization was relaxed.
- Unified p50/p95: 14.0/33.3 ms, excluding input decoding, IPC, artifact IO, startup
  and control calls. Startup/warmup: 3107.7/417.6 ms. These are local benchmark timings,
  not production observer latency. Each sample follows a raw-recognition control call,
  which may warm shape-specific caches despite its excluded timing. Zero detection did
  not occur in this corpus.
- Twenty-five Python tests pass, including raw-pixel identity for zero/one detection
  and equality with existing multiline crop extraction. Production .NET/worker/trust
  code is unchanged. Recommend the Unified architecture for subsequent implementation;
  do not infer semantic readiness from this extraction benchmark.

Stop for review; Phase
4.5 is not PASS and Phase 5 must not begin.

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
