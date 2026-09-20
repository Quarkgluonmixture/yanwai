# EXAMPLES.md

These examples communicate intent. They are not frozen Jev prompt text.

## 1. Product interaction

### GOOD

```text
Remote message:
[avatar] 都是磨合期了吗

HUD to the right:
┌─ Jev ──────────────┐
│ 疑问/确认      88% │
│ 期待回应        91% │
│ 依赖前文        79% │
└────────────────────┘
```

The user remains in control of what to say.

### BAD

```text
Remote message:
[avatar] 都是磨合期了吗

System:
"她其实在试探你。马上回复：……"
[自动发送]
```

Why bad:
- claims hidden intent too strongly;
- generates a behavioral directive;
- takes control of the conversation.

---

## 2. Judgment design

### GOOD — observable and bounded

```text
Does the current message explicitly ask a question?
Does it depend on prior context?
Does it contain a direct request?
Which speech-act option best fits?
How urgent is the requested response on defined levels?
```

### BAD — mind reading / personality labeling

```text
Is the person lying?
Is the person manipulative?
Does the person love the user?
What personality disorder does this indicate?
```

---

## 3. Shared state

### GOOD

```json
{
  "current_message": {
    "side": "remote",
    "text": "都是磨合期了吗"
  },
  "recent_messages": [
    {
      "side": "self",
      "text": "在坡她就说什么在磨合期了 现在应该都磨平了"
    },
    {
      "side": "remote",
      "text": "诶哟我去"
    }
  ]
}
```

Questions are evaluated against the same relevant state.

### BAD

```text
Send 5 unrelated API requests, each with different partial context,
then assume their probabilities are directly comparable.
```

---

## 4. Capture scope

### GOOD

```text
WeChat window rect
  -> chat ROI
      -> changed subregion
          -> one candidate bubble crop
              -> OCR
```

### BAD

```text
Screenshot entire dual-monitor desktop 10 times/sec
  -> OCR every pixel
  -> re-submit every visible message to Jev
```

---

## 5. Bubble geometry

### GOOD

Detector output:

```text
remote [x=101, y=622, w=148, h=42] detection_score=.94
remote [x=101, y=674, w=112, h=42] detection_score=.92
self   [x=642, y=350, w=281, h=46] detection_score=.97
```

Coordinates are capture-relative and later transformed to desktop/DIP space.

### BAD

```text
"Remote bubbles start around screen x=1040 on my current laptop."
```

Absolute desktop calibration is not portable across window moves, monitors, or DPI.

---

## 6. WeChat UIA findings

Observed accessibility shape was effectively:

```text
WeChat top-level window
  └─ Weixin / render shell
      └─ MMUIRenderSubWindowHW
```

### GOOD response to this fact

Use the shell/window for window-level tracking and use visual capture for message content.

### BAD response

Keep writing selectors such as:

```text
Find TextControl(Name="都是磨合期了吗")
```

and hope current WeChat eventually exposes it.

---

## 7. OCR behavior

### GOOD

```text
OCR confidence low
-> mark message uncertain
-> do not send to Jev or render a strong semantic HUD
```

### BAD

```text
OCR produced "都是磨台期了吗"
-> silently assume it is perfect
-> show confident semantic conclusions
```

---

## 8. Duplicate suppression

### GOOD

A message stays visible for 20 frames, but semantic inference runs once unless its recognized content materially changes.

### BAD

20 visible frames -> 20 OCR calls -> 20 Jev calls -> 20 HUD recreations.

---

## 9. Multi-monitor

### GOOD

```text
WeChat moved from laptop monitor to external monitor
-> window event
-> refresh monitor + DPI + bounds
-> update capture source
-> re-anchor HUD
```

### BAD

```text
if x > 1920:
    ...
```

No fixed assumption about monitor topology.

---

## 10. HUD density

### GOOD collapsed HUD

```text
疑问/确认      88%
期待回应        91%
依赖前文        79%
```

### BAD collapsed HUD

A 300-word model explanation covering the chat.

---

## 11. Jev failure

### GOOD

```text
Perception still works.
Jev request times out.
HUD semantic rows disappear/show "analysis unavailable".
No crash and no retry storm.
```

### BAD

```text
Jev unavailable
-> block WeChat capture loop
-> overlay freezes
-> app crashes
```

---

## 12. Visual reference

See:

`docs/assets/wechat-dark-layout-reference.png`

Key characteristics in the user's real UI:
- dark theme;
- remote bubbles left, dark gray;
- self bubbles right, green;
- centered time labels;
- quoted reply blocks are present;
- large horizontal space to the right of remote bubbles;
- WeChat is commonly placed on the right side of the laptop display.
