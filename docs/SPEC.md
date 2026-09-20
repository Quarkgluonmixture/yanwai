# SPEC.md

## 1. System overview

Target flow:

```text
WeChat top-level window
        |
        v
WindowTracker
        |
        v
FrameCapture
        |
        v
ChatRegion / ChangeDetector
        |
        v
BubbleDetector
        |
        v
OCR (new/relevant bubble crop only)
        |
        v
MessageNormalizer + ConversationState
        |
        v
JevClient
        |
        v
JudgmentComposer
        |
        v
Overlay/HUD anchored to bubble coordinates
```

Each stage must be independently testable.

---

## 2. Runtime environment

Primary target:
- Windows desktop
- current Windows desktop WeChat 4.x family
- user uses a laptop display plus an external monitor
- WeChat is commonly placed on the right side of the laptop display
- dark theme is the initial real-world visual target

Do **not** encode "right side of laptop screen" as product logic.

### WSL development constraint

Codex commonly runs inside WSL.

Native WeChat observation and overlay execution must run in the Windows desktop session. Source editing can happen from WSL, but Windows-specific runtime tests must use Windows execution (for example Windows `dotnet`/PowerShell via interop).

---

## 3. Recommended project structure

A suggested .NET solution layout:

```text
src/
  WeChatJevHud.App/              # WPF host, composition root
  WeChatJevHud.Core/             # domain models and orchestration
  WeChatJevHud.Windows/          # Win32, monitor, DPI, window tracking
  WeChatJevHud.Capture/          # window/client frame capture
  WeChatJevHud.Vision/           # ROI, frame diff, bubble detection
  WeChatJevHud.Ocr/              # OCR abstraction + implementation(s)
  WeChatJevHud.TypeSafe/         # Jev client and typed judgment mapping
  WeChatJevHud.Overlay/          # overlay layout/anchoring
tests/
  WeChatJevHud.Core.Tests/
  WeChatJevHud.Windows.Tests/
  WeChatJevHud.Vision.Tests/
  WeChatJevHud.TypeSafe.Tests/
fixtures/
  screenshots/
```

This is a default, not a requirement if an equally modular structure already exists.

---

## 4. Core domain models

Use records/immutable types where practical.

### Rectangle

Maintain an explicit distinction between:
- capture pixels relative to WeChat surface;
- physical desktop pixels;
- WPF DIPs.

Do not pass anonymous `(x,y,w,h)` tuples across layers without declaring the coordinate space.

Example:

```csharp
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
```

### Message

Conceptual shape:

```csharp
public sealed record ChatMessage(
    string Id,
    MessageSide Side,
    string Text,
    PixelRect BubbleRect,
    string? QuotedText,
    PixelRect? QuotedRegion,
    DateTimeOffset ObservedAt,
    double OcrConfidence,
    bool IsVisible
);

public enum MessageSide
{
    Remote,
    Self,
    System,
    Unknown
}
```

Fields may evolve, but preserve the separation between:
- observed facts;
- inferred semantic judgments.

For a quoted reply, `QuotedRegion`/`QuotedText` are optional metadata associated
with the containing message. They are not independent message bubbles. Phase 2
does not detect that secondary region; Phase 3 may add it without changing the
meaning of `DetectedBubble.Bounds` as the main message-bubble bounds.

### Message identity

A message must not be re-submitted to Jev on every frame.

V0 can build a stable-enough visible-message identity from a combination of:
- side
- normalized OCR text
- quoted text if present
- temporal appearance/order
- approximate geometry only as a weak signal

Do not use raw `y` coordinate alone because messages move when the chat scrolls.

---

## 5. WeChat window tracking

### Responsibilities

`IWeChatWindowTracker` should provide:
- top-level WeChat HWND
- process identity
- visible/minimized state
- foreground relationship
- top-level/client/render bounds
- current monitor
- DPI scale
- location/size change notification

### Discovery hints

Observed in the user's environment:
- UIA exposes a `Weixin` outer element
- render shell includes `MMUIRenderSubWindowHW`

Community code for current WeChat often observes:
- process `Weixin.exe`
- Qt top-level window classes such as `Qt51514QWindowIcon`
- render child prefix `MMUIRenderSubWindow`

Treat exact class names as hints, not permanent API contracts.

Prefer:
1. process identity
2. top-level visible window
3. class/title heuristics
4. render child prefix as an optional aid

### Events

Prefer WinEvent hooks for lifecycle/location where reliable:
- foreground changes
- minimize start/end
- location changes

A low-frequency polling fallback is acceptable.

### Multi-monitor

Requirements:
- support virtual desktop coordinates including negative X/Y;
- use Per-Monitor DPI Awareness V2;
- explicitly convert physical pixels <-> WPF DIPs;
- recalculate after monitor or DPI change.

---

## 6. Frame capture

Define:

