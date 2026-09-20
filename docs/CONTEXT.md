# CONTEXT.md

## Origin of the idea

The user saw a concept image where small classifier panels appear beside WeChat messages and immediately connected it to Jev, TypeSafe's fast typed judgment model.

The desired experiment is not merely "classify a sentence." The interesting part is combining:
- live desktop perception;
- short conversation state;
- multiple fast typed judgments;
- spatially attached UI.

## TypeSafe readiness

The user already:
- has a TypeSafe account;
- created an API key.

Do not ask the user to paste the API key into chat or commit it.

The TypeSafe homepage recommended:

```text
Install the TypeSafe skill. If you're in Claude Code, run
`claude plugin marketplace add typesafe-ai/skills`, then
`claude plugin install typesafe@typesafe-ai`.
If you're in another agent, run
`npx skills add typesafe-ai/skills --skill typesafe-ai`
and select your agent.
Use one installation method.

Skill:
https://github.com/typesafe-ai/skills/blob/main/skills/typesafe-ai/SKILL.md
Raw:
https://raw.githubusercontent.com/typesafe-ai/skills/main/skills/typesafe-ai/SKILL.md
```

For Codex, use the "another agent" route.

The official skill was read during project design. Important guidance from it:
- live docs are source of truth;
- code owns workflow;
- Jev supplies typed common-sense judgments;
- Noul = yes probability;
- Choice = one bounded option/distribution;
- Score = ordered level;
- independent questions over shared state should be asked together when appropriate;
- probabilities/confidence should drive behavior;
- typed output guarantees interface, not truth.

The handoff creator could access the skill file from GitHub but could not fetch the live `docs.typesafe.ai` Markdown pages from the current chat environment. Codex must do that before API integration.

## WeChat observation performed

The user opened Accessibility Insights for Windows and inspected the real current desktop WeChat.

Result:
- the tool could see the overall WeChat/window shell;
- it could not inspect individual chat messages;
- the tree exposed `Weixin` / `MMUIRenderSubWindowHW` rather than per-message elements.

This is why normal UI Automation was rejected as the message-content channel.

Accessibility Insights does not need to remain open during development/runtime.

## User's real WeChat layout

Reference image:
`docs/assets/wechat-dark-layout-reference.png`

Characteristics:
- Windows desktop WeChat
- dark theme
- left navigation rail
- conversation list
- large chat pane
- remote messages: left aligned, dark gray bubbles, avatar on left
- self messages: right aligned, green bubbles, avatar on right
- centered timestamp/system labels
- quoted reply blocks occur
- large empty region to the right of many remote bubbles

This empty region motivated the right-of-remote-bubble HUD anchor.

## Hardware/display workflow

The user has:
- laptop display
- external monitor

Typical behavior:
- WeChat is placed on the entire/right side of the laptop display.

But the implementation must follow the WeChat window, not the user's current habit.

Multi-monitor/DPI behavior is part of V0 acceptance, not a later polish item.

## Development environment

The user commonly develops with:
- Windows host
- WSL Ubuntu
- Codex CLI
- project work under `~/projects`

This creates an important boundary:
- Codex may edit/build source from WSL;
- WeChat window APIs, capture, overlay, and GUI testing require the Windows desktop session.

Do not accidentally build a Linux-only/WSL GUI prototype and call it the real integration.

## Prior architectural direction

A Windows-native .NET 8 + WPF companion app was favored because the difficult engineering is:
- HWND/window tracking;
- per-monitor DPI;
- capture;
- transparent topmost overlay;
- lifecycle;
- coordinate transforms.

This is the preferred default but perception modules should be interface-driven so experiments can use other tools when useful.

## Why OCR/vision is acceptable

The visual channel is not intended to OCR the whole desktop continuously.

Desired steady state:

```text
known WeChat window
-> known chat ROI
-> cheap visual change check
-> candidate new/changed bubble
-> crop
-> OCR that crop
-> semantic inference only when needed
```

This keeps the design efficient and spatially grounded.

## Product tone

The HUD should be compact and factual. It should not pretend to explain people's hidden motives.

The key product distinction:

> "What observable conversational action is happening?"

rather than:

> "What is this person secretly thinking?"

## Open questions

These should not block Phase 0/1 unless they become relevant.

1. Best production capture backend:
   - Windows Graphics Capture is a strong candidate;
   - a simpler visible-region capture is acceptable for the first spike.

2. Best OCR engine:
   - choose after testing real WeChat crops;
   - keep an `IOcrEngine` abstraction.

3. Quoted reply parsing:
   - ideally represent `quoted_text` separately;
   - if V0 OCR cannot separate it robustly, explicitly mark the limitation.

4. HUD visibility when WeChat is visible but not foreground:
   - default to hiding to avoid overlaying unrelated applications;
   - user can later choose different behavior.

5. Dynamic Jev question selection:
   - interesting later;
   - first build uses a small stable judgment set.

6. Reply suggestions:
   - not V0;
   - can be a later opt-in feature after the judgment HUD proves useful.

## First task for Codex

Do **not** start with Jev.

Start with Phase 0 and Phase 1:
1. bootstrap the Windows-native solution;
2. find the real WeChat HWND;
3. report bounds/monitor/DPI;
4. capture one correct frame;
5. provide a simple debug artifact for human verification.

Only after this passes should Codex proceed to bubble detection.
