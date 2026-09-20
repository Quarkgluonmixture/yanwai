# VISION.md

## Why this project exists

The project explores a very specific use of TypeSafe's Jev model: make fast, typed semantic judgments over a live human conversation and surface those judgments directly beside the conversation as a lightweight visual HUD.

The user wants to "play with" Jev as a very fast general-purpose classifier/judgment model in a real application rather than as an isolated API demo.

The target experience is a companion layer floating beside the Windows desktop WeChat chat window. When the other person sends a message, the system quickly detects it, understands the recent context, asks Jev a small set of focused questions, and shows probabilistic judgments beside that message.

## Product vision

The product should feel like:

> A real-time conversation intelligence HUD attached to WeChat.

It should **not** feel like:

> An AI taking control of the conversation or pretending to know another person's inner thoughts.

The underlying design philosophy is:

- observe first;
- represent uncertainty;
- decompose semantic understanding into narrow typed judgments;
- let code own the workflow;
- let the human own the response.

## Desired experience

A remote message appears:

```text
[avatar] 都是磨合期了吗     ┌─ Jev ───────────────┐
                           │ 疑问/确认       88% │
                           │ 期待回应         91% │
                           │ 依赖前文         79% │
                           └───────────────────┘
```

The original WeChat UI is not modified. The HUD is a separate transparent companion window positioned from the detected message geometry.

The user can continue using WeChat normally. No automatic message is sent.

## What matters most

### 1. Fast enough to feel live

The semantic overlay should appear shortly after a new message becomes visible. The engineering target is to measure each stage separately:

- capture
- change detection
- bubble detection
- OCR
- Jev
- render
- total end-to-end latency

Optimization should be evidence-driven.

### 2. The overlay belongs to the message

The HUD must track the actual message position, not a fixed screen position. If WeChat moves, resizes, changes monitor, changes DPI, or the chat scrolls, the HUD should remain spatially coherent.

### 3. Judgments, not fake certainty

Jev is useful because it returns typed answers and probabilities. The product should preserve that uncertainty instead of converting everything into categorical psychological claims.

### 4. Privacy-aware by construction

Only the context needed for a judgment should be sent to Jev. Full local chat history should not be uploaded just because it is available. Raw chat content should not be persistently logged by default.

### 5. Replaceable perception pipeline

WeChat is not a stable public UI API. The project should isolate:
- window tracking
- frame capture
- bubble detection
- OCR

so one component can be replaced without rewriting the semantic or HUD layers.

## V0 product boundary

V0 is read-only/observational.

Included:
- locate WeChat
- observe visible chat area
- detect remote/self text message bubbles
- OCR visible/new text messages
- maintain a short recent conversation state
- run focused Jev judgments
- show an anchored HUD
- show confidence/probability
- debug latency and perception

Excluded:
- automatic replies
- generated replies as the primary feature
- automatic clicks
- automatic sending
- process injection
- WeChat binary patching
- private protocol hooking
- local database decryption as a dependency
- broad long-term relationship profiling
- personality/mental-health/lie/manipulation labeling

## Longer-term possibilities, not V0 commitments

Potential later experiments:
- dynamic judgment sets selected from message context
- alternative model comparison (Jev vs small LLM vs local classifier)
- calibration benchmark on user-labeled conversation examples
- hover-to-expand details
- per-contact optional preferences
- generated reply suggestions as a separate opt-in layer

Do not implement these until V0 works and the user explicitly chooses to expand scope.