```csharp
public interface IWindowCapture
{
    CapturedFrame Capture(WeChatWindowSnapshot window);
}
```

Preferred production direction: Windows Graphics Capture or another Windows-native capture path that works with the user's visible WeChat render surface.

For Spike 1, a simpler visible-screen-region capture is acceptable if it gets to bubble detection faster.

### Important constraints

- Do not capture the whole virtual desktop if the WeChat render/chat region is known.
- The eventual HUD must not recursively pollute the captured frame. Use an exclude-from-capture mechanism where supported, or capture the underlying WeChat surface directly.
- Detect and handle minimized/invalid windows.

---

## 7. Chat region

The initial screenshot has:
- left navigation rail;
- conversation list;
- large right chat pane;
- header at top;
- input/composer region at bottom.

V0 may use calibrated/relative heuristics for the user's current layout.

Production rule:
- no absolute global pixel constants;
- derive ROI from current WeChat window/render bounds;
- store calibration as relative or structural measurements.

A temporary debug UI for adjusting the chat ROI is acceptable and may be useful.

Phase 2 uses a replaceable chat-region seam:

```csharp
public interface IChatRegionLocator
{
    DetectedChatRegion Locate(CapturedFrame frame);
}
```

The initial dark-theme adapter locates the conversation-list/chat divider, header
bottom, and composer top from long structural edges. Compact layouts without a
conversation list use the header geometry as a relative fallback. The returned
rectangle is always capture-relative; display resolution and desktop position are
not inputs.

---

## 8. Bubble detection

Define:

```csharp
public interface IBubbleDetector
{
    IReadOnlyList<DetectedBubble> Detect(CapturedFrame frame, PixelRect chatRegion);
}
```

Conceptual output:

```csharp
public sealed record DetectedBubble(
    PixelRect Bounds,
    MessageSide Side,
    double DetectionScore
);
```

`DetectionScore` is an uncalibrated heuristic quality/ranking score. It must not
be presented as a probability. Keep it distinct from a later OCR engine's
`OcrConfidence` and Jev's `Probability`/confidence values.

Initial visual scope:
- dark WeChat theme;
- self messages: green bubbles, right aligned;
- remote messages: dark gray bubbles, left aligned;
- centered gray text is typically time/system content;
- quoted replies have nested/secondary text regions.

Do not require a deep-learning detector for the first spike. Classical CV/connected components/edges/color/layout heuristics are acceptable if they meet the acceptance criteria.

The initial dark-theme detector uses connected bubble-color regions, rectangular
fill/shape, text-contrast evidence, and left/right anchoring. This deliberately
ignores unbacked timestamp text and obvious image/sticker regions. It may return
`Unknown` when a bubble-like text region is not convincingly anchored to either
side. Detection remains behind `IBubbleDetector`; no OCR participates in Phase 2.

### Debug mode

Must be able to render/export a debug frame containing:
- chat ROI
- one rectangle per detected bubble
- `remote/self/unknown`
- heuristic detection score
- capture-relative coordinates

This is required before OCR integration.

---

## 9. Change detection and new-message observation

The steady-state system should not OCR the whole chat at high frequency.

Target behavior:

```text
lightweight frame/ROI comparison
  -> no meaningful change: do nothing
  -> change detected:
       detect/reconcile visible bubbles
       OCR only new/changed candidate crops
       normalize/dedupe
       process newly observed remote message
```

The exact interval should be measured rather than assumed. A starting range around 5–10 lightweight checks per second is acceptable for experimentation, provided CPU usage is measured.

Scrolling must be treated differently from a genuinely new message where possible.

---

## 10. OCR

Define a replaceable interface:

```csharp
public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken ct);
}
```

V0 language needs:
- Simplified Chinese
- English
- mixed Chinese/English
- punctuation

Emoji-only/sticker/image messages may be skipped in V0 rather than misrepresented as text.

### OCR selection

Do not lock the architecture to one OCR engine before testing real crops.

Possible implementations can include a Windows-native OCR path or a local model/service such as PaddleOCR, but the choice must be based on accuracy/latency against fixture crops.

The first OCR milestone should compare at least representative:
- short remote text
- long wrapped text
- self text
- quoted reply
- mixed Chinese/English

Return OCR confidence when available.

---

## 11. Conversation state

Maintain a short in-memory window of recent normalized messages.

Conceptual state sent to Jev:

```json
{
  "current_message": {
    "side": "remote",
    "text": "都是磨合期了吗",
    "quoted_text": null
  },
  "recent_messages": [
    {"side": "self", "text": "在坡她就说什么在磨合期了 现在应该都磨平了"},
    {"side": "remote", "text": "诶哟我去"}
  ],
  "locale": "zh-CN"
}
```

