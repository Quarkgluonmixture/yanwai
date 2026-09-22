# AGENTS.md — 言外 / Yanwai

This repository is a Windows desktop companion/overlay project for WeChat.
Forked from `Wionerlol/wechat-jev-hud`; `upstream` is configured read-only.

**Read `docs/GOTCHAS.md` before touching code.** It is the live hazard list, not a
history: every entry is something that still bites and that will not announce itself
(no compiler error, no failing test, no visible symptom). When delegating to a
subagent, copy the relevant numbered entries into its brief — a subagent does not
inherit this session's context.

Session handover lives in `CHECKPOINT.md` (start here), `TODO.md` and `LOG.md`.

Before changing code, read these files in order:

1. `docs/VISION.md`
2. `docs/DECISIONS.md`
3. `docs/SPEC.md`
4. `docs/EXAMPLES.md`
5. `docs/ACCEPTANCE.md`
6. `docs/CONTEXT.md`

## Decision priority

When requirements conflict, use this order:

1. The user's latest explicit decision
2. `docs/DECISIONS.md`
3. `docs/VISION.md`
4. `docs/SPEC.md`
5. Implementation convenience

Do not silently preserve contradictory requirements. Surface the conflict and follow the higher-priority source.

## Implementation principles

- Build the smallest testable phase first. Do not jump ahead.
- The project is an **observer and visualization tool**, not an auto-reply bot.
- Do not inject into WeChat, patch `Weixin.dll`, write WeChat process memory, hook private protocols, or depend on decrypted local chat databases in V0.
- UI Automation was tested on the user's current WeChat and only exposed the outer render shell (`MMUIRenderSubWindowHW`). Do not design V0 around message-level UIA.
- Use Win32/window APIs for window identity, bounds, visibility, monitor/DPI, and lifecycle.
- Use visual capture for the chat region; detect message geometry visually; OCR only relevant message crops.
- Keep capture, detection, OCR, message-state, Jev inference, and overlay rendering behind separate interfaces/modules.
- Never hard-code absolute screen coordinates. All message coordinates must be relative to the current WeChat window/client/render surface and transformed explicitly to desktop coordinates.
- Multi-monitor and per-monitor DPI behavior are first-class requirements.
- Raw chat text is private. Do not persist it by default. Debug logging must be opt-in and redacted/minimized.
- The TypeSafe API key must never be committed or printed. Use an environment variable or local secret store.

## TypeSafe / Jev requirement

Install and use the official TypeSafe skill for this project:

```bash
npx skills add typesafe-ai/skills --skill typesafe-ai
```

Official skill:
- https://github.com/typesafe-ai/skills/blob/main/skills/typesafe-ai/SKILL.md
- raw: https://raw.githubusercontent.com/typesafe-ai/skills/main/skills/typesafe-ai/SKILL.md

The skill explicitly says the **live TypeSafe docs are the source of truth**. Before implementing or changing Jev integration, read:
- https://docs.typesafe.ai/llms.txt
- the current HTTP API or chosen SDK page
- State
- Noul / Choice / Score primitives
- Confidence
- the closest relevant cookbook/pattern

Do not invent version-dependent API details from memory.

## Phase discipline

Implement phases in `docs/ACCEPTANCE.md` in order.

For each phase:

1. Implement only what is needed for that phase.
2. Add automated tests where practical.
3. Add a visible/debug harness for anything that needs human inspection.
4. Run the tests.
5. Record concrete evidence of success/failure.
6. Stop and report the result before expanding scope if manual verification is required.

If a phase fails, diagnose the failure instead of compensating with unrelated complexity.

## Preferred default stack

Unless the repository already contains a different approved stack, use:

- Windows native companion app
- .NET 8
- WPF for the eventual overlay/debug UI
- Win32 interop for window tracking
- a replaceable capture abstraction
- a replaceable `IBubbleDetector`
- a replaceable `IOcrEngine`
- a replaceable `IJevClient`

The **capture/detection spike may use a simpler temporary implementation** if it materially speeds up validation. Do not let a temporary spike leak into architecture as an irreversible dependency.

The user's Codex normally runs from WSL. The application itself must run against the **Windows desktop session**, not as a Linux/WSL GUI process. It is fine for Codex to edit from WSL, but Windows-specific build/run/test commands may need to use Windows `dotnet` / PowerShell through WSL interop.

## Do not over-interpret conversation semantics

Prefer judgments about observable conversational behavior.

GOOD:
- "Does this message contain a direct request?"
- "Does the message refer to prior conversation context?"
- "Is a response explicitly expected?"
- "Which bounded speech-act category best fits this message?"

BAD:
- "Is this person lying?"
- "Is this person manipulative?"
- "Does this person love the user?"
- psychiatric/personality labels

Typed output guarantees an interface, not truth. Show uncertainty.

## Definition of done

The project is not complete merely because Jev returns JSON.

V0 is complete only when a new remote WeChat text message can flow through:

`WeChat window -> capture -> bubble detection -> OCR -> message state -> Jev -> anchored HUD`

with acceptable latency, correct multi-monitor/DPI behavior, no WeChat process modification, and no automatic reply/click behavior.
