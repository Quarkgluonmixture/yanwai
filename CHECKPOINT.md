# CHECKPOINT — 言外 / Yanwai

> 下一个 session 从这里接手。阶段的唯一真相是 `docs/ACCEPTANCE.md`，此处只放一行 cursor。
> 动手前先读 `docs/GOTCHAS.md`。

## 这是什么

盯住桌面微信的会话窗口，对方每来一条消息就用 TypeSafe Jev 判定若干个窄问题
（话里有话吗 / 她要什么 / 现在该怎么回 / 危险等级），把结果显示在面板里，
并在每条对方消息旁边贴一张「Jev：」批注卡（放不下时退成一行小标签）。

**只观察、只展示**——不注入微信、不自动回复、不碰本地加密库。

Fork 自 `Wionerlol/wechat-jev-hud`，`upstream` remote 只读。

## 阶段 cursor

**Phase 6 进行中**：锚定、跟随、滚动、一屏多卡都已写并在真机上粗看过（用户：滚动「大概跟得上」）；
fallback 防护与跨屏 / DPI 没真机验过。Phase 5（Jev 接入）与 Phase 1–4 已通过。
每一阶段的验收条目在 `docs/ACCEPTANCE.md`，⛔ 别在这里复制。

## 跑起来

```powershell
.\scripts\install-dotnet-sdk.ps1    # 只在私有 SDK 不存在时
.\scripts\build.ps1
.\scripts\panel.ps1                 # 主程序
```

判定要 `TYPESAFE_API_KEY`（用户级环境变量，已设）。实时模式还要 OCR 模型：
`.\scripts\install-ocr-models.ps1` → PP-OCRv6 small（ONNX，钉 revision + 校验哈希）落在
`.ocr-cache\paddle\`；Tesseract 模型只剩 `--ocr-evaluate` 在用。

OCR 在真屏幕上的表现（不打印原文）：`src\Yanwai.Diagnostics\bin\...\Yanwai.Diagnostics.exe --ocr-compare-live`。

诊断：

```powershell
.\scripts\diagnose.ps1                 # 只读窗口信息
.\scripts\diagnose.ps1 -CaptureDetect  # 抓一帧 + 气泡检测（写 PNG，含聊天内容）
.\scripts\observe.ps1 -Seconds 30      # 实时观察器，只在内存里跑
```

浮层跟随行为（不调 Jev）：

```powershell
& "src\Yanwai.Panel\bin\Debug\net8.0-windows10.0.19041.0\Yanwai.Panel.exe" --overlay-demo
```

## 运行环境事实

| 事实 | 值 |
|---|---|
| SDK | `global.json` 钉 8.0.425；私有 SDK 在 `%LOCALAPPDATA%\Yanwai\dotnet`，**系统装的是 9.0.101，会被 global.json 拒绝** |
| 微信 | Weixin 4.x（`MMUIRenderSubWindow` 渲染子窗口），深色主题 |
| 抓帧 | 走 `RenderWindow`（离屏，不要求微信不被遮挡）；退化到 `VisibleDesktopFallback` 时才要求可见 |
| Jev | `https://api.typesafe.ai/v1/systemone`，模型 `jev-latest` → `jev-1.13.0` |
| 一次判定 | 5 个问题一次请求，约 700 ms，约 1150 input tokens |

## 现在的状态

- 用法：面板开「实时模式」→ 点「判定这一屏」（之后变成「滚到哪判到哪」，滚进来的对方消息自动判）。
  开启前就在屏幕上的消息**只有**这样才会被判（坑 4）。浮层只在微信在前台时显示（坑 5）。
- OCR = PP-OCRv6 small 主读 + Windows OCR 逐行核对（坑 10）。核不上的**照样判**，卡片顶上标
  「OCR 未核实，判的是：「…」」。瓶颈是核对方，不是 Paddle。
- 浮层卡片只放 3 项（话里有话 / 怎么回 / 危险等级）；心情和「她要什么」在面板。
- 问题集在 `src/Yanwai.TypeSafe/ConversationQuestionSet.cs` 一个文件里，改起来便宜。
- **没验过的**：跨显示器与 DPI 切换；fallback 帧防护；`--overlay-demo` 改走 board 后的样子。

## 未完的活

见 `TODO.md`。最要紧的：用户确认一条「OCR 未核实」卡片上的原文对不对 ⇒ 决定要不要换核对引擎。

## 链接

- 阶段验收 `docs/ACCEPTANCE.md` · 设计裁决 `docs/DECISIONS.md` · 契约 `docs/SPEC.md`
- 坑 `docs/GOTCHAS.md` · 历史与为什么 `LOG.md`
- 协作契约 `AGENTS.md`（Codex / Claude 都按它走）