Rules:
- send the minimum context needed;
- do not automatically upload the entire chat history;
- keep state ephemeral by default;
- distinguish observed text from Jev inference;
- invalidate/re-evaluate when underlying message text/context changes.

---

## 12. TypeSafe / Jev integration

### Skill and docs

Before coding, install:

```bash
npx skills add typesafe-ai/skills --skill typesafe-ai
```

Then read the live TypeSafe docs. The handoff intentionally does not freeze an API request schema because the official skill says current live docs are authoritative.

### Programming model

Use Jev as small semantic programming primitives:
- **Noul** for yes/no probability
- **Choice** for one outcome from a bounded set
- **Score** for ordered degree/intensity

Ask independent questions over the same state together when appropriate.

### Initial judgment set

Start small and observable.

#### Noul candidates

1. `expects_response`
   - Does the current remote message conventionally call for a response in this conversation?

2. `references_prior_context`
   - Does understanding the current message materially depend on prior conversation context?

3. `contains_direct_request`
   - Does the current message contain a direct request for the user to do/provide something?

4. `expresses_disagreement_or_correction`
   - Does the current message explicitly disagree with, correct, or challenge something in the recent context?

5. `contains_time_or_plan_commitment`
   - Does the current message propose, confirm, change, or constrain a time/plan/commitment?

#### Choice candidate: `speech_act`

Bounded options:
- question
- request
- answer
- acknowledgement
- clarification
- complaint_or_concern
- planning
- joke_or_banter
- information
- other

Include `other`; do not force a bad label.

#### Score candidates

Keep levels concrete.

`urgency`:
- 0: no timing pressure
- 1: mild preference for near-term response
- 2: clear promptness matters
- 3: immediate/near-immediate action or response is explicitly important

`emotional_intensity`:
- 0: neutral/low-affect
- 1: mild affect
- 2: clear strong affect
- 3: highly emphatic affect

Do not use emotional intensity as a mental-health diagnosis.

### UI use of probabilities

- Preserve probabilities/confidence in the UI.
- Do not convert uncertain judgments into categorical claims.
- Thresholds are product policy and must be validated on representative data.
- Typed output is not a truth guarantee.

### API key

Development:
- environment variable such as `TYPESAFE_API_KEY`, or the official SDK's current recommended secret mechanism.

Never:
- commit the key;
- print it;
- include it in screenshots;
- include it in exception telemetry.

---

## 13. Judgment composition

The code, not Jev, decides what to display.

Example policy:
- only show a Noul row when probability is far enough from indecision to be useful;
- always allow debug mode to show raw outputs;
- show at most a few high-signal rows in collapsed HUD;
- expanded HUD may show all configured judgments.

Avoid turning several weak signals into a strong psychological claim.

---

## 14. Overlay/HUD

### Window behavior

The overlay is a separate transparent companion window.

Requirements:
- topmost only as needed relative to WeChat;
- click-through when collapsed unless the user is interacting with it;
- hide when WeChat is minimized/not visible;
- default: hide when WeChat is not the foreground app, to avoid floating over unrelated apps;
- track move/resize/monitor/DPI changes.

### Anchoring

Default remote-message placement:

```text
hud.left = remoteBubble.right + gap
hud.top  = remoteBubble.top
```

Use collision resolution if there is insufficient space.

The current WeChat layout has substantial empty space to the right of remote bubbles, which is the preferred HUD area.

### Collapsed view

Initially show at most 2–4 concise rows, for example:

```text
询问/确认       88%
期待回应         91%
依赖前文         79%
```

### Expanded view

May show:
- all judgment outputs;
- raw probabilities;
- OCR confidence;
- pipeline timing in debug mode.

No generated reply is required for V0.

---

## 15. Privacy and logging

Default:
- in-memory short conversation buffer;
- no persistent raw chat logs;
- no screenshot persistence;
- no API payload persistence.

Debug fixture export must be explicit and user-triggered.

Diagnostics should prefer:
- timing
- dimensions
- counts
- hashes
- redacted snippets

over full chat contents.

---

## 16. Error handling

The UI must degrade gracefully.

Examples:
- WeChat not found -> idle/status, no crash
- WeChat minimized -> suspend capture/HUD
- capture fails -> retry with bounded backoff
- no bubble detected -> do nothing, debug trace
- OCR low confidence -> mark/skip instead of fabricating
- Jev unavailable -> keep perception running; show semantic layer unavailable
- API key missing -> explicit local configuration status, no crash
- overlay cannot anchor -> hide that HUD rather than covering random UI

---

## 17. Performance instrumentation

Measure at minimum:

```text
capture_ms
change_detect_ms
bubble_detect_ms
ocr_ms
jev_ms
render_ms
total_ms
```

Also track:
- capture/check frequency
- CPU usage during idle
- duplicate-message suppressions
- OCR confidence
- heuristic detection score

Do not optimize solely from synthetic benchmarks.
