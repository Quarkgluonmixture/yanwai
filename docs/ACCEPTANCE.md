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

The Phase 2 human exit gate is satisfied. Phase 3 remains out of scope for this
branch.

---

## Phase 3 — OCR

### Goal

Extract text from individual detected message crops.

### Acceptance

- [ ] `IOcrEngine` abstraction exists.
- [ ] OCR runs on bubble crops, not entire dual-monitor desktop.
- [ ] Simplified Chinese short text works on representative samples.
- [ ] Long wrapped Chinese text works on representative samples.
- [ ] Mixed Chinese/English is tested.
- [ ] OCR result exposes confidence if available.
- [ ] Low-confidence text is surfaced as uncertain/skipped rather than silently trusted.
- [ ] Emoji-only/sticker/image messages may return "unsupported/skip" in V0.
- [ ] Quoted reply structure is either extracted or explicitly marked unsupported; do not silently merge quote and current text as if they were one sentence.

### Evaluation artifact

Provide a small table/console report:

```text
fixture | expected | recognized | confidence | elapsed_ms
```

---

## Phase 4 — New-message observer and conversation state

### Goal

Process new/changed messages once, not every frame.

### Acceptance

- [ ] Lightweight change detection avoids unnecessary OCR on unchanged frames.
- [ ] A visible message persisting across frames does not trigger repeated OCR/Jev work.
- [ ] A newly appearing remote text message produces one normalized `ChatMessage`.
- [ ] Recent-message state is kept in memory.
- [ ] Scrolling does not cause every old message to become "new" under normal conditions.
- [ ] Switching conversation clears/reconciles state rather than mixing two contacts.
- [ ] No raw conversation persistence by default.
- [ ] Timings/counts are instrumented.

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
